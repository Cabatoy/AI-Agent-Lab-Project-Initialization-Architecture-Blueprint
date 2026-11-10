import { existsSync } from "node:fs";
import { delimiter, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { PINNED_SDK_VERSION } from "./contracts.ts";

export const DECLARED_BUNDLED_RUNTIME_VERSION = "1.0.108";

export interface ReadinessPreflight {
  readonly sdk: {
    readonly package: "@github/copilot-sdk";
    readonly pinnedVersion: string;
    readonly bundledRuntimeVersionClaim: string;
    readonly runtimeInvoked: false;
  };
  readonly cli: {
    readonly namePresentOnPath: boolean;
    readonly presenceIsAuthoritative: false;
    readonly versionQueryRun: false;
  };
  readonly authentication: "unknown";
  readonly modelEntitlement: "unknown";
  readonly quotaOrBilling: "unknown";
  readonly credentialsInspected: false;
  readonly networkAccessed: false;
  readonly modelRequestMade: false;
}

export function isCopilotCliOnPath(pathValue = process.env.PATH ?? ""): boolean {
  const extensions =
    process.platform === "win32" ? ["", ".exe", ".cmd", ".bat"] : [""];
  return pathValue.split(delimiter).some((directory) =>
    extensions.some((extension) => existsSync(join(directory, `copilot${extension}`))),
  );
}

export function collectReadinessPreflight(
  pathValue = process.env.PATH ?? "",
): ReadinessPreflight {
  return Object.freeze({
    sdk: Object.freeze({
      package: "@github/copilot-sdk",
      pinnedVersion: PINNED_SDK_VERSION,
      bundledRuntimeVersionClaim: DECLARED_BUNDLED_RUNTIME_VERSION,
      runtimeInvoked: false,
    }),
    cli: Object.freeze({
      namePresentOnPath: isCopilotCliOnPath(pathValue),
      presenceIsAuthoritative: false,
      versionQueryRun: false,
    }),
    authentication: "unknown",
    modelEntitlement: "unknown",
    quotaOrBilling: "unknown",
    credentialsInspected: false,
    networkAccessed: false,
    modelRequestMade: false,
  });
}

if (
  process.argv[1] !== undefined &&
  fileURLToPath(import.meta.url) === resolve(process.argv[1])
) {
  process.stdout.write(`${JSON.stringify(collectReadinessPreflight(), null, 2)}\n`);
}
