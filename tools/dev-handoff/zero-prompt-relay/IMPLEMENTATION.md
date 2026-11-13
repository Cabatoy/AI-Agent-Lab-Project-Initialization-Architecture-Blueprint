# HIC Relay Authenticated GitHub API Client — Implementation Guide

**Date:** 2026-11-13  
**Issue:** #142  
**Branch:** `feature/issue-142-relay-auth-rate-limit`  
**Status:** Draft PR (no merge/deploy)

## Overview

This implementation addresses GitHub Issue #142: Making the development-only Relay watcher robust under GitHub API rate limits. The solution provides:

- **Secure authenticated REST client** for GitHub API with no credential exposure
- **Rate-limit aware retry logic** with exponential backoff and jitter
- **Git Credential Manager integration** for credential management
- **Output redaction** to prevent secret leakage in logs/exceptions
- **Offline test suite** with fake HTTP and credential providers

## Architecture

### Components

1. **GitHubRestClient** (`GitHubRestClient.cs`)
   - Main API client with authenticated requests
   - Distinguishes 401/403 auth errors from rate limits (429)
   - Never logs, forwards or exposes credentials
   - Respects `Retry-After`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` headers

2. **GitCredentialManagerProvider** (`GitCredentialManagerProvider.cs`)
   - Fetches credentials via `git credential fill` (Git Credential Manager)
   - Validates token format without assuming REST scope validity
   - Gracefully returns null if GCM unavailable or credentials missing
   - Decoupled from remote transport success

3. **RateLimitHandler** (`RateLimitHandler.cs`)
   - Exponential backoff with jitter: (2^n + jitter) * 1s, capped at 5 minutes
   - Tracks consecutive rate limits; fails after max retries
   - Extracts and respects rate limit headers

4. **OutputRedactor** (`GitCredentialManagerProvider.cs`)
   - Regex-based redaction of token patterns, Authorization headers, Bearer tokens
   - Applied to all exception messages and diagnostic output

### Credential Flow

```
Relay Watch Loop
   ↓
GitHubRestClient.FetchIssueAsync()
   ↓
GitCredentialManagerProvider.GetCredentialAsync()
   → (if unavailable, return null; Relay continues with fallback)
   → Invoke: git credential fill
   → Parse username/password (password = token)
   ↓ (if credential obtained)
Set Authorization: token <redacted> header
Send GET /repos/.../issues/142
   ↓
Check response status & rate-limit headers
   ↓
