# HIC Relay — Authenticated GitHub API Client

**Issue:** #142  
**Branch:** `feature/issue-142-relay-auth-rate-limit`  
**Status:** Draft PR — Ready for Review (No Merge)

## What's Included

This implementation addresses GitHub Issue #142 by providing a robust, secure GitHub REST API client for the HIC Relay watcher with:

### 🔐 Security
- **Authenticated requests** via Git Credential Manager (GCM)
- **Zero credential exposure** in logs, exceptions, or command lines
- **Output redaction** for all sensitive data (tokens, Bearer headers, etc.)
- **No hardcoded secrets** anywhere in the codebase

### ⚙️ Functionality
- **Rate-limit aware** client with exponential backoff + jitter
- **Distinction** between auth errors (401/403) and rate limits (429)
- **Respects** GitHub API headers: `Retry-After`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`
- **Graceful fallback** when credentials unavailable

### ✅ Testing
- **54 offline tests** covering auth, rate limits, redaction, error handling
- **No network calls**, no real tokens, completely isolated
- **Test categories**: credential validation, output redaction, retry logic, URL parsing, HTTP status handling

### 📚 Documentation
- `IMPLEMENTATION.md` — Full technical guide, setup, credential flow, and rollback instructions
- `relay-fetch-issue.ps1` — PowerShell wrapper for Relay integration

## Directory Structure

```
tools/dev-handoff/
├── zero-prompt-relay/
│   ├── RelayAuthClient.csproj              # C# library project
│   ├── GitHubRestClient.cs                 # Main authenticated API client
│   ├── GitCredentialManagerProvider.cs     # GCM credential integration
│   ├── RateLimitHandler.cs                 # Exponential backoff + rate limit logic
│   ├── IMPLEMENTATION.md                   # Detailed implementation guide
│   ├── RelayAuthClient.sln                 # Solution file
│   └── tests/
│       ├── RelayAuthClient.Tests.csproj    # XUnit test project
│       ├── GitHubRestClientTests.cs        # API client tests
│       ├── CredentialAndRedactionTests.cs  # Credential & redaction tests
│       └── offline-tests.ps1               # Standalone offline test suite (54 tests, all passing)
└── relay-fetch-issue.ps1                   # PowerShell wrapper for Relay integration
```

## Running the Offline Tests

All tests are **completely offline** — no network, no real tokens, no side effects:

```powershell
cd tools/dev-handoff/zero-prompt-relay/tests
. ./offline-tests.ps1
```

**Expected output:**
```
✅ ALL TESTS PASSED
Passed: 54
Failed: 0
```

## Key Design Decisions

### ✅ GCM Integration
- Respects `git credential fill` for credential retrieval
- Does **not** assume git remote success means REST token validity
- Gracefully handles missing GCM (returns null, Relay continues)

### ✅ Rate Limit Strategy
- Exponential backoff: (2^n + jitter) × 1s, capped at 5 minutes
- Respects GitHub headers: `Retry-After`, `X-RateLimit-Reset`
- Max consecutive retries: 3 (configurable)

### ✅ No Credential Leakage
- All credentials redacted before exception throw
- No tokens in process arguments, HTTP headers, git URLs, or tracked files
- Regex-based redaction of token patterns, Authorization headers, Bearer tokens

### ✅ Zero Impact on Current Relay
- This is a **standalone library** and wrapper, not yet integrated
- Existing Relay process remains completely unchanged
- Explicit approval required before activating in production

## Security & Compliance

- ✅ No secrets in source code
- ✅ No `.env`, `.config` files with credentials
- ✅ All errors user-facing but redacted
- ✅ Git Credential Manager vault untouched
- ✅ No background services or system changes
- ✅ Graceful rollback (git revert)

## Known Limitations

1. **GCM-only credential source** — Currently integrates with Git Credential Manager
   - Alternative: fine-grained GitHub App tokens (documented but not implemented)

2. **No interactive auth** — Requires pre-configured credentials in GCM
   - Prevents blocking during unattended runs

3. **Read-only scope** — Limited to repository read operations
   - Cannot write/merge/approve on behalf of user

## Next Steps

This is a prerequisite for **Issue #144** ("HIC unattended development — isolated workspace, corporate-data exclusion and outbound gates"), which will embed this rate-limit and auth logic in a broader execution sandbox.

## Verification Checklist

- [x] Offline test suite passing (54 tests)
- [x] No trailing whitespace (`git diff --check` passes)
- [x] Secure credential handling — no exposure in logs/exceptions
- [x] Rate-limit aware with exponential backoff
- [x] Git Credential Manager integration
- [x] Output redaction for all sensitive data
- [x] Documentation complete
- [x] Zero impact on current Relay
- [x] No automatic enablement or machine-wide changes

---

**PR Status:** Draft (ready for review, not for merge/deploy)  
**Base Branch:** `develop`
