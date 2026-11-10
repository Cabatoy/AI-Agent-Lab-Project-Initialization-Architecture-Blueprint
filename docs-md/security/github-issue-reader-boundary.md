# HIC Issue Reader — least-privilege security contract (Issue #80)

**Status:** proposed documentation-only; NOT implemented, installed, enabled, merged, or authorized for live credentials.

**Pinned source:** #73 `07a49f9d511ad7dfd55b9f258f759568061e7046`. This document is stacked on that exact commit. User-visible Issue import remains blocked until a human-reviewed operator acceptance.

## Findings in current code

- `GitHubIssuePreview.Map` uses `GitHubAppBroker.Destination` and `GitHubAppBroker.OpenAsync` by default for source preview, prepared import and confirmation.
- `GitHubAppBroker.OpenAsync` obtains installation tokens with `contents=write`, `pull_requests=write`, `metadata=read`; `CheckPermissions` requires exactly these publication-oriented grants. Requests being GET does **not** reduce that token's authority.
- `GitHubIssuePreview.Fetch` rechecks exact destination and rejects private repositories, but reuses publisher-authorized API. Its `GetAsync("/issues/{number}")` needs explicit Issue-vs-PR discrimination, using the `pull_request` payload field.
- `GitHubIssueImportStore` performs actor-bound five-minute preview, GitHub source reread, transactional revision dedup and Draft creation; preserve this flow and existing schema. Separate broker changes must not widen permission, bypass actor binding, auto-confirm, dispatch, or publish.
- `hic_issue_import_previews` is immutable with no documented expiry cleanup; retention/capacity is follow-up design, not permission to delete or migrate live data.

## Boundaries and design decisions to resolve

1. Define **`IIssueSourceReader`** or equivalent narrow interface: `ReadIssueAsync(ValidatedIssueReference, ActorContext, CancellationToken)` returns canonical source identity and revision digest, never credentials or mutable API clients. It must not expose commit, branch, PR write or installation-wide repository enumeration to consumers. Keep publisher broker unchanged.
2. Separate **capability/status** from credential details. Public-only, authenticated-read-only and unavailable modes are distinct. Public anonymous mode must be explicitly allowlisted for one repository with strict hostname and path validation; no fallback to publisher write token when read authentication fails. Private mode requires a separately approved installed reader identity and read-scoped GitHub App permissions. Check actual GitHub App installation grants rather than assuming GitHub token exchange can narrow installation permissions not granted to the app.
3. Bind exact `repositoryId`, normalized `owner/name`, `issueNumber`, returned `issueId`, source `updatedAt`, and revision digest. Verify repository metadata and private/archived state. Reject issue responses containing a `pull_request` object. Do not treat a GitHub PR number as an Issue import target. Restrict redirects, API hostname, response bytes, network timeout and decoded field size. Never embed attacker-provided URL directly into a network request.
4. Keep **operator authentication** and source authorization independent. Revalidate session after GitHub reads and before writing prepared preview and confirmation; reject actor/installation/source revision changes. Never log token, PEM, auth header, fetched private issue body, raw response or untrusted oversized errors.
5. The provider response is **untrusted content** even after authenticated fetch. Source text may be displayed/quoted for human review, never executed as an agent instruction. Prepared Draft title/description are bounded, visible and edited by the operator; no model/tool call on fetch or dashboard refresh.
6. Preserve failure modes: missing reader permission, 401/403, private repo without read authorization, repository mismatch, token expiry, suspended installation, 404, 429/rate limit, GitHub 5xx, timeout, malformed JSON, pull request instead of issue, stale revision, concurrent confirm and session revocation are **fail closed**, with stable sanitized reason codes. No implicit retry of a possible write.
7. Source reread and local PostgreSQL commit are not one atomic GitHub transaction. Document that limitation, preserve revision dedup and test a remote mutation between preview/prepare/confirm, including a mutation just after final fetch. Do not claim cross-service atomicity.

## Suggested thin interfaces

```text
IssueReaderCapability:
  availability: public-only | configured-read-only | unavailable
  repository: exact configured display name (optional)
  missing: sanitized capability identifiers (no paths, key names or tokens)

VerifiedIssueSource:
  repositoryId, issueId, issueNumber, canonicalUrl
  title, body, updatedAtUtc, revisionSha256
  authorizationScopeDigest (not a bearer token)

IssueReader.OpenAsync:
  provider object supports GET/verify only
  source identity must be rechecked per import operation
  publisher token is never a fallback
```

No new API endpoint is authorized by this design alone. If a status route is implemented, return capability metadata under the existing authenticated operator scope; expose no credentials.

## Negative test / acceptance matrix

| Case | Expected outcome |
| --- | --- |
| Publisher write credential configured, reader absent | Issue read unavailable; never use publisher credential |
| Reader app token has `contents:write` or `pull_requests:write` | Reject as non-minimal for read broker |
| Public allowlisted repository and valid real Issue | Preview-only content; zero local Draft/queue/PR writes |
| PR number supplied as issue | Reject `pull_request` response |
| Requested repo ID/full name differs from provider metadata | Reject; no cross-repo scan or fallback |
| Private repo without explicitly approved reader installation | Reject before issue body disclosure |
| Reader installation suspended/revoked/expired | Reject and redact provider error |
| GitHub 401/403/404/429/5xx or malformed/truncated response | Fail closed; bounded response; no token leak |
| Redirect to different hostname or arbitrary user-provided URL | Reject before accessing redirect |
| Actor session expires while remote GET is pending | Discard result; no prepared preview or confirm |
| Issue revision changes at prepare or confirm | Conflict; no Draft |
| Same actor repeats a confirmed revision | Return safe dedup outcome; no second Draft |
| Different actor races for same revision | No cross-actor reuse; deterministic conflict |
| Import preview expires or source identity changes | No confirmed task |
| Issue body includes instructions to exfiltrate secrets | Render as data only; no tool/model/command execution |
| Failed GitHub read while UI Dashboard refreshes | No auto-read provider or auto-import |
| Publisher has existing live publication workload | Regression: read broker changes cannot affect publisher |
| Network outage / cancelled request | No retry storm, no local mutation |
| Live DB schema missing v11 | Fail closed; isolated synthetic fixture only for tests |

## Implementation sequencing and acceptance

**R0 — design and test harness:** this document, provider fixtures and explicit security tests. No new grants or runtime behaviour.

**R1 — broker split in separate Draft PR:** implement dedicated reader abstraction plus strictly bounded configuration/status and mock provider tests. Keep publisher API and its token use isolated. Start with public-only **synthetic** fixture. Do not install a real app or request keys.

**R2 — disposable acceptance:** exact-head GitHub CI and dedicated v11 PostgreSQL tests; isolated preview on separate ports/resources. Inspect real browser source → review → prepare → separate human confirm, restart/dedup/audit with zero run/queue/publication. Installing reader credentials for private GitHub issues or migrating a real database requires a new explicit scoped human decision.

**Out of scope:** Dashboard UI code (#82/#84), Fusion developer automation (#83), model lifecycle (#76), Worker/executor (#63), automatic PR merge, deploy, GitHub App credential acquisition, real private issue access.

**Stop condition:** no provider-native narrow read API/installation permission means no read broker, not fallback to publisher. Human remains the authority for the final import decision.

Refs: #80, #72, #73, #75 Q3, #79, #83. *Humans are not message queues.*