If 401/403: throw GitHubAuthException (auth/scope error)
If 429: RateLimitHandler.HandleRateLimitAsync() → backoff & retry signal
If 2xx: return IssueResponse
```

## Setup Instructions

### Prerequisites

1. **Git Credential Manager (GCM)** installed:
   ```powershell
   git credential-manager --version
   ```
   If not installed: [GitHub/git-credential-manager](https://github.com/git-ecosystem/git-credential-manager)

2. **GitHub Personal Access Token (PAT)** or fine-grained token:
   - Minimum scope: `public_repo` (read public repositories)
   - Never hardcode; store in OS-protected GCM vault
   - For fine-grained tokens: grant `Contents: read` on specific repos

3. **No modification required** to existing Relay configuration:
   - This is an opt-in upgrade for the REST client layer
   - Live Relay process remains unaffected until explicit enablement

### Integration with Existing Relay

The Relay watcher currently uses `curl` or .NET HTTP for API calls. To integrate:

1. **Create a wrapper CLI tool** that accepts:
   - Owner, Repo, Issue Number
   - Returns JSON response or rate-limit backoff signal

   Example:
   ```powershell
   tools/dev-handoff/relay-fetch-issue.ps1 -Owner "Cabatoy" -Repo "AI-Agent-Lab..." -IssueNumber 142
   ```

2. **Build and register** the client:
   ```bash
   dotnet build tools/dev-handoff/zero-prompt-relay/RelayAuthClient.csproj
   dotnet build tools/dev-handoff/zero-prompt-relay/tests/RelayAuthClient.Tests.csproj
   ```

3. **Run offline tests** (no network, no real tokens):
   ```bash
   dotnet test tools/dev-handoff/zero-prompt-relay/tests/RelayAuthClient.Tests.csproj
   ```

## Credential Safety — No Token Leakage

### ✅ Credentials Never Exposed In:

- **Exception messages:** All credential-related errors redacted before throw
- **HTTP headers in logs:** Request/response headers redacted
- **Process arguments:** Token never passed on command line
- **Git remote URLs:** Not used for API auth
- **Tracked files/config:** Zero hardcoded tokens
- **Test fixtures:** All credentials are fake/test-only
- **VS Code Chat:** Errors are user-facing but redacted

### ✅ Examples of Redaction

**Input:**  
```
Authorization: token ghp_1234567890abcdefghijklmnopqrstuvwxyz
```
**Output:**  
```
Authorization: ***REDACTED***
```

**Input:**  
```
Error: Request failed with Bearer eyJhbGciOiJIUzI1NiJ9...
```
**Output:**  
```
Error: Request failed with Bearer ***REDACTED***
```

## Rate Limit Handling

### Retry Strategy

1. **Primary rate limit (429):** Respect `Retry-After` and `X-RateLimit-Reset`
2. **Backoff formula:** `delay = (2^attempt + jitter) * 1s`, capped at 5 minutes
3. **Max consecutive retries:** Configurable (default: 3)
4. **After max retries:** Throw `RateLimitExceededException` with reset timestamp

### Error Handling Matrix

| Status | Error Type | Handler | Relay Action |
|--------|-----------|---------|--------------|
| 401 | `GitHubAuthException` | Throw (auth error) | Log, halt this watch cycle |
| 403 | `GitHubAuthException` | Throw (scope error) | Log, halt this watch cycle |
| 429 | RateLimit | `HandleRateLimitAsync()` | Backoff, retry, or defer |
| 5xx | Network | `HttpRequestException` | Network error, defer |

## Testing

### Offline Test Suite

All tests use **fake HTTP handlers** and **fake credential providers** — no real network calls, no real tokens.

**Test categories:**

1. **Authentication**
   - Valid credential → successful fetch
   - Missing credential → returns null gracefully
   - Invalid credential → throws `GitHubAuthException`
   - Invalid scope → throws `GitHubAuthException`

2. **Rate Limiting**
   - 429 response → `HandleRateLimitAsync()` called
   - Retry logic respected
   - Max retries enforced

3. **Redaction**
   - Token patterns removed from messages
   - Authorization headers redacted
   - Bearer tokens redacted

4. **Transient Failures**
   - Network errors propagate
   - Timeout handling

**Run tests:**
```bash
dotnet test tools/dev-handoff/zero-prompt-relay/tests/RelayAuthClient.Tests.csproj -v normal
```

**Expected output:**
```
Starting test execution, please wait...
A total of X test files matched the specified pattern.
...
Passed!  —  X passed, 0 failed
```

## Rollback & Safety

### ✅ Zero Impact on Current Relay

- This implementation is **standalone**, not integrated into the live Relay yet
- Existing `scripts/hic-relay.ps1` or watch loop remains unchanged
- No automatic enablement or machine-wide permission changes
- Explicit approval required before activating in production

### ✅ Rollback Process

If issues arise:

1. **Revert branch:**
   ```bash
   git checkout task/hic-relay-smoke-139  # Prior stable state
   ```

2. **No secrets left behind:**
   - Redactor prevents token leakage in logs
   - No `.env`, `.config` files with tokens
   - Git Credential Manager vault unchanged

3. **No runtime side effects:**
   - No background services installed
   - No system registry changes
   - No firewall/network rules added

## Known Limitations

1. **GCM-only credential source:** Currently integrates with Git Credential Manager.
   - If GCM unavailable, returns null (graceful fallback)
   - Alternative: fine-grained GitHub App token (documented but not implemented here)

2. **No interactive auth:** Tokens must be pre-stored in GCM vault.
   - Prevents blocking/hanging during unattended runs
   - User must configure GCM manually before watch polling starts

3. **Read-only scope:** Limited to repository read access.
   - Cannot write/merge/approve on behalf of user
   - Suitable only for watch polling and issue inspection

## Next Steps (Issue #144)

This implementation is a foundation for Issue #144 ("HIC unattended development — isolated workspace, corporate-data exclusion and outbound gates"). The rate-limit and auth logic here will be embedded in a broader execution sandbox that:

- Isolates workspace and network endpoints
- Enforces outbound content scanning
- Prevents credential leakage across personal/corporate boundaries
- Requires explicit approval before unattended mode activation

---

## Draft PR Checklist

- [x] Secure GitHub API client with no credential exposure
- [x] Git Credential Manager integration with validation
- [x] Rate-limit aware retry with exponential backoff
- [x] Output redactor for all sensitive data
- [x] Offline test suite (fake HTTP, fake credentials)
- [x] Setup and integration documentation
- [x] Zero impact on current Relay
- [x] No automatic enablement or machine-wide changes

**PR Status:** Draft (ready for review, not for merge)  
**CI Evidence:** See `git diff --check` for whitespace, and build log below

