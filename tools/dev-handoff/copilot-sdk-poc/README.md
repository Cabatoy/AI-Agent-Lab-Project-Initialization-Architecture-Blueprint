# HIC developer-only Copilot SDK PoC

This package is an isolated S0/S1 feasibility slice for Issue #88. It is not
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

The installed `copilot` command is only a VS Code shim in this environment; it
does not establish that the Copilot CLI is installed or authenticated. No
credential was read, copied, installed, or emitted. The VS Code editor or its
extension sign-in must not be treated as proof of CLI/SDK authentication.

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

These are defense-in-depth SDK settings, **not an OS sandbox**. A Git
worktree alone does not isolate the host filesystem, network, inherited
environment, or credentials. This PoC has no exact-SHA verifier implementation
or sandbox implementation; both remain separate S2 prerequisites.

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

Tests use only a fake transport and fake exact-head verifier. They do not
construct a `CopilotClient`, start a CLI/runtime, authenticate, access a model,
or make network requests. Dependency installation excludes optional platform
runtime binaries and lifecycle scripts. There is no live-session command in
this S1 slice. The `HIC Copilot SDK PoC (offline)` workflow runs the same
typecheck/tests on Windows and Linux with read-only repository permission.

## S2 readiness

**Blocked, not authorized or run.** Before S2, an operator must verify an
appropriate SDK CLI authentication and Copilot model/quota/billing mode; approve
a bounded disposable docs-only test; and provide a separately reviewed exact
head/worktree verifier plus a defensible sandbox with explicit filesystem and
egress controls. The CLI/auth state was not verified here, and a worktree alone
is insufficient isolation. S1 results are not S2 acceptance.

## Official references

- [GitHub Copilot SDK repository](https://github.com/github/copilot-sdk)
- [SDK getting started](https://github.com/github/copilot-sdk/blob/main/docs/getting-started.md)
- [Official authentication methods](https://docs.github.com/en/copilot/how-tos/copilot-sdk/auth/authenticate)
- [Official Node.js SDK README](https://github.com/github/copilot-sdk/blob/main/nodejs/README.md)
- [SDK package 1.0.30 metadata](https://registry.npmjs.org/@github/copilot-sdk/1.0.30)

Research and installed-tool checks: 2026-11-10. Public documentation and
published package metadata can change; revalidate before any later S2 decision.
