import assert from "node:assert/strict";
import test from "node:test";
import { ALLOWED_REPOSITORY } from "../src/contracts.ts";
import {
  GitHubExactHeadStatusProvider,
  type GitHubReadOnlyChecksApi,
} from "../src/github-verification.ts";
import {
  collectReadinessPreflight,
  DECLARED_BUNDLED_RUNTIME_VERSION,
} from "../src/preflight.ts";

const exactSha = "a".repeat(40);

class FakeChecksApi implements GitHubReadOnlyChecksApi {
  requested: Array<{ repository: string; sha: string }> = [];
  response: unknown = {
    total_count: 1,
    check_runs: [
      {
        id: 12,
        name: "offline",
        head_sha: exactSha,
        status: "completed",
        conclusion: "success",
      },
    ],
  };

  async listCheckRuns(repository: string, sha: string): Promise<unknown> {
    this.requested.push({ repository, sha });
    return this.response;
  }
}

test("GitHub read-only status schema binds checks to the exact requested SHA", async () => {
  const api = new FakeChecksApi();
  const provider = new GitHubExactHeadStatusProvider(api);
  const status = await provider.read(ALLOWED_REPOSITORY, exactSha);

  assert.equal(status?.provider, "github-check-runs-read-only");
  assert.equal(status?.requestedSha, exactSha);
  assert.equal(status?.observedSha, exactSha);
  assert.equal(status?.overall, "success");
  assert.deepEqual(api.requested, [{ repository: ALLOWED_REPOSITORY, sha: exactSha }]);

  api.response = {
    total_count: 1,
    check_runs: [
      {
        id: 12,
        name: "offline",
        head_sha: "b".repeat(40),
        status: "completed",
        conclusion: "success",
      },
    ],
  };
  assert.equal(await provider.read(ALLOWED_REPOSITORY, exactSha), undefined);
  assert.equal(await provider.read("attacker/repo", exactSha), undefined);
  assert.equal(await provider.read(ALLOWED_REPOSITORY, "abc"), undefined);

  api.response = { total_count: 0, check_runs: [] };
  const noChecks = await provider.read(ALLOWED_REPOSITORY, exactSha);
  assert.equal(noChecks?.overall, "unknown");
  assert.equal(noChecks?.observedSha, null);

  api.response = {
    total_count: 2,
    check_runs: [
      {
        id: 12,
        name: "offline",
        head_sha: exactSha,
        status: "completed",
        conclusion: "success",
      },
    ],
  };
  assert.equal(await provider.read(ALLOWED_REPOSITORY, exactSha), undefined);

  api.response = {
    total_count: 1,
    check_runs: [
      {
        id: 12,
        name: "offline",
        head_sha: exactSha,
        status: "completed",
        conclusion: "skipped",
      },
    ],
  };
  assert.equal(
    (await provider.read(ALLOWED_REPOSITORY, exactSha))?.overall,
    "unknown",
  );
});

test("readiness preflight avoids PATH and credentials and leaves auth/quota unknown", () => {
  const preflight = collectReadinessPreflight();
  assert.equal(preflight.sdk.pinnedVersion, "1.0.30");
  assert.equal(preflight.sdk.bundledRuntimeVersionClaim, DECLARED_BUNDLED_RUNTIME_VERSION);
  assert.equal(preflight.sdk.runtimeInvoked, false);
  assert.equal(preflight.cli.namePresentOnPath, "not-checked");
  assert.equal(preflight.cli.presenceIsAuthoritative, false);
  assert.equal(preflight.cli.versionQueryRun, false);
  assert.equal(preflight.authentication, "unknown");
  assert.equal(preflight.modelEntitlement, "unknown");
  assert.equal(preflight.quotaOrBilling, "unknown");
  assert.equal(preflight.credentialsInspected, false);
  assert.equal(preflight.networkAccessed, false);
  assert.equal(preflight.modelRequestMade, false);
  assert.equal(JSON.stringify(preflight).includes("PATH"), false);
});
