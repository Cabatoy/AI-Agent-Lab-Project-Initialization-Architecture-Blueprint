# HIC Developer Proposal Relay — P0 receive-only

**Developer workstation only; not a HIC product feature.** This package reads an explicit **public GitHub** branch's **unsigned, non-executable** proposals and writes only local `seen-untrusted` receipts. It cannot send to Fusion, start an SDK/model session, execute files/commands, mutate HIC runtime/DB, commit, push, publish PRs, merge or deploy.

## Safety contract

- Explicit `--enable-read-only` opt-in. Nothing runs at startup or installation. No plugins, CLI auth, SDK, npm dependencies or GitHub token.
- **Only** `GET https://api.github.com/repos/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/contents/dev-work-orders/{inbox.json|orders/<uuid>.json}?ref=hic-dev-orders`; no user-controlled URLs, redirection, or write requests. Public API rate limits may apply.
- A strict bounded inbox (20 entries), SHA-256 raw content check, exact repository/source, expiry, replay protection and review-only validation. No GitHub Issue prose, PR titles, comments or unreviewed content can become an executable prompt.
- Synthetic ping has no HIC task ID. Review-only proposals do not grant authority and are always recorded as `executionAllowed:false`.
- Receipts live in the operator's **local** `~/.hic-dev-relay/acks/`. They do **not** leave the machine. They are not a durable HIC task/attempt ledger, remote acknowledgment or proof of model execution. A subsequent independently approved GitHub ack broker is still required for *zero report relay*.
- One poll per 10 minutes in watch mode; previously acknowledged entries do not refetch the full order. Stop with Ctrl+C.
- This branch is a Draft PR; review and CI before local acceptance. Never use a real HIC production task while the receive-only boundary is unproven.

## Offline tests

From `tools/dev-handoff/zero-prompt-relay`:

```powershell
npm run check
```

Tests use fake GitHub responses and disposable local receipts. They never call the network or model.

## One-time operator bootstrap (after PR CI review)

Open PowerShell **from any HIC repository checkout** (not from the Copilot SDK/sandbox worktree). These commands fetch only the verified developer-tool branch and create a **separate**, detached worktree; they do not alter your active branch, runtime, database, protected volumes or Podman.

```powershell
git fetch origin feature/hic-dev-proposal-watcher
$relayParent = Join-Path $env:LOCALAPPDATA 'HICDevRelay'
New-Item -ItemType Directory -Force -Path $relayParent | Out-Null
$relayRoot = Join-Path $relayParent 'source'
git worktree add --detach $relayRoot origin/feature/hic-dev-proposal-watcher
node --version
node (Join-Path $relayRoot 'tools/dev-handoff/zero-prompt-relay/src/cli.mjs') --enable-read-only --once
```

Expected first result when a valid synthetic GitHub ping is present: `{"status":"read-only","observed":1,"newlySeen":1,"executionAllowed":false}`. Repeating once should return `newlySeen:0`. Any other result is a test outcome to diagnose, not permission to start anything.

For *manual opt-in* foreground polling (terminal stays running):

```powershell
node (Join-Path $relayRoot 'tools/dev-handoff/zero-prompt-relay/src/cli.mjs') --enable-read-only --watch
```

No scheduled task/Windows service is installed. Do not enable OS autostart yet; independently review first. If the source checkout already exists, **do not** overwrite/recreate it—inspect its branch/HEAD and update with a reviewed fast-forward procedure.

## Missing pieces

This P0 slice proves only **GitHub → local receipt** after one-time startup. It does not prove **local → GitHub ack**, HIC task/run claims, reviewed signing keys, lease recovery, sandbox isolation, SDK login/quota, code/test/CI artifacts or autonomous continuation. Those must be separately implemented and approved under [Issue #93](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/93); existing [#92](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/92) sandbox preflight remains `UNVERIFIED`.

*Humans are not message queues.*
