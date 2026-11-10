# HIC developer-only Copilot SDK PoC

This package is an isolated S0/S1 and S2-readiness slice for Issues #88/#90. It is not
part of the HIC product, solution, release, UI, Worker, executor, or a customer
workflow. It cannot control or resume the active VS Code Copilot Fusion chat;
the SDK creates its own Copilot CLI agent session.

## Installed environment and pinned SDK

Read-only checks on 2026-11-10 found:

| Component | Observed |
|---|---|
| Node.js | `v24.22.2` |
| npm | `11.12.1` |
| .NET SDK | `10.0.403` |
| VS Code | `1.145.2` |
| Copilot CLI | Not available: the `copilot --version` shim reported that the CLI was not found |
| SDK authentication | Not verified; the CLI was unavailable, and no credentials/keychain state was inspected |

The official Node SDK is pinned to `@github/copilot-sdk` **1.0.30** in
`package.json` and `package-lock.json`. The npm metadata reported bundled
Copilot runtime **1.0.108** for that SDK package; the SDK release page showed
`1.0.31-preview.1` as a pre-release, so this PoC pins the latest stable package
observed during research, not a floating tag. The official SDK supports Node
`^20.19.0 || >=22.12.0`; installed Node 24.22.2 satisfies that requirement.
The installed .NET SDK is also compatible with the documented .NET SDK option,
but Node/TypeScript was selected because this environment already provides a
supported Node runtime and the SDK package ships its CLI runtime.

The installed `copilot` command was only a VS Code shim in the S0 environment;
it did not establish that a supported SDK-bundled runtime was invoked or
authenticated. The npm package metadata reports bundled Copilot runtime
**1.0.108**, so global CLI absence is not evidence that the SDK cannot run.
This branch does not execute that runtime or invoke any CLI command. The
non-secret `npm run preflight` reports only whether a `copilot` executable name
is present on `PATH`; it does not print the path, run a version/auth command,
or inspect any other environment variable, token store, keychain, or credential
file. An editor Copilot sign-in is not proof of SDK/CLI authentication.

## Authentication, quota, and billing

The official SDK documents distinct authentication modes:

- **Signed-in Copilot CLI user:** SDK/CLI uses the CLI user's stored sign-in
  credentials. This can use that user's Copilot subscription and may consume
  the plan's applicable premium-request quota. Account entitlement, model
  access, current quota, organization policy, and any overage setting were not
  available for this offline preflight and must be checked before S2.
- **OAuth or token/environment authentication:** requires explicitly supplied
  user or service credentials and the corresponding Copilot access/policy.
  This PoC does not set or install any of these.
- **Server-to-server token:** organization-attributed usage with organization
  policy and billing; it is not the user's subscription.
- **BYOK:** uses a separately configured provider key and can incur that
  provider's charges. This PoC does not choose a provider or BYOK.

No live model request was made, so no quota use, model entitlement, rate limit,
provider egress, or billable amount was measured. Do not infer a free request
from an existing Copilot plan or choose BYOK automatically.

## Tool permission boundary

The official SDK documentation says first-party tools are exposed by default
similarly to CLI `--allow-all`; the permission handler controls approval,
denial, or customization. The pinned SDK types also say that without a handler,
permission requests are surfaced and left pending for the consumer.

The concrete transport therefore creates a session with:

- `availableTools: []`, so no model-facing tools are allowlisted;
- a permission callback that rejects every request; and
- `skipCustomInstructions: true`, to avoid importing repository instructions.

These are defense-in-depth SDK settings, **not an OS sandbox**. The optional
`ReadOnlyGitExactHeadVerifier` implementation performs read-only local checks:

- requires the exact allowlisted repository origin, a linked (non-primary)
  Git worktree, the reviewed branch and full `HEAD` SHA;
- rejects a dirty or ignored-file-containing worktree, unregistered/locked
  worktrees, path escapes, symlink/reparse paths, and non-regular or
  non-Markdown files outside `docs/` and `docs-md/`;
- compares the committed tree, index, and filesystem by Git blob hashes instead
  of invoking `git status`; it disables optional Git locks/global config and
  passes only executable-resolution/system variables to Git subprocesses. A
  checkout whose filesystem bytes differ from Git blobs (for example due to
  line-ending conversion) is rejected rather than normalized;
- enforces per-command timeout and cancellation and requires the review task
  digest to be signed by a configured trusted Ed25519 public key. No trusted key
  is configured in this PoC, so the real verifier fails closed until an
  independently managed key/approval process is provided.

The verifier runs before session creation and again immediately before task
submission. It is not a filesystem lock: another process can still change a
worktree after a read-only check. Tests keep the SDK transport fake. Existing
adapter unit tests use a fake `ExactHeadVerifier`; the concrete verifier is
explicitly injectable and is never the default. Local `HEAD` is not proof that
GitHub CI checked that commit.

GitHub check status has a separate `GitHubExactHeadStatusProvider` contract and
schema (`github-check-runs-read-only`). It accepts only the allowlisted
repository and full SHA, validates each returned check-run `head_sha` against
that SHA, and returns `unknown` when no checks exist. Its API dependency is
injected; no GitHub API call or credential is used by this package's tests. CI
checks out and asserts the exact pull-request head SHA on Windows and Linux.

