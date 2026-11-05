#!/usr/bin/env bash
set -euo pipefail

action="${1:-}"
shift || true

confirm_data_removal=false
reset_mode=false
llm_provider='auto'
llm_provider_explicit=false
llm_endpoint=''
llm_model=''
llm_api_key=''

for arg in "$@"; do
  case "$arg" in
    --confirm-data-removal)
      confirm_data_removal=true
      ;;
    --reset)
      reset_mode=true
      ;;
    --llm-provider=*)
      llm_provider="${arg#*=}"
      llm_provider_explicit=true
      ;;
    --llm-endpoint=*)
      llm_endpoint="${arg#*=}"
      ;;
    --llm-model=*)
      llm_model="${arg#*=}"
      ;;
    --llm-api-key=*)
      llm_api_key="${arg#*=}"
      ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"

if [[ "$action" != "new-build" && "$action" != "rebuild-all" && "$action" != "delete-all" && "$action" != "status" && "$action" != "bootstrap-dev" ]]; then
  printf 'Usage: %s {new-build|rebuild-all|delete-all|status|bootstrap-dev} [--confirm-data-removal] [--reset] [--llm-provider=auto|disabled|podman-ai-lab|ollama|openai-compatible] [--llm-endpoint=URL] [--llm-model=NAME] [--llm-api-key=KEY]\n' "$0" >&2
  exit 2
fi

if [[ ( "$action" == "rebuild-all" || "$action" == "delete-all" ) && "$confirm_data_removal" != true ]]; then
  echo 'This operation removes local task data. Rerun with --confirm-data-removal after confirming.' >&2
  exit 2
fi

if ! command -v podman >/dev/null 2>&1; then
  echo 'Podman CLI is not installed. Install Podman Desktop, then rerun this command.' >&2
  exit 1
fi

cd "$repository_root"

machines="$(podman machine list --format '{{.Name}} {{.Running}}')"
if [[ -z "$(printf '%s\n' "$machines" | awk 'NF { print; exit }')" ]]; then
  if [[ "$action" == "status" || "$action" == "delete-all" ]]; then
    echo 'No Podman machine exists; there is nothing to inspect or delete.'
    exit 0
  fi
  podman machine init --now --update-connection --cpus 4 --memory 4096 --disk-size 100
elif ! printf '%s\n' "$machines" | awk '$2 == "true" { found = 1 } END { exit !found }'; then
  podman machine start
fi

cgroup_path='/sys/fs/cgroup/non-systemd/cgroup.subtree_control'
cgroup_state="$(podman machine ssh -- cat "$cgroup_path" 2>/dev/null || true)"
if [[ -n "$cgroup_state" && ! " $cgroup_state " =~ [[:space:]]pids[[:space:]] ]]; then
  podman machine ssh -- sudo sh -c "echo +pids > $cgroup_path"
fi

podman compose version >/dev/null

get_api_base_url() {
  local candidate='http://localhost:8080'
  local machine_ip

  if curl --silent --fail --max-time 2 "$candidate/health" >/dev/null; then
    printf '%s' "$candidate"
    return
  fi

  machine_ip="$(podman machine ssh -- ip -o -4 addr show dev eth0 2>/dev/null | awk '{ for (i = 1; i <= NF; i++) if ($i == "inet") { split($(i + 1), address, "/"); print address[1]; exit } }')"
  if [[ -n "$machine_ip" ]]; then
    candidate="http://$machine_ip:8080"
    if curl --silent --fail --max-time 2 "$candidate/health" >/dev/null; then
      printf '%s' "$candidate"
      return
    fi
  fi

  printf '%s' "$candidate"
}

get_ai_lab_containers() {
  podman ps --format '{{.Names}} {{.Image}}' \
    | awk 'BEGIN{IGNORECASE=1} /podman-ai-lab|llama-stack|instructlab|llama\.cpp|ramalama-llama-server|ramalama/ { print $1 }'
}

get_ai_lab_container_name() {
  get_ai_lab_containers | head -n 1
}

connect_ai_lab_alias() {
  local network_name='enterprise_default'
  local alias_name='ai-lab'
  local container_name="${1:-}"
  local connect_output

  if [[ -z "$container_name" ]]; then
    container_name="$(get_ai_lab_container_name || true)"
  fi
  if [[ -z "$container_name" ]]; then
    return 1
  fi

  if connect_output="$(podman network connect --alias "$alias_name" "$network_name" "$container_name" 2>&1)"; then
    printf 'AI Lab alias is ready: http://%s:8000\n' "$alias_name"
    return 0
  fi

  if [[ "$connect_output" == *"already exists"* || "$connect_output" == *"already connected"* || "$connect_output" == *"name already in use"* ]]; then
    printf 'AI Lab alias already attached: http://%s:8000\n' "$alias_name"
    return 0
  fi

  printf 'AI Lab alias attach skipped: %s\n' "$connect_output" >&2
  return 1
}

disconnect_ai_lab_from_network() {
  local network_name='enterprise_default'
  local container_name
  local disconnect_output

  while IFS= read -r container_name; do
    [[ -n "$container_name" ]] || continue

    if disconnect_output="$(podman network disconnect "$network_name" "$container_name" 2>&1)"; then
      printf 'AI Lab container detached from %s before recreate: %s\n' "$network_name" "$container_name"
      continue
    fi

    if [[ "$disconnect_output" == *"is not connected"* || "$disconnect_output" == *"no such network"* || "$disconnect_output" == *"network not found"* || "$disconnect_output" == *"no static ips provided"* ]]; then
      continue
    fi

    printf 'AI Lab network detach skipped for %s: %s\n' "$container_name" "$disconnect_output" >&2
  done <<< "$(get_ai_lab_containers || true)"
}

