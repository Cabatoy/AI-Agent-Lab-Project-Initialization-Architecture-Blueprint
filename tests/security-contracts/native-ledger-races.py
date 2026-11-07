#!/usr/bin/env python3
"""CI-only PostgreSQL race tests for the unwired, manual-only v3 ledger.

Prove claim blocks behind concurrently held approval/task locks, then rejects
after revocation or stale revision commits. An unrelated valid grant must work.
No user Windows, Podman, databases, or real approvals are involved.
"""
from __future__ import annotations

import os
import secrets
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
if os.getenv("GITHUB_ACTIONS") != "true" or os.getenv("RUNNER_OS") != "Linux":
    raise SystemExit("Refusing to run outside an isolated GitHub Linux runner")

CONTAINER = f"hic-native-race-ci-{os.getpid()}"
REVOCATION_APPROVAL = "20000000-0000-4000-8000-000000000201"
REVISION_APPROVAL = "20000000-0000-4000-8000-000000000202"
CONTROL_APPROVAL = "20000000-0000-4000-8000-000000000203"
CONTROL_RUN = "20000000-0000-4000-8000-000000000303"


def run(*args: str, timeout: int = 25) -> str:
    result = subprocess.run(args, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        # This runner contains only synthetic fixture data. Print SQL ERROR lines,
        # never commands, connection strings, tokens, task content or passwords.
        safe_errors = [line.split("ERROR:", 1)[1].strip()[:160]
                       for line in result.stderr.splitlines() if "ERROR:" in line]
        phase = Path(args[-1]).name if args and args[-2:-1] == ("-f",) else "sql-client"
        raise RuntimeError(f"CI {phase} failed (code {result.returncode}; sql_errors={safe_errors[:3]})")
    return result.stdout.strip()


def psql(sql: str, *, app: str = "hic-race-inspect", timeout: int = 25) -> str:
    return run(
        "docker", "exec", "-e", f"PGAPPNAME={app}", CONTAINER,
        "psql", "-X", "-qAt", "-U", "hic", "-d", "hic",
        "-v", "ON_ERROR_STOP=1", "-c", sql, timeout=timeout
    )


def psql_file(path: str) -> None:
    run(
        "docker", "exec", CONTAINER, "psql", "-X", "-qAt", "-U", "hic", "-d", "hic",
        "-v", "ON_ERROR_STOP=1", "--single-transaction", "-f", path, timeout=35
    )


def wait_for_activity(app: str, condition: str, seconds: float = 9) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        query = (
            "SELECT count(*) FROM pg_stat_activity "
            f"WHERE application_name='{app}' AND state='active' AND {condition};"
        )
        if psql(query) == "1":
            return
        time.sleep(0.15)
    raise AssertionError(f"Expected lock contention was not observed for {app}")


def claim_sql(approval_id: str) -> str:
    return (
        "SELECT coalesce(hic_try_claim_native_readonly_run("
        f"'{approval_id}'::uuid, gen_random_uuid(), repeat('a',64))::text,'DENIED');"
    )


def race(kind: str, approval_id: str, holder_sql: str) -> None:
    holding = f"hic-holder-{kind}"
    claiming = f"hic-claim-{kind}"
    holder = subprocess.Popen(
        ["docker", "exec", "-e", f"PGAPPNAME={holding}", CONTAINER,
         "psql", "-X", "-qAt", "-U", "hic", "-d", "hic", "-v", "ON_ERROR_STOP=1",
         "-c", holder_sql], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
    )
    claimer = None
    try:
        wait_for_activity(holding, "wait_event='PgSleep'")
        claimer = subprocess.Popen(
            ["docker", "exec", "-e", f"PGAPPNAME={claiming}", CONTAINER,
             "psql", "-X", "-qAt", "-U", "hic", "-d", "hic", "-v", "ON_ERROR_STOP=1",
             "-c", claim_sql(approval_id)], stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True
        )
        # Verify real lock wait; a sequential test would prove much less.
        wait_for_activity(claiming, "wait_event_type='Lock'")
        _, _ = holder.communicate(timeout=25)
        result, _ = claimer.communicate(timeout=15)
        if holder.returncode != 0 or claimer.returncode != 0:
            raise AssertionError(f"{kind}: one of the concurrent DB clients failed")
        if result.strip() != "DENIED":
            raise AssertionError(f"{kind}: blocked claim unexpectedly succeeded")
    finally:
        for proc in (claimer, holder):
            if proc is not None and proc.poll() is None:
                proc.kill()
                proc.communicate(timeout=5)


def main() -> None:
    ignored = {".git", ".local", "bin", "obj", "node_modules"}
    migrations = [p for p in ROOT.rglob("003-native-run-approval-ledger.sql")
                  if not any(part in ignored for part in p.parts)]
    bootstraps = [p for p in ROOT.rglob("000-bootstrap-v2.sql")
                  if not any(part in ignored for part in p.parts)]
    if (len(migrations) != 1 or len(bootstraps) != 1 or
            not (migrations[0].parent / "002-execution-ownership.sql").exists()):
        raise RuntimeError("Canonical v2 and manual v3 SQL cannot be identified uniquely")
    fixtures = ROOT / "tests" / "security-contracts"
    try:
        run(
            "docker", "run", "-d", "--name", CONTAINER,
            "-e", "POSTGRES_USER=hic", "-e", "POSTGRES_DB=hic",
            "-e", f"POSTGRES_PASSWORD={secrets.token_hex(32)}",
            "-v", f"{migrations[0].parent.resolve()}:/migration:ro",
            "-v", f"{bootstraps[0].parent.resolve()}:/bootstrap:ro",
            "-v", f"{fixtures.resolve()}:/fixtures:ro",
            "postgres:16-alpine", timeout=60
        )
        ready = False
        for _ in range(90):
            log_probe = subprocess.run(["docker", "logs", CONTAINER], text=True,
                                       capture_output=True, timeout=10, check=False)
            if "PostgreSQL init process complete" in (log_probe.stdout + log_probe.stderr):
                try:
                    if psql("SELECT 1;", timeout=5) == "1":
                        ready = True
                        break
                except (RuntimeError, subprocess.TimeoutExpired):
                    pass
            time.sleep(0.4)
        if not ready:
            raise AssertionError("Disposable PostgreSQL did not finish startup")
        print("CI stage: fresh schema v2", flush=True)
        psql_file("/bootstrap/000-bootstrap-v2.sql")
        print("CI stage: isolated manual ledger v3", flush=True)
        psql_file("/migration/003-native-run-approval-ledger.sql")
        print("CI stage: synthetic approvals", flush=True)
        psql_file("/fixtures/native-ledger-races-seed.sql")
        print("CI stage: overlapping row-lock races", flush=True)

        race(
            "revoke", REVOCATION_APPROVAL,
            "BEGIN; UPDATE native_run_approvals "
            "SET state='revoked', revoked_at_utc=clock_timestamp() "
            f"WHERE id='{REVOCATION_APPROVAL}'; SELECT pg_sleep(14); COMMIT;"
        )
        race(
            "revision", REVISION_APPROVAL,
            "BEGIN; UPDATE tasks SET title='Changed during approval' "
            "WHERE id='20000000-0000-4000-8000-000000000102'; "
            "SELECT pg_sleep(14); COMMIT;"
        )

        if psql(claim_sql(CONTROL_APPROVAL)) != CONTROL_RUN:
            raise AssertionError("Valid positive control failed")
        if psql(claim_sql(CONTROL_APPROVAL)) != "DENIED":
            raise AssertionError("Replay of positive control was accepted")
        psql_file("/fixtures/native-ledger-races-assert.sql")
        print("PASS: revocation and version races blocked and denied, positive control single-use")
    finally:
        subprocess.run(["docker", "rm", "-f", CONTAINER], text=True,
                       capture_output=True, timeout=25, check=False)


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, RuntimeError, subprocess.TimeoutExpired) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        sys.exit(1)
