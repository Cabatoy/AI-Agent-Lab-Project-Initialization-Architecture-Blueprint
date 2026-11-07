#!/usr/bin/env bash
# Ephemeral GitHub CI ONLY. Never connect to Windows, user Podman, or R2 data.
set -Eeuo pipefail
if [[ "$GITHUB_ACTIONS" != "true" || "$RUNNER_OS" != "Linux" ]]; then
  echo "This test requires an isolated GitHub-hosted Linux runner" >&2
  exit 2
fi
root="$(git rev-parse --show-toplevel)"
cd "$root"
migration="$(find . -type f -path '*/Migrations/003-native-run-approval-ledger.sql' -print)"
if [[ "$(printf '%s\n' "$migration" | grep -c .)" -ne 1 ]]; then
  echo "Expected exactly one canonical ledger v3 migration" >&2
  exit 2
fi
migration_dir="$(realpath "$(dirname "$migration")")"
if [[ ! -f "$migration_dir/002-execution-ownership.sql" ||
      ! -f deploy/hic-managed/migrations/000-bootstrap-v2.sql ]]; then
  echo "Required canonical v2/bootstrap SQL is unavailable" >&2
  exit 2
fi
container="hic-ledger-concurrency-ci"
work="$(mktemp -d)"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
password="$(openssl rand -hex 32)"
echo "::add-mask::$password"
docker run -d --name "$container" \
  -e POSTGRES_USER=hic -e POSTGRES_DB=hic -e POSTGRES_PASSWORD="$password" \
  -v "$migration_dir:/control-plane-migrations:ro" \
  -v "$root/deploy/hic-managed/migrations:/bootstrap:ro" \
  -v "$root/tests/security-contracts:/security-contracts:ro" \
  postgres:16-alpine >/dev/null
unset password
ready=0
for i in $(seq 1 65); do
  if docker logs "$container" 2>&1 | grep -q 'PostgreSQL init process complete' &&
     docker exec "$container" psql -U hic -d hic -tAc "select 1" 2>/dev/null | grep -qx 1; then
    ready=1
    break
  fi
  sleep 1
done
if [[ "$ready" -ne 1 ]]; then
  echo "Final PostgreSQL post-init server was not ready" >&2
  exit 1
fi
docker exec "$container" psql -U hic -d hic -v ON_ERROR_STOP=1 \
  --single-transaction -f /bootstrap/000-bootstrap-v2.sql >/dev/null
docker exec "$container" psql -U hic -d hic -v ON_ERROR_STOP=1 \
  --single-transaction -f /control-plane-migrations/003-native-run-approval-ledger.sql >/dev/null
docker exec "$container" psql -U hic -d hic -v ON_ERROR_STOP=1 \
  --single-transaction -f /security-contracts/native-ledger-parallel-seed.sql >/dev/null
approval="10000000-0000-4000-8000-000000000201"
expected_run="10000000-0000-4000-8000-000000000301"
pids=()
for i in $(seq 1 24); do
  (
    timeout 40s docker exec "$container" psql -U hic -d hic -v ON_ERROR_STOP=1 \
      -tAc "SELECT coalesce(hic_try_claim_native_readonly_run('$approval'::uuid, gen_random_uuid(), repeat('a', 64))::text, 'DENIED');" \
      > "$work/claim-$i.out" 2> "$work/claim-$i.err"
  ) &
  pids+=("$!")
done
for pid in "${pids[@]}"; do
  if ! wait "$pid"; then
    echo "A parallel client failed or timed out; refusing to call test successful" >&2
    exit 1
  fi
done
success=0
denied=0
for i in $(seq 1 24); do
  result="$(tr -d '\r\n' < "$work/claim-$i.out")"
  case "$result" in
    "$expected_run") success=$((success + 1)) ;;
    "DENIED") denied=$((denied + 1)) ;;
    *) echo "Unexpected concurrent claim result" >&2; exit 1 ;;
  esac
done
if [[ "$success" -ne 1 || "$denied" -ne 23 ]]; then
  echo "Single-use claim failure: accepted=$success denied=$denied" >&2
  exit 1
fi
docker exec "$container" psql -U hic -d hic -v ON_ERROR_STOP=1 \
  -f /security-contracts/native-ledger-parallel-assert.sql >/dev/null
echo "PASS: 24 concurrent clients; exactly one claim, one audit, zero PUBLIC EXECUTE."