select_llm_provider() {
  local ai_lab_available="$1"
  local options=(auto disabled)
  local selection
  local selected_index
  local index

  if [[ "$ai_lab_available" == true ]]; then
    options+=(podman-ai-lab)
  fi
  options+=(ollama openai-compatible)

  if [[ ! -t 0 ]]; then
    echo 'Input is redirected. Using default provider selection: auto.' >&2
    printf 'auto'
    return
  fi

  printf 'LLM provider selection:\n' >&2
  for index in "${!options[@]}"; do
    printf '[%s] %s\n' "$((index + 1))" "${options[$index]}" >&2
  done

  while true; do
    read -r -p "Select provider number (1-${#options[@]}, Enter=1): " selection
    if [[ -z "$selection" ]]; then
      printf '%s' "${options[0]}"
      return
    fi

    if [[ "$selection" =~ ^[0-9]+$ ]]; then
      selected_index=$((selection - 1))
      if (( selected_index >= 0 && selected_index < ${#options[@]} )); then
        printf '%s' "${options[$selected_index]}"
        return
      fi
    fi

    echo 'Invalid selection. Try again.' >&2
  done
}

select_ai_lab_container() {
  local containers_text="$1"
  local containers=()
  local container_name
  local selection
  local selected_index
  local index

  while IFS= read -r container_name; do
    [[ -n "$container_name" ]] && containers+=("$container_name")
  done <<< "$containers_text"

  if (( ${#containers[@]} == 0 )); then
    return
  fi
  if (( ${#containers[@]} == 1 )); then
    printf '%s' "${containers[0]}"
    return
  fi
  if [[ ! -t 0 ]]; then
    printf 'Input is redirected. Defaulting to the first detected AI Lab service: %s\n' "${containers[0]}" >&2
    printf '%s' "${containers[0]}"
    return
  fi

  printf 'Available Podman AI Lab model services:\n' >&2
  for index in "${!containers[@]}"; do
    printf '[%s] %s\n' "$((index + 1))" "${containers[$index]}" >&2
  done

  while true; do
    read -r -p "Select AI Lab service number (1-${#containers[@]}, Enter=1): " selection
    if [[ -z "$selection" ]]; then
      printf '%s' "${containers[0]}"
      return
    fi

    if [[ "$selection" =~ ^[0-9]+$ ]]; then
      selected_index=$((selection - 1))
      if (( selected_index >= 0 && selected_index < ${#containers[@]} )); then
        printf '%s' "${containers[$selected_index]}"
        return
      fi
    fi

    echo 'Invalid selection. Try again.' >&2
  done
}

get_model_id_from_endpoint() {
  local endpoint="$1"
  local payload

  if [[ -z "$endpoint" ]]; then
    return
  fi

  payload="$(podman run --rm --network enterprise_default curlimages/curl:8.10.1 -sS -m 20 "$endpoint/v1/models" 2>/dev/null || true)"
  if [[ -z "$payload" ]]; then
    return
  fi

  printf '%s' "$payload" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -n 1
}

get_model_id_from_host_port() {
  local container_name="$1"
  local port_output
  local port
  local payload

  if [[ -z "$container_name" ]]; then
    return
  fi

  port_output="$(podman port "$container_name" '8000/tcp' 2>/dev/null || true)"
  port="$(printf '%s\n' "$port_output" | sed -n 's/.*:\([0-9][0-9]*\)$/\1/p' | head -n 1)"
  if [[ -z "$port" ]]; then
    return
  fi

  payload="$(curl --silent --show-error --max-time 5 "http://localhost:$port/v1/models" 2>/dev/null || true)"
  printf '%s' "$payload" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -n 1
}

resolve_model_configuration() {
  local provider_selection="$1"
  local ai_lab_alias_ready="$2"
  local requested_endpoint="$3"
  local requested_model="$4"
  local requested_api_key="$5"
  local ai_lab_container_name="${6:-}"

  local provider="$provider_selection"
  local endpoint="$requested_endpoint"
  local model="$requested_model"
  local api_key="$requested_api_key"
  local message=''

  if [[ "$provider" == 'auto' ]]; then
    if [[ "$ai_lab_alias_ready" == 'true' ]]; then
      provider='podman-ai-lab'
    else
      provider='disabled'
    fi
  fi

  case "$provider" in
    disabled)
      endpoint=''
      model=''
      api_key=''
      message='.env updated with MODEL_PROVIDER=disabled.'
      ;;
    podman-ai-lab)
      if [[ -z "$endpoint" ]]; then
        endpoint='http://ai-lab:8000'
      fi

      if [[ -z "$model" ]]; then
        model="$(get_model_id_from_endpoint "$endpoint" || true)"
      fi
      if [[ -z "$model" ]]; then
        model="$(get_model_id_from_host_port "$ai_lab_container_name" || true)"
      fi

      if [[ -z "$model" ]]; then
        provider='disabled'
        endpoint=''
        model=''
        api_key=''
        message='.env created with MODEL_PROVIDER=disabled because podman-ai-lab model id could not be detected.'
      else
        message='.env updated for podman-ai-lab endpoint and selected model.'
      fi
      ;;
    ollama)
      if [[ -z "$endpoint" ]]; then
        endpoint='http://host.containers.internal:11434'
      fi

      if [[ -z "$model" ]]; then
        model='llama3.1'
      fi

      message='.env updated for ollama endpoint and selected model.'
      ;;
    openai-compatible)
      if [[ -z "$endpoint" ]]; then
        echo 'LlmProvider=openai-compatible requires --llm-endpoint.' >&2
        exit 2
      fi

      if [[ -z "$model" ]]; then
        echo 'LlmProvider=openai-compatible requires --llm-model.' >&2
        exit 2
      fi

      message='.env updated for openai-compatible endpoint and selected model.'
      ;;
    *)
      echo "Unsupported --llm-provider: $provider" >&2
      exit 2
      ;;
  esac

  printf '%s|%s|%s|%s|%s' "$provider" "$endpoint" "$model" "$api_key" "$message"
}

set_local_env_file() {
  local provider="$1"
  local endpoint="$2"
  local model="$3"
  local api_key="$4"
  local env_path="$repository_root/.env"

  cat > "$env_path" <<EOF
MODEL_PROVIDER=$provider
MODEL_ENDPOINT=$endpoint
MODEL_NAME=$model
MODEL_API_KEY=$api_key
MODEL_TIMEOUT_SECONDS=180
EOF
}

wait_control_plane() {
  local attempt
  local max_attempts=20
  local base

  for attempt in $(seq 1 "$max_attempts"); do
    base="$(get_api_base_url)"
    if curl --silent --fail --max-time 3 "$base/health" >/dev/null; then
      printf '%s' "$base"
      return 0
    fi
    sleep 2
  done

  return 1
}

invoke_smoke_test() {
  local base="$1"
  local create_response task_id approve_response state_response state

  create_response="$(curl --silent --show-error -X POST "$base/tasks" -H 'Content-Type: application/json' --data-raw '{"title":"LLM bootstrap smoke test","description":"kisa bir uygulama plani uret"}')"
  task_id="$(printf '%s' "$create_response" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"

  if [[ -z "$task_id" ]]; then
    echo 'Smoke test could not parse task id.' >&2
    return
  fi

  approve_response="$(curl --silent --show-error -X POST "$base/tasks/$task_id/approve")"
  printf 'Smoke test approve: %s\n' "$approve_response"

  for i in $(seq 1 20); do
    sleep 2
    state_response="$(curl --silent --show-error "$base/tasks/$task_id")"
    state="$(printf '%s' "$state_response" | sed -n 's/.*"status":"\([^"]*\)".*/\1/p')"
    if [[ "$state" == 'Completed' || "$state" == 'Failed' ]]; then
      printf 'Smoke test task: %s\n' "$task_id"
      printf 'Smoke test status: %s\n' "$state"
      printf '%s\n' "$state_response"
      return
    fi
  done

  echo 'Smoke test did not reach Completed/Failed within timeout.' >&2
}

case "$action" in
  new-build)
    disconnect_ai_lab_from_network
    podman compose down --remove-orphans
    podman compose up -d --build
    connect_ai_lab_alias || true
    printf 'Control Plane: %s\nTask data is preserved.\n' "$(get_api_base_url)"
    ;;
  rebuild-all)
    disconnect_ai_lab_from_network
    podman compose down --remove-orphans --rmi all --volumes
    podman compose build --no-cache
    podman compose up -d
    connect_ai_lab_alias || true
    printf 'Control Plane: %s\nLocal task data was removed and all project images were rebuilt without cache.\n' "$(get_api_base_url)"
    ;;
  delete-all)
    podman compose down --remove-orphans --rmi all --volumes
    echo 'This repository stack, its images, network, and data volumes were removed.'
    echo 'The Podman machine and unrelated Podman resources were left untouched.'
    ;;
  status)
    podman compose ps
    printf 'Control Plane: %s\n' "$(get_api_base_url)"
    ;;
  bootstrap-dev)
    if [[ "$reset_mode" == true ]]; then
      echo 'Reset mode enabled: local task data will be removed.'
      disconnect_ai_lab_from_network
      podman compose down --remove-orphans --rmi all --volumes
    else
      disconnect_ai_lab_from_network
      podman compose down --remove-orphans
    fi

    podman compose up -d postgres redis control-plane

    ai_lab_containers_text="$(get_ai_lab_containers || true)"
    ai_lab_containers=()
    while IFS= read -r container_name; do
      [[ -n "$container_name" ]] && ai_lab_containers+=("$container_name")
    done <<< "$ai_lab_containers_text"
    alias_ready='false'
    if (( ${#ai_lab_containers[@]} > 0 )); then
      alias_ready='true'
    fi

    if [[ "$llm_provider_explicit" != true ]]; then
      llm_provider="$(select_llm_provider "$alias_ready")"
    fi

    if [[ "$llm_provider" == 'auto' && ${#ai_lab_containers[@]} -gt 0 ]]; then
      llm_provider='podman-ai-lab'
    fi

    if [[ "$llm_provider" == 'podman-ai-lab' ]]; then
      selected_ai_lab_container="$(select_ai_lab_container "$ai_lab_containers_text")"
      if [[ -n "$selected_ai_lab_container" ]]; then
        disconnect_ai_lab_from_network
        alias_ready='false'
        if connect_ai_lab_alias "$selected_ai_lab_container"; then
          alias_ready='true'
        fi
      fi
    fi

    model_config="$(resolve_model_configuration "$llm_provider" "$alias_ready" "$llm_endpoint" "$llm_model" "$llm_api_key" "${selected_ai_lab_container:-}")"
    IFS='|' read -r resolved_provider resolved_endpoint resolved_model resolved_api_key resolved_message <<< "$model_config"

    set_local_env_file "$resolved_provider" "$resolved_endpoint" "$resolved_model" "$resolved_api_key"
    echo "$resolved_message"

    podman compose up -d --build

    if base_url="$(wait_control_plane)"; then
      printf 'Control Plane: %s\n' "$base_url"
    else
      echo 'Control Plane health check timed out.' >&2
      exit 1
    fi

    if [[ "$resolved_provider" != 'disabled' ]]; then
      invoke_smoke_test "$base_url"
    else
      echo 'Smoke test skipped because MODEL_PROVIDER is disabled.' >&2
    fi
    ;;
esac