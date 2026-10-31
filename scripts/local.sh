#!/usr/bin/env bash
set -euo pipefail

action="${1:-}"
confirmation="${2:-}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_dir/.." && pwd)"

if [[ "$action" != "new-build" && "$action" != "rebuild-all" && "$action" != "delete-all" && "$action" != "status" ]]; then
  printf 'Usage: %s {new-build|rebuild-all|delete-all|status} [--confirm-data-removal]\n' "$0" >&2
  exit 2
fi

if [[ ( "$action" == "rebuild-all" || "$action" == "delete-all" ) && "$confirmation" != "--confirm-data-removal" ]]; then
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
    | awk 'BEGIN{IGNORECASE=1} /podman-ai-lab|llama-stack|instructlab|llama\.cpp/ { print $1; exit }'
}

connect_ai_lab_alias() {
  local network_name='enterprise_default'
  local alias_name='ai-lab'
  local container_name
  local connect_output

  container_name="$(get_ai_lab_container_name || true)"
  if [[ -z "$container_name" ]]; then
    return
  fi

  if connect_output="$(podman network connect --alias "$alias_name" "$network_name" "$container_name" 2>&1)"; then
    printf 'AI Lab alias is ready: http://%s:8000\n' "$alias_name"
    return
  fi

  if [[ "$connect_output" == *"already exists"* || "$connect_output" == *"already connected"* || "$connect_output" == *"name already in use"* ]]; then
    printf 'AI Lab alias already attached: http://%s:8000\n' "$alias_name"
    return
  fi

  printf 'AI Lab alias attach skipped: %s\n' "$connect_output" >&2
}

case "$action" in
  new-build)
    podman compose down --remove-orphans
    podman compose up -d --build
    connect_ai_lab_alias
    printf 'Control Plane: %s\nTask data is preserved.\n' "$(get_api_base_url)"
    ;;
  rebuild-all)
    podman compose down --remove-orphans --rmi all --volumes
    podman compose build --no-cache
    podman compose up -d
    connect_ai_lab_alias
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
esac