#!/usr/bin/env bash
set -euo pipefail

action="${1:-}"
shift || true

confirm_data_removal=false
reset_mode=false

for arg in "$@"; do
  case "$arg" in
    --confirm-data-removal)
      confirm_data_removal=true
      ;;
    --reset)
      reset_mode=true
      ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"

if [[ "$action" != "new-build" && "$action" != "rebuild-all" && "$action" != "delete-all" && "$action" != "status" && "$action" != "bootstrap-dev" ]]; then
  printf 'Usage: %s {new-build|rebuild-all|delete-all|status|bootstrap-dev} [--confirm-data-removal] [--reset]\n' "$0" >&2
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

get_ai_lab_container_name() {
  podman ps --format '{{.Names}} {{.Image}}' \
    | awk 'BEGIN{IGNORECASE=1} /podman-ai-lab|llama-stack|instructlab|llama\.cpp|ramalama-llama-server|ramalama/ { print $1; exit }'
}

connect_ai_lab_alias() {
  local network_name='enterprise_default'
  local alias_name='ai-lab'
  local container_name
  local connect_output

  container_name="$(get_ai_lab_container_name || true)"
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

  container_name="$(get_ai_lab_container_name || true)"
  if [[ -z "$container_name" ]]; then
    return
  fi

  if disconnect_output="$(podman network disconnect "$network_name" "$container_name" 2>&1)"; then
    printf 'AI Lab container detached from %s before recreate.\n' "$network_name"
    return
  fi

  if [[ "$disconnect_output" == *"is not connected"* || "$disconnect_output" == *"no such network"* || "$disconnect_output" == *"network not found"* || "$disconnect_output" == *"no static ips provided"* ]]; then
    return
  fi

  printf 'AI Lab network detach skipped: %s\n' "$disconnect_output" >&2
}

get_ai_lab_model_id() {
  local payload
  payload="$(podman run --rm --network enterprise_default curlimages/curl:8.10.1 -sS -m 20 http://ai-lab:8000/v1/models 2>/dev/null || true)"
  if [[ -z "$payload" ]]; then
    return
  fi

  printf '%s' "$payload" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -n 1
}

set_local_env_file() {
  local model_id="${1:-}"
  local env_path="$repository_root/.env"

  if [[ -z "$model_id" ]]; then
    cat > "$env_path" <<EOF
MODEL_PROVIDER=disabled
MODEL_ENDPOINT=
MODEL_NAME=
MODEL_API_KEY=
MODEL_TIMEOUT_SECONDS=180
EOF
    echo '.env created with MODEL_PROVIDER=disabled because AI Lab model id could not be detected.' >&2
    return
  fi

  cat > "$env_path" <<EOF
MODEL_PROVIDER=podman-ai-lab
MODEL_ENDPOINT=http://ai-lab:8000
MODEL_NAME=$model_id
MODEL_API_KEY=
MODEL_TIMEOUT_SECONDS=180
EOF

  echo '.env updated for AI Lab endpoint and detected model.'
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

    podman compose up -d --build
    if connect_ai_lab_alias; then
      model_id="$(get_ai_lab_model_id || true)"
    else
      model_id=''
    fi

    set_local_env_file "$model_id"

    if base_url="$(wait_control_plane)"; then
      printf 'Control Plane: %s\n' "$base_url"
    else
      echo 'Control Plane health check timed out.' >&2
      exit 1
    fi

    if [[ -n "$model_id" ]]; then
      invoke_smoke_test "$base_url"
    else
      echo 'Smoke test skipped because AI Lab model id was not detected and provider was set to disabled.' >&2
    fi
    ;;
esac