import { randomUUID } from "node:crypto";
import { execFileSync } from "node:child_process";
import {
  lstatSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";

const OWNER_FILE = ".hic-sandbox-owner";
const FIXTURE_PREFIX = "hic-copilot-offline-";

function fixtureGit(cwd: string, args: readonly string[]): void {
  execFileSync("git", args, {
    cwd,
    env: {
      PATH: process.env.PATH,
      PATHEXT: process.platform === "win32" ? process.env.PATHEXT : undefined,
      SYSTEMROOT: process.env.SystemRoot ?? process.env.SYSTEMROOT,
      WINDIR: process.env.WINDIR,
      TEMP: process.env.TEMP,
      TMP: process.env.TMP,
      GIT_CONFIG_NOSYSTEM: "1",
      GIT_CONFIG_GLOBAL: process.platform === "win32" ? "NUL" : "/dev/null",
      GIT_TERMINAL_PROMPT: "0",
      GIT_AUTHOR_DATE: "2026-01-01T00:00:00Z",
      GIT_COMMITTER_DATE: "2026-01-01T00:00:00Z",
    },
    windowsHide: true,
    stdio: "ignore",
  });
}

export const OFFLINE_SANDBOX_POLICY = Object.freeze({
  enabledByDefault: false,
  network: "none",
  rootFilesystem: "read-only",
  fixtureInput: "read-only",
  output: "single-bounded-tmpfs",
  inheritedEnvironment: "deny",
  credentials: "deny",
  hostSocketsAndDevices: "deny",
  cpuLimitRequired: true,
  memoryLimitRequired: true,
  processLimitRequired: true,
  wallClockLimitRequired: true,
  outputSizeLimitRequired: true,
  broadCleanupAllowed: false,
});

export type SandboxCheckState = "verified" | "blocked" | "unverified";

export const SANDBOX_CHECKS = [
  "fixtureReadOnly",
  "outputBounded",
  "hostWorkspaceUnavailable",
  "homeUnavailable",
  "parentTraversalDenied",
  "siblingWorktreeDenied",
  "symlinkDenied",
  "junctionDenied",
  "reparsePointDenied",
  "pathAliasDenied",
  "hostSocketsAndDevicesUnavailable",
  "parentEnvironmentScrubbed",
  "credentialEnvironmentAbsent",
  "dummyCredentialFilesUnavailable",
  "editorAndGitCredentialsUnavailable",
  "networkNamespaceDisabled",
  "outboundEgressBlocked",
  "dnsEgressBlocked",
  "cpuLimitEnforced",
  "memoryLimitEnforced",
  "processLimitEnforced",
  "wallClockLimitEnforced",
  "outputSizeLimitEnforced",
  "ownedFixtureRemoved",
  "unownedTargetPreserved",
  "duplicateRunRejected",
] as const;

export type SandboxCheck = (typeof SANDBOX_CHECKS)[number];

export interface SandboxProbeEvidence {
  readonly sandboxType: "podman-rootless" | "windows-sandbox";
  readonly imageDigest?: string;
  readonly fixtureId: string;
  readonly checks: Readonly<Record<SandboxCheck, SandboxCheckState>>;
}

export interface SandboxPreflight {
  readonly sandboxType: "podman-rootless" | "windows-sandbox" | "unavailable";
  readonly imageDigest: string | null;
  readonly fixtureId: string | null;
  readonly filesystemIsolation: SandboxCheckState;
  readonly credentialIsolation: SandboxCheckState;
  readonly egressPolicy: SandboxCheckState;
  readonly resourceLimits: SandboxCheckState;
  readonly cleanupVerified: boolean;
  readonly status: SandboxCheckState;
  readonly sanitizedReasons: readonly string[];
}

export interface DisposableOfflineFixture {
  readonly id: string;
  readonly root: string;
  readonly input: string;
  readonly output: string;
}

const CATEGORY_CHECKS = {
  filesystemIsolation: [
    "fixtureReadOnly",
    "outputBounded",
    "hostWorkspaceUnavailable",
    "homeUnavailable",
    "parentTraversalDenied",
    "siblingWorktreeDenied",
    "symlinkDenied",
    "junctionDenied",
    "reparsePointDenied",
    "pathAliasDenied",
    "hostSocketsAndDevicesUnavailable",
  ],
  credentialIsolation: [
    "parentEnvironmentScrubbed",
    "credentialEnvironmentAbsent",
    "dummyCredentialFilesUnavailable",
    "editorAndGitCredentialsUnavailable",
  ],
  egressPolicy: [
    "networkNamespaceDisabled",
    "outboundEgressBlocked",
    "dnsEgressBlocked",
  ],
  resourceLimits: [
    "cpuLimitEnforced",
    "memoryLimitEnforced",
    "processLimitEnforced",
    "wallClockLimitEnforced",
    "outputSizeLimitEnforced",
  ],
} as const satisfies Record<string, readonly SandboxCheck[]>;

function isFixtureId(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(
    value,
  );
}

function isImageDigest(value: string): boolean {
  return /^sha256:[0-9a-f]{64}$/i.test(value);
}

function categoryState(
  checks: Readonly<Record<SandboxCheck, SandboxCheckState>>,
  names: readonly SandboxCheck[],
): SandboxCheckState {
  const states = names.map((name) => checks[name]);
  if (states.includes("blocked")) {
    return "blocked";
  }
  return states.every((state) => state === "verified")
    ? "verified"
    : "unverified";
}

function overallState(states: readonly SandboxCheckState[]): SandboxCheckState {
  if (states.includes("blocked")) {
    return "blocked";
  }
  return states.every((state) => state === "verified")
    ? "verified"
    : "unverified";
}

export function buildSandboxPreflight(
  evidence?: SandboxProbeEvidence,
): SandboxPreflight {
  const checks = evidence?.checks;
  const filesystemIsolation = checks === undefined
    ? "unverified"
    : categoryState(checks, CATEGORY_CHECKS.filesystemIsolation);
  const credentialIsolation = checks === undefined
    ? "unverified"
    : categoryState(checks, CATEGORY_CHECKS.credentialIsolation);
  const egressPolicy = checks === undefined
    ? "unverified"
    : categoryState(checks, CATEGORY_CHECKS.egressPolicy);
  const resourceLimits = checks === undefined
    ? "unverified"
    : categoryState(checks, CATEGORY_CHECKS.resourceLimits);
  const cleanupChecks = ["ownedFixtureRemoved", "unownedTargetPreserved", "duplicateRunRejected"] as const;
  const cleanupVerified =
    checks !== undefined &&
    cleanupChecks.every((name) => checks[name] === "verified");
  const sanitizedReasons: string[] = [];

  if (evidence === undefined) {
    sanitizedReasons.push("runtime-provider-unavailable");
  } else {
    if (!isFixtureId(evidence.fixtureId)) {
      sanitizedReasons.push("fixture-id-invalid");
    }
    if (evidence.imageDigest === undefined) {
      sanitizedReasons.push("image-digest-unavailable");
    } else if (!isImageDigest(evidence.imageDigest)) {
      sanitizedReasons.push("image-digest-invalid");
    }
    for (const name of SANDBOX_CHECKS) {
      if (checks?.[name] !== "verified") {
        sanitizedReasons.push(`${name}:${checks?.[name] ?? "unverified"}`);
      }
    }
  }

  const identityState: SandboxCheckState =
    evidence === undefined ||
    !isFixtureId(evidence.fixtureId) ||
    evidence.imageDigest === undefined ||
    !isImageDigest(evidence.imageDigest)
      ? "unverified"
      : "verified";
  const status = overallState([
    identityState,
    filesystemIsolation,
    credentialIsolation,
    egressPolicy,
    resourceLimits,
    cleanupChecks.every((name) => checks?.[name] === "verified")
      ? "verified"
      : cleanupChecks.some((name) => checks?.[name] === "blocked")
        ? "blocked"
        : "unverified",
  ]);

  return Object.freeze({
    sandboxType: evidence?.sandboxType ?? "unavailable",
    imageDigest:
      evidence?.imageDigest !== undefined && isImageDigest(evidence.imageDigest)
        ? evidence.imageDigest
        : null,
    fixtureId:
      evidence !== undefined && isFixtureId(evidence.fixtureId)
        ? evidence.fixtureId
        : null,
    filesystemIsolation,
    credentialIsolation,
    egressPolicy,
    resourceLimits,
    cleanupVerified,
    status,
    sanitizedReasons: Object.freeze(sanitizedReasons),
  });
}

export function createDisposableOfflineFixture(): DisposableOfflineFixture {
  const id = randomUUID();
  const root = join(tmpdir(), `${FIXTURE_PREFIX}${id}`);
  const fixture = Object.freeze({
    id,
    root,
    input: join(root, "input"),
    output: join(root, "output"),
  });
  mkdirSync(root, { mode: 0o700 });
  try {
    writeFileSync(join(root, OWNER_FILE), id, { flag: "wx", mode: 0o600 });
    const repository = join(fixture.input, "synthetic-public-repository");
    mkdirSync(repository, { recursive: true, mode: 0o700 });
    mkdirSync(fixture.output, { mode: 0o700 });
    writeFileSync(join(repository, "README.md"), "Synthetic offline sandbox fixture.\n", {
      flag: "wx",
      mode: 0o600,
    });
    writeFileSync(
      join(repository, ".dummy-github-token"),
      "NOT-A-CREDENTIAL\n",
      { flag: "wx", mode: 0o600 },
    );
    writeFileSync(join(repository, "probe.txt"), "fixture-only\n", {
      flag: "wx",
      mode: 0o600,
    });
    fixtureGit(repository, ["init", "--quiet"]);
    fixtureGit(repository, ["config", "--local", "user.name", "Offline Sandbox Fixture"]);
    fixtureGit(repository, ["config", "--local", "user.email", "fixture@example.invalid"]);
    fixtureGit(repository, ["config", "--local", "core.autocrlf", "false"]);
    fixtureGit(repository, ["add", "--all"]);
    fixtureGit(repository, ["commit", "--quiet", "-m", "seed synthetic offline fixture"]);
  } catch (error) {
    try {
      if (!cleanupDisposableOfflineFixture(fixture)) {
        throw new Error("fixture ownership could not be verified");
      }
    } catch (cleanupError) {
      throw new AggregateError(
        [error, cleanupError],
        "Offline fixture creation failed and safe cleanup could not be verified",
      );
    }
    throw error;
  }
  return fixture;
}

export function cleanupDisposableOfflineFixture(
  fixture: DisposableOfflineFixture,
): boolean {
  if (!isFixtureId(fixture.id)) {
    return false;
  }
  const expectedRoot = resolve(tmpdir(), `${FIXTURE_PREFIX}${fixture.id}`);
  if (
    resolve(fixture.root) !== expectedRoot ||
    basename(fixture.root) !== `${FIXTURE_PREFIX}${fixture.id}`
  ) {
    return false;
  }
  if (!lstatExists(fixture.root)) {
    return false;
  }
  const rootStat = lstatSync(fixture.root);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    return false;
  }
  const canonicalTemp = realpathSync(tmpdir());
  const canonicalRoot = realpathSync(fixture.root);
  if (
    dirname(canonicalRoot) !== canonicalTemp ||
    basename(canonicalRoot) !== `${FIXTURE_PREFIX}${fixture.id}`
  ) {
    return false;
  }
  const ownerPath = join(fixture.root, OWNER_FILE);
  if (!lstatExists(ownerPath)) {
    return false;
  }
  const ownerStat = lstatSync(ownerPath);
  if (!ownerStat.isFile() || ownerStat.isSymbolicLink()) {
    return false;
  }
  if (readFileSync(ownerPath, "utf8") !== fixture.id) {
    return false;
  }
  rmSync(fixture.root, { recursive: true });
  return true;
}

function lstatExists(path: string): boolean {
  try {
    lstatSync(path);
    return true;
  } catch (error) {
    if (
      typeof error === "object" &&
      error !== null &&
      "code" in error &&
      error.code === "ENOENT"
    ) {
      return false;
    }
    throw error;
  }
}
