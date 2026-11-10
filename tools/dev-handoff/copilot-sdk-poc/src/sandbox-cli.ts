import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildSandboxPreflight } from "./offline-sandbox.ts";

export function runOfflineSandboxPreflight(
  args: readonly string[],
  writeOutput: (text: string) => void = (text) => process.stdout.write(text),
  writeError: (text: string) => void = (text) => process.stderr.write(text),
): number {
  if (args.length !== 1 || args[0] !== "--offline-fixture") {
    writeError("Usage: npm run sandbox -- --offline-fixture\n");
    return 2;
  }
  writeOutput(`${JSON.stringify(buildSandboxPreflight(), null, 2)}\n`);
  return 1;
}

if (
  process.argv[1] !== undefined &&
  fileURLToPath(import.meta.url) === resolve(process.argv[1])
) {
  process.exitCode = runOfflineSandboxPreflight(process.argv.slice(2));
}
