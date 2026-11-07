# HIC runtime safety boundary CI — structural regression check

This is an additive **source-level** check during the transition from agent-lab paths to a clean src/tests Enterprise solution. It locates the Native Host Program.cs by trusted identifying strings rather than assuming its directory name, and it looks for the exact current read-only boundaries:

- Native Host status explicitly reports taskExecutionEnabled=false.
- Kestrel binds to loopback rather than all interfaces; original token check, strict payload check, and request size bound remain.
- Native Host exposes only its existing POST /v1/actions/run endpoint, whose allowed action is workspace.inspect with exactly one action payload field.
- No production runtime path in src/agent-lab/scripts/deploy/tools invokes the **manual-only** schema v3 SQL migration or the otherwise unwired Control Plane signing primitive.

The scanner runs in a fresh GitHub-hosted runner and includes synthetic negative tests proving that it detects changes to these markers. It starts **no processes, containers, databases or Worker** and never accesses the user's PC.

## Important limits

Regex/source checks are *not* a full security analysis, authentication proof or a reliable way to detect all possible execution code. They can be fooled by dead code/comments or bypassed by code patterns they do not inspect. Real C# integration tests, origin/CSRF checks, signature verification, PostgreSQL atomic claims, code review, runtime deployment gates and explicit user approval remain necessary. A future reviewed authenticated execution feature will intentionally update both the source-level guard and its tests; the scanner must never be weakened merely to make CI green.

## Coordination with workspace consolidation

The guard is designed to tolerate directory moves so long as the same Native Host runtime and policy remain in the repository, which reduces friction while Fusion refactors project paths. It creates new files only and does not change the user's active Windows worktree, service ports or the existing R2 PostgreSQL database.