`session.idle` is recorded as an SDK lifecycle event only. Evidence always has
`executionVerified: false`; code changes, tests, CI, an exact GitHub SHA, and
HIC ledger state require independent verification and are not inferred from an
SDK event or assistant response.

## Offline-only usage and tests

Dependencies and scripts are scoped to this directory. The adapter is disabled
unless `HIC_COPILOT_SDK_POC_ENABLE=1` is present **and** a reviewed input has
`oneShotOptIn: true`. A verifier must independently approve the exact task;
the adapter accepts only HIC issue #75, this repository, one UUID task, one
turn, one session, a short expiry, a full base SHA, a review digest, and
Markdown paths under `docs/` or `docs-md/`. Arbitrary issue text is not an
accepted input field and is never placed in the generated prompt.

From this directory:

```powershell
npm ci --omit=optional --ignore-scripts --no-audit --no-fund
npm run check
```

Adapter tests use a fake transport and fake exact-head verifier; separate
verifier tests use disposable local Git fixtures. No test constructs a
`CopilotClient`, starts a CLI/runtime, authenticates, accesses a model, or makes
network requests. Dependency installation excludes optional platform runtime
binaries and lifecycle scripts. There is no live-session command in this
readiness slice. The `HIC Copilot SDK PoC (offline)` workflow runs the same
typecheck/tests on Windows and Linux with read-only repository permission.

Run the redacted local preflight separately with `npm run preflight`. It prints
the pinned SDK and package-metadata runtime-version claim, `cli.namePresentOnPath=not-checked`,
and the states `authentication=unknown`,
`modelEntitlement=unknown`, `quotaOrBilling=unknown`. It does not launch the
bundled runtime or CLI, scan `PATH` (which could contain network locations),
inspect credentials, access the network, or make a model request. The earlier
read-only `copilot --version` probe in this environment found only a VS Code
shim and reported the CLI unavailable. The npm package metadata claims the
bundled runtime version, but the runtime has not been invoked; authentication
support, entitlement, and quota remain unverified.

## Sandbox threat model and fixture plan

The threat includes untrusted reviewed-task metadata, SDK/model output,
repository content, inherited process environment, host filesystem and
credential stores, network egress, runaway processes/resources, and cleanup
mistakes. Empty SDK tool configuration does not constrain runtime internals;
Git worktrees do not provide OS isolation.

**No safe execution sandbox has been implemented or verified.** In particular,
Windows, WSL2, and Podman filesystem, process, and network boundaries are not
assumed equivalent. A future reproducible disposable fixture must:

1. Generate a unique disposable identity and a deterministic, minimal fixture
   repository with a seed commit; never mount the HIC production checkout,
   `.git`, home directory, databases, Redis, live volumes, or a developer
   workspace.
2. Run as an unprivileged isolated user in a digest-pinned VM/container image.
   Mount only the fixture input read-only and a single scoped output directory
   read/write. Start with a read-only root filesystem, no host socket/device,
   no inherited credentials or unrelated environment, and verify those
   properties on each supported Windows/Linux boundary.
3. Bound CPU, memory, process count, wall-clock time, file size, and output
   volume. Default network to none for offline tests. Any separately approved
   future SDK request would require a controlled egress allowlist and explicit
   single-use human consent; no zero-cost or no-egress claim is implied.
4. Record only the disposable identity and sanitized exit/evidence metadata.
   Clean up only the exact container/worktree created for that run, verify its
   removal, and never use broad prune/reset/delete commands.
5. Re-run the fake-transport offline harness on Windows and Linux. No CI job
   starts the runtime, CLI, live model, or credential preflight.

The verifier tests create uniquely named disposable Git fixtures under the
OS temporary directory (rather than the deeply nested CI checkout path on
Windows), commit no fixture artifacts, and remove only each test's exact
fixture path. This tests local Git/worktree checks, not OS isolation. Until the sandbox and environment-scrubbing properties above are
proven, even a no-tools, no-file-write SDK message can expose sensitive
environment/context through the runtime or network. A future S2 smoke may only
propose that non-mutating response first; docs-file editing is not presently
feasible or authorized.

## S2 readiness

**Blocked, not authorized or run.** Bundled runtime version **1.0.108 is a
package-metadata claim**, not an invocation test. Authentication,
model entitlement, quota, and billing are all **unknown**; no account
identifiers, credentials, or billing details were inspected. The exact-head
local verifier and GitHub status schema are implemented and offline-tested, but
there is no configured trusted review key, no live GitHub check provider, and
no OS sandbox proof. A worktree alone is insufficient isolation; even the
non-mutating model smoke remains blocked. No model session was started, and no
quota use or cost was measured. Do not promise zero cost. Any future real
request requires separate explicit one-shot human consent and re-validation of
auth, entitlement, quota/billing, sandbox, and egress.

## Official references

- [GitHub Copilot SDK repository](https://github.com/github/copilot-sdk)
- [SDK getting started](https://github.com/github/copilot-sdk/blob/main/docs/getting-started.md)
- [Official authentication methods](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate)
- [Official Node.js SDK README](https://github.com/github/copilot-sdk/blob/main/nodejs/README.md)
- [SDK package 1.0.30 metadata](https://registry.npmjs.org/@github/copilot-sdk/1.0.30)

Research and installed-tool checks: 2026-11-10. Public documentation and
published package metadata can change; revalidate before any later S2 decision.
