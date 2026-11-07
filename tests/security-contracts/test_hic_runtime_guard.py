"""Pure synthetic tests for the source-level guard; no processes/containers started."""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("hic_runtime_guard.py")
spec = importlib.util.spec_from_file_location("hic_runtime_guard", SCRIPT)
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RuntimeBoundaryGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.native = self.root / "src" / "NativeHost"
        self.native.mkdir(parents=True)
        self.program = self.native / "Program.cs"
        self.program.write_text(
            'var token = Environment.GetEnvironmentVariable("HIC_NATIVE_TOKEN");\n'
            'app.MapGet("/v1/status", () => new { service = "hic-native-host", taskExecutionEnabled = false });\n'
            'kestrel.Listen(IPAddress.Loopback, port);\n'
            'kestrel.Limits.MaxRequestBodySize = 1024;\n'
            'app.MapPost("/v1/actions/run", async (ctx) => {\n'
            '  NativeActionPolicy.ValidToken(candidate, expected);\n'
            '  NativeActionPolicy.AllowedPayload(payload);\n'
            '});\n', encoding="utf-8")
        self.policy = self.native / "NativeActionPolicy.cs"
        self.policy.write_text(
            'internal const string Action = "workspace.inspect";\n'
            'if (count > 1 || property.Value.GetString() != Action) return false;\n'
            'return count == 1;\n', encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def problems(self):
        return module.inspect_repo(self.root)

    def test_clean_read_only_fixture(self):
        self.assertEqual([], self.problems())

    def test_detects_enabled_execution(self):
        self.program.write_text(self.program.read_text().replace(
            "taskExecutionEnabled = false", "taskExecutionEnabled = true"))
        self.assertTrue(any("execution-disabled" in item for item in self.problems()))

    def test_detects_public_listener(self):
        self.program.write_text(self.program.read_text().replace(
            "IPAddress.Loopback", "IPAddress.Any"))
        self.assertTrue(any("loopback-only" in item for item in self.problems()))

    def test_detects_additional_native_route(self):
        with self.program.open("a", encoding="utf-8") as out:
            out.write('app.MapPost("/v1/shell", handler);\n')
        self.assertTrue(any("POST route set changed" in item for item in self.problems()))

    def test_detects_manual_v3_wired_into_runtime(self):
        scripts = self.root / "scripts"
        scripts.mkdir()
        (scripts / "bad-startup.ps1").write_text(
            "& psql -f 003-native-run-approval-ledger.sql", encoding="utf-8")
        self.assertTrue(any("manual-only schema v3" in item for item in self.problems()))

    def test_detects_unreviewed_signer_callsite(self):
        cp = self.root / "src" / "ControlPlane"
        cp.mkdir()
        (cp / "Unsafe.cs").write_text(
            "var signed = NativeApprovalReceiptV2Issuer.Sign(request, key, id, now);",
            encoding="utf-8")
        self.assertTrue(any("issuer call site" in item for item in self.problems()))

    def test_discovery_survives_directory_move(self):
        target = self.root / "agent-lab"
        target.mkdir()
        self.native.rename(target / "hic-native-host")
        self.assertEqual([], self.problems())


if __name__ == "__main__":
    unittest.main()
