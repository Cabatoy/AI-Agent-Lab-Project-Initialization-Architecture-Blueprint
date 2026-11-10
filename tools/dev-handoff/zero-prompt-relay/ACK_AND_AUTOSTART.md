# Developer relay — GitHub acknowledgment and one-time Windows bootstrap

**Status: Draft PR, not installed or activated.** This adds an optional outbound transport to the receive-only #96 relay. It is for the **developer workstation only**, not the HIC product, customer UI, product Worker/executor, or remote command execution.

## What actually works / does not

- The existing #96 reader was independently tested on the developer PC: `observed=1, newlySeen=1, executionAllowed=false`. It wrote a **local** receipt only.
- This change adds an Issue #97 comment transport for **synthetic-ping** `seen-untrusted` receipts. The body contains only a public synthetic UUID and SHA256 digest plus an explicit `executionAllowed=false` statement. **It is not task execution or completion evidence.**
- The outbound publisher performs a fixed GET on [Issue #97](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/97) comments, rejects conflicting digest and uncertain pagination, then a single POST if missing. No redirects, no arbitrary API addresses, no arbitrary GitHub Issue comments as commands.
- Only comments whose GitHub API `user.login` is exactly `Cabatoy` are accepted for deduplication/conflict; a public stranger's comment cannot claim a synthetic receipt. After POST, the API-returned author, comment ID, and matching marker must be validated. **This is public commenter attribution, not a cryptographic HIC execution attestation.** A compromised owner account could still forge a transport receipt.
- It is **at least once**, not exactly once: a concurrent process may publish a duplicate comment because GitHub Issues comments do not provide an atomic compare-and-swap transaction. HIC's later durable ledger is still required.
- The token, if provisioned, is a **fine-grained PAT with only Issues: Read/Write on this one repository**. GitHub's Issues permission is repository-scoped, not restricted to Issue #97 by the server; code restricts the destination. This is a residual risk and a dedicated GitHub App/broker may later be preferable. No existing publisher token, GitHub Copilot token or cached Git credential is automatically reused.
- **No live token is in this repository, no token was installed, no outbound POST was made.** All PR tests use fake GitHub responses.

## One-time bootstrap, only AFTER Draft PR review and successful CI

When physically at the Windows desktop:

1. Create a short-lived, fine-grained personal access token via GitHub Settings → Developer settings → Personal access tokens → Fine-grained tokens. Repository access: **only `Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint`**. Repository permissions: **Issues Read/Write** only (GitHub adds read-only metadata). Do not permit Contents/Actions/Pull requests/Administration and never paste the token into ChatGPT, a GitHub Issue, a terminal history or a model prompt.
2. Update the **existing detached relay worktree** only if it is clean and the remote exact SHA is reviewed. Do not create another worktree on top of it, force-push, or overwrite the local directory.

```powershell
$relayRoot = Join-Path $env:LOCALAPPDATA 'HICDevRelay\source'
git -C $relayRoot status --short
git -C $relayRoot fetch origin feature/hic-dev-ack-channel
git -C $relayRoot switch --detach origin/feature/hic-dev-ack-channel
$script = Join-Path $relayRoot 'tools/dev-handoff/zero-prompt-relay/scripts/Run-DevRelay.ps1'
& $script -Mode Plan
& $script -Mode SetupAck
& $script -Mode AckOnce
```

Check the latest reviewed SHA before switching. `SetupAck` prompts **locally** and saves a DPAPI CurrentUser-encrypted secret under `%LOCALAPPDATA%\HICDevRelay\ack-token.dpapi`; do not synchronize that secret. `AckOnce` retrieves the existing synthetic local receipt and, only after authorization, attempts a comment to Issue #97. That receipt is public transport evidence, not an SDK run.

3. After a successful human-reviewed `AckOnce` test, enable a continuous **outbound-only** poller while Windows remains awake and the user session is active:

```powershell
& $script -Mode AckWatch
```

This foreground process polls every 10 minutes and needs no public inbound port or GitHub Actions self-hosted runner. For optional per-user **at-logon startup**, explicitly execute `& $script -Mode InstallAutostart` and type `ENABLE-HIC-ACK`. The scheduled task is set to run only when the current user logs on, and is not installed or started by this PR. It neither wakes a sleeping PC nor operates after Windows is shut down.

To stop the foreground poller, press Ctrl+C. To disable future autostart:

```powershell
Unregister-ScheduledTask -TaskName 'HIC-Developer-Relay-Ack-OptIn' -Confirm:$false
```

If the PAT expires/revokes, the publisher fails closed; it must not fall back to ambient Git/Copilot credentials. Removing `ack-token.dpapi` stops outbound ack after the current process has exited; ensure you have a backup plan before deliberately deleting personal secret material.

## Local branch and worktree cleanup inventory (read only)

Run with the **HIC source repo** as the last argument, not `Enterprise2`:

```powershell
node (Join-Path $relayRoot 'tools/dev-handoff/zero-prompt-relay/scripts/audit-local-git.mjs') C:\Enterprise
```

It reports active worktrees, upstream, ahead/behind commits and branch candidates. It **never fetches, pushes, merges, changes branch, deletes, prunes, modifies a worktree or accesses secrets**. No branch may be deleted just because it appears merged into `origin/develop`: open Draft PR ancestry and unpushed commits must be independently checked first. Protected product projects and `C:\Enterprise2` are outside scope.

## Remaining work for true mobile-to-local automatic coding

1. **P0 transport:** GitHub → local read-only receipt exists, local → GitHub synthetic ack can be activated after the operator provides a minimal Issues-write credential. No model or execution.
2. **P1:** Signed work orders, authoritative HIC ledger/lease/replay, scoped code-change permissions and independent GitHub commit/PR/CI verifier.
3. **P2:** Proven OS/container sandbox (#92 remains `UNVERIFIED`), real Copilot SDK authentication/entitlement/quota and controlled egress, one docs-only one-shot model run with human approval.
4. **P3:** Bounded automatic continuation of **allowlisted** tasks and Draft PR creation with separate scoped publisher; no automatic merge/deploy or privileged changes.

The operator must complete the one-time local setup. A cloud ChatGPT session cannot install or launch software on this Windows host merely by writing a GitHub issue. **Humans are not message queues.**

**CI nesting note:** The parent #96 workflow must include `feature/hic-dev-proposal-watcher` as a permitted PR base branch so the #97 stacked review receives exact-head Windows/Linux checks. This is a CI trigger rule, not an execution permission.
