#!/usr/bin/env python3
"""Disposable CI regression for native ledger crash/restart durability.

An already committed claim must survive postgres restart and reject replay.
A killed in-flight transaction must leave no partial claim/audit. This is NOT
a license to blindly retry execution: the future runner must reconcile unknown
side effects before deciding what to do.
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
    raise SystemExit("Refusing test outside isolated GitHub Linux CI")

CONTAINER = f"hic-ledger-crash-ci-{os.getpid()}"
A = "30000000-0000-4000-8000-000000000201"
B = "30000000-0000-4000-8000-000000000202"
A_RUN = "30000000-0000-4000-8000-000000000301"


def command(*args: str, timeout: int = 35) -> str:
    result = subprocess.run(args, capture_output=True, text=True, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f"CI subprocess returned {result.returncode}: {Path(args[0]).name}")
    return result.stdout.strip()


def sql(statement: str, app: str = "hic-recovery-test") -> str:
    return command(
        "docker", "exec", "-e", f"PGAPPNAME={app}", CONTAINER,
        "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-U", "hic", "-d", "hic",
        "-c", statement, timeout=15
    )


def file_sql(sql_file: str) -> None:
    command(
        "docker", "exec", CONTAINER, "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1",
        "-U", "hic", "-d", "hic", "--single-transaction", "-f", sql_file, timeout=35
    )


def await_postgres(*, first_boot: bool = False) -> None:
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if first_boot:
            logs = subprocess.run(["docker", "logs", CONTAINER], text=True,
                                  capture_output=True, timeout=10, check=False)
            if "PostgreSQL init process complete" not in (logs.stdout + logs.stderr):
                time.sleep(0.4)
                continue
        try:
            if sql("SELECT 1;") == "1":
                return
        except (RuntimeError, subprocess.TimeoutExpired):
            pass
        time.sleep(0.35)
    raise AssertionError("PostgreSQL was not ready after start/crash recovery")


def claim_stmt(approval_id: str) -> str:
    return (
        "SELECT coalesce(hic_try_claim_native_readonly_run("
        f"'{approval_id}'::uuid, gen_random_uuid(), repeat('a',64))::text,'DENIED');"
    )


def await_uncommitted_insert(app: str) -> None:
    deadline = time.monotonic() + 9
    while time.monotonic() < deadline:
        count = sql(
            "SELECT count(*) FROM pg_stat_activity "
            f"WHERE application_name='{app}' AND state='active' AND wait_event='PgSleep';"
        )
        if count == "1":
            return
        time.sleep(0.2)
    raise AssertionError("Interrupted transaction was not observed holding its uncommitted claim")


def main() -> None:
    ignored = {".git", ".local", "bin", "obj", "node_modules"}
    v3 = [p for p in ROOT.rglob("003-native-run-approval-ledger.sql")
          if not any(part in ignored for part in p.parts)]
    boot = [p for p in ROOT.rglob("000-bootstrap-v2.sql")
            if not any(part in ignored for part in p.parts)]
    if len(v3) != 1 or len(boot) != 1 or not (v3[0].parent / "002-execution-ownership.sql").exists():
        raise AssertionError("Cannot identify exactly one fresh v2 and manual-only v3 migration")
    fixture_dir = ROOT / "tests" / "security-contracts"
    in_flight: subprocess.Popen[str] | None = None
    try:
        command(
            "docker", "run", "-d", "--name", CONTAINER,
            "-e", "POSTGRES_USER=hic", "-e", "POSTGRES_DB=hic",
            "-e", f"POSTGRES_PASSWORD={secrets.token_hex(32)}",
            "-v", f"{v3[0].parent.resolve()}:/control-plane-migrations:ro",
            "-v", f"{boot[0].parent.resolve()}:/bootstrap:ro",
            "-v", f"{fixture_dir.resolve()}:/fixtures:ro",
            "postgres:16-alpine", timeout=60
        )
        await_postgres(first_boot=True)
        file_sql("/bootstrap/000-bootstrap-v2.sql")
        file_sql("/control-plane-migrations/003-native-run-approval-ledger.sql")
        file_sql("/fixtures/native-ledger-crash-seed.sql")

        # Case A: DB confirmed the committed claim, but a local runner loses its
        # response before acting. An R2/WSL restart must not produce a new claim.
        if sql(claim_stmt(A)) != A_RUN:
            raise AssertionError("First committed claim did not succeed")
        command("docker", "restart", "--time", "1", CONTAINER, timeout=45)
        await_postgres()
        if sql(claim_stmt(A)) != "DENIED":
            raise AssertionError("Committed grant could be claimed again after restart")
        print("PASS: committed claim and audit are durable across PostgreSQL restart", flush=True)

        # Case B: simulate power loss after the claim SQL has executed within a
        # transaction, but BEFORE the transaction's COMMIT. No native action runs.
        pgapp = "hic-uncommitted-claim"
        transaction = (
            "BEGIN; "
            f"SELECT hic_try_claim_native_readonly_run('{B}'::uuid,gen_random_uuid(),repeat('a',64)); "
            "DO $assert$ BEGIN "
            f"IF NOT EXISTS (SELECT 1 FROM native_run_claims WHERE approval_id='{B}'::uuid) "
            "THEN RAISE EXCEPTION 'Synthetic claim missing before crash'; END IF; "
            "END $assert$; "
            "SELECT pg_sleep(20); COMMIT;"
        )
        in_flight = subprocess.Popen(
            ["docker", "exec", "-e", f"PGAPPNAME={pgapp}", CONTAINER,
             "psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1",
             "-U", "hic", "-d", "hic", "-c", transaction],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
        )
        await_uncommitted_insert(pgapp)
        # Only this disposable CI container is killed; NEVER user's Podman.
        command("docker", "kill", "--signal=KILL", CONTAINER, timeout=20)
        _, _ = in_flight.communicate(timeout=12)
        if in_flight.returncode == 0:
            raise AssertionError("Uncommitted synthetic transaction somehow committed before crash")

        command("docker", "start", CONTAINER, timeout=30)
        await_postgres()
        file_sql("/fixtures/native-ledger-crash-assert.sql")
        print("PASS: interrupted uncommitted claim/audit rolled back after crash recovery", flush=True)
    finally:
        if in_flight is not None and in_flight.poll() is None:
            in_flight.kill()
            in_flight.communicate(timeout=5)
        subprocess.run(["docker", "rm", "-f", CONTAINER], capture_output=True,
                       text=True, check=False, timeout=25)


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, RuntimeError, subprocess.TimeoutExpired) as e:
        print(f"FAIL: {e}", file=sys.stderr)
        sys.exit(1)
