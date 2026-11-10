# #83 — D0 discovery and D1 developer adapter

## Executive summary

Developer-only tooling for the local **ChatGPT ↔ Fusion** workflow, not an
end-user HIC capability. No product runtime, UI or release integration.
Disabled by default; explicit `prepare --enable-manual` is required.
No executor, scheduler, agent session, credentials or lifecycle operations.

**Proposed ADR-083-D0, not an accepted architecture decision:** use the
documented `vscode://agents/new` draft-only URL. Never infer a headless Fusion
start/completion API. A developer's optional editor must not become a cloud
product runtime dependency.

## Technical appendix

### Installed interface truth table — 10 November 2026

| Surface | Verified evidence | Decision / limit |
|---|---|---|
| VS Code | 1.145.2 stable, commit `c4453f8ea430795f941c1b2ff6c9321135a74a02` from shipped metadata | Read-only discovery |
| Bundled extension | `GitHub.copilot-chat` 0.73.0 | No distinct installed Fusion CLI/extension contract verified |
| `code chat --help` | `chat [options] [prompt]`, ask/edit/agent (agent default), add-file/new-window/reuse-window | Can run a prompt; **not used**. Process exit is not task completion |
| Standalone Copilot shim | Help returned CLI-not-found, exit 127 | No installation attempted; shim presence is not runtime readiness |
| `vscode://agents/new` | Official docs plus shipped `out/mainImpl.js` route parser: agents authority, /new path, prompt/workspace, draft inputText | URL-encoded prompt + workspace file URI; **does not submit**. Existing draft may be preserved |
| Extension commands/onUri | Contributions and activation exist | Not a documented external parameter/start/completion contract |
| External Fusion SDK/API | Not verified by this discovery | No private command, token, endpoint or WebSocket assumptions |
| Current chat's session/automation tools | Provider-mediated tooling is visible | Not evidence of an externally callable CLI interface; none invoked |
| Completion callback/MCP listener | No supported external contract verified | No listener, polling service or webhook |

Sources checked on 10 November 2026:
[CLI and draft URLs](https://code.visualstudio.com/docs/configure/command-line#_prepare-a-new-agent-session-draft),
[Agents window](https://code.visualstudio.com/docs/agents/run/agents-window).
[Fingerprints](../installed-interface.json) pin the inspected installation;
updates fail closed until reviewed. No credentials/settings or third-party
extension internals were read. **Live operator composer acceptance: not run.**
Static installed support is not proof of end-to-end human acceptance.

### Existing HIC authority mapping

- GitHub #75 owns ordered engineering instructions. A human-reviewed structured
  allowlist maps its order to existing task IDs; Issue bodies/titles/comments
  never become executable prompts. Public metadata revisions and branch ref
  SHAs are reconciled anonymously.
- `ControlPlane/Program.cs` GET `/tasks` and `/tasks/{id}` expose durable tasks.
  D1 accepts only an **unverified operator export** of id/status; there is no
  new task state machine, local execution cursor or automatic Task creation.
- `ExecutionOwnershipStore` owns executionId/workerId/generation and
  transactional claims. Claiming changes task state to Executing, so this
  developer tool **never calls that endpoint**.
- `TaskTransitionStore`, `task_executions`, `task_status_transitions` retain
  transition/audit authority. No approve/retry/status/DB writes.
- `AgentBridge/Contracts.cs` and `SnapshotProducer.cs` already bind snapshots
  to task/execution/correlation and source SHA, distinguishing caller
  attestation. Such artifacts are not authenticated completion callbacks.
  The publisher and credential broker are not used.
- Local Git plus GitHub branch refs bind a clean, separate worktree,
  repository, branch and exact SHA. A static draft URL cannot revalidate them
  atomically when clicked.
- PR base, specified tests and exact-head CI need an independent verifier.
  D1 does **not** implement completion verification; green CI/closed Issues
  do not authorize or prove execution. Reports always say
  `completionVerified=false`.

No verified automatic #75 Issue-to-HIC-task mapping/export adapter exists in
this baseline. Missing mappings block actual use; do not invent task IDs or
enable migrations to bridge the gap.

**Future contract, not implemented:** store queue/Issue revision, authorized
scope, predecessor, worktree/branch/base SHA, completion digest, PR/head/tests/CI
alongside the existing task/attempt/run identity and generation. Schema and
authorization changes require separate review. A verified immutable completion
must precede the next claim under the existing single ownership authority.
Unsigned, replayed or forged completion must not advance state.

### Threats and stop gates

| Threat | D1 mitigation / remaining boundary |
|---|---|
| Malicious Issue or shell/secret instructions | No Issue text interpolation; fixed template, strict schema, dev-tool-only paths |
| Wrong repo/branch/SHA/worktree | Clean separate worktree + public ref reconciliation, repeated baseline read |
| Credential escalation | No credential access; anonymous fixed-repository GET; 401/403 stop |
| Stale approval, host sleep, network failure | Five-minute window, bounded timeouts, no retries, explicit failures |
| Replay/duplicate/partial artifact | Digest and exclusive creation; no overwrite/repair. Not a task lease |
| Forged completion, CI absent/delayed/failed | No completion receiver or verifier; unknown completion, no auto-resume |
| Concurrent runners/restart | No running coordinator. Same export gives same draft; D2 ownership/recovery not delivered |
| Installed interface drift | Pinned fingerprints; fail closed |
| HTML injection | Escaped scriptless/resource-free HTML with CSP |
| Expired link / existing composer | Human rechecks; static URL is not an approval token and may not replace existing draft |
| Host/model approval policy | URL does not select/lock it; human checks before sending; prompt text is not enforcement |
| Third task requiring approval | Stop rather than skip; no auto-safe mode |

### Options, operation and cost

1. **Draft URI (proposed and implemented for D1):** local immutable inbox,
   no model call or service. Bounded public metadata reads only on explicit
   preparation; one link preloads a draft without cross-service prompt copying.
2. `code chat`/session dispatch can execute prompts; not selected.
3. Keyboard/UI scripts and private endpoints are unsupported/unsafe; rejected.
4. Native HIC executor remains out of scope until separate #63 isolation gates.

Tests demonstrate two successive synthetic **non-executable** handoffs,
restart from exported state and a third human stop in disposable Git storage.
This is not two real agent executions, a durable coordinator, or D2 acceptance.
The installed fingerprint check passed; no actual Agents link was opened or
sent. D2 claims/leases/completion/auto-resume and live ledger integration remain
open. See [README](../README.md) for CLI/schema and evidence boundaries.
