#!/usr/bin/env python3
"""Fail-closed SOURCE-LEVEL regression guard for the current read-only HIC runtime.

This is a structural safety gate, NOT an authorization audit, taint analysis,
or proof that a new handler is safe. Reviewed feature activation requires new tests.
File discovery is content-based to survive agent-lab -> src refactors.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

SKIP_PARTS = {".git", ".local", "bin", "obj", "node_modules", ".venv", "dist"}
RUNTIME_ROOTS = {"agent-lab", "src", "scripts", "deploy", "tools"}
RUNTIME_EXTENSIONS = {".cs", ".ps1", ".cmd", ".yaml", ".yml"}


def source_files(root: Path, *, extension: str | None = None) -> list[Path]:
    result = []
    for path in root.rglob("*"):
        if not path.is_file() or any(part in SKIP_PARTS for part in path.relative_to(root).parts):
            continue
        if extension is not None and path.suffix != extension:
            continue
        result.append(path)
    return result


def inspect_repo(root: Path) -> list[str]:
    errors: list[str] = []
    programs = []
    for path in source_files(root, extension=".cs"):
        if path.name != "Program.cs":
            continue
        body = path.read_text(encoding="utf-8", errors="replace")
        if "HIC_NATIVE_TOKEN" in body and '"hic-native-host"' in body:
            programs.append((path, body))
    if len(programs) != 1:
        return [f"Expected exactly one HIC Native Host Program.cs, found {len(programs)}"]

    program_path, program = programs[0]
    policies = [
        path for path in source_files(root, extension=".cs")
        if path.name == "NativeActionPolicy.cs"
    ]
    if len(policies) != 1:
        errors.append(f"Expected exactly one NativeActionPolicy.cs, found {len(policies)}")
    else:
        policy = policies[0].read_text(encoding="utf-8", errors="replace")
        required_policy = {
            "allowlisted workspace action": r'internal\s+const\s+string\s+Action\s*=\s*"workspace\.inspect"\s*;',
            "single-field payload": r'count\s*>\s*1',
            "exact action match": r'GetString\(\)\s*!=\s*Action',
            "exactly one payload field": r'return\s+count\s*==\s*1\s*;',
        }
        for label, pattern in required_policy.items():
            if not re.search(pattern, policy):
                errors.append(f"Native Action Policy lacks {label}")

    checks = {
        "runtime execution-disabled flag": r'taskExecutionEnabled\s*=\s*false\b',
        "loopback-only listener": r'kestrel\.Listen\s*\(\s*IPAddress\.Loopback\s*,',
        "ephemeral native token authentication": r'NativeActionPolicy\.ValidToken\s*\(',
        "strict native action payload validation": r'NativeActionPolicy\.AllowedPayload\s*\(',
        "bounded request body": r'MaxRequestBodySize\s*=\s*1024\s*;',
    }
    for label, pattern in checks.items():
        if not re.search(pattern, program):
            errors.append(f"Native Host Program.cs lacks {label}")

    routes = re.findall(r'\bapp\.MapPost\s*\(\s*"([^"]+)"', program)
    if routes != ["/v1/actions/run"]:
        errors.append(f"Native Host POST route set changed; review required: {routes}")

    for path in source_files(root):
        relative = path.relative_to(root)
        if not relative.parts or relative.parts[0] not in RUNTIME_ROOTS:
            continue
        if path.suffix not in RUNTIME_EXTENSIONS:
            continue
        body = path.read_text(encoding="utf-8", errors="replace")
        if "003-native-run-approval-ledger.sql" in body:
            errors.append(f"Runtime references manual-only schema v3 migration: {relative.as_posix()}")
        if (path.suffix == ".cs" and path.name != "NativeApprovalReceiptV2Issuer.cs" and
            re.search(r'\bNativeApprovalReceiptV2Issuer\s*\.\s*Sign\s*\(', body)):
            errors.append(f"Unreviewed production approval issuer call site: {relative.as_posix()}")

    if program_path.relative_to(root).parts[0] not in RUNTIME_ROOTS:
        errors.append("HIC Native Host Program.cs unexpectedly outside production roots")
    return errors


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    problems = inspect_repo(root)
    if problems:
        for item in problems:
            print(f"FAIL: {item}", file=sys.stderr)
        return 1
    print("PASS: read-only Native Host boundary and manual-only v3/issuer wiring remain intact")
    return 0


if __name__ == "__main__":
    sys.exit(main())
