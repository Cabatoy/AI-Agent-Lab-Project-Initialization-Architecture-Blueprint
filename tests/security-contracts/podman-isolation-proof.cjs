'use strict';

// This is an offline capability probe, not the HIC execution backend.
const { spawnSync } = require('node:child_process');

const image = '3386778a73b501d3918c3dc26685bd284aa632ca06f2f99e6bf642a3b325334b';
const script = `set -eu
fail() { echo "FAIL: $1" >&2; exit 1; }
[ "$(id -u)" = 10000 ] || fail user
grep -q '^CapEff:[[:space:]]*0000000000000000$' /proc/self/status || fail capabilities
grep -q '^NoNewPrivs:[[:space:]]*1$' /proc/self/status || fail no-new-privileges
[ -z "\${HIC_PROOF_HOST_SENTINEL+x}" ] || fail environment
[ ! -e /run/podman/podman.sock ] || fail podman-socket
[ ! -e /var/run/docker.sock ] || fail docker-socket
[ ! -e /mnt/c/Enterprise ] || fail host-workspace
[ ! -e /mnt/c/Envr ] || fail host-credentials
[ "$(ls /sys/class/net)" = lo ] || fail network-interface
if curl --noproxy '*' --connect-timeout 2 --max-time 3 -sS http://1.1.1.1/ >/dev/null 2>&1; then fail network; fi
if touch /etc/hic-proof-write 2>/dev/null; then fail read-only-root; fi
mkdir /tmp/private
if touch /tmp/private/../../etc/hic-proof-write 2>/dev/null; then fail traversal; fi
printf 'original\\n' > /tmp/private/source
ln -s /etc/hic-proof-write /tmp/private/link
if printf 'change\\n' > /tmp/private/link 2>/dev/null; then fail symlink; fi
mv /tmp/private/source /tmp/private/old
printf 'replacement\\n' > /tmp/private/source
[ "$(cat /tmp/private/old)" = original ] || fail snapshot
[ ! -e /etc/hic-proof-write ] || fail escape
cat > /tmp/private/Proof.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>
EOF
cat > /tmp/private/NuGet.Config <<'EOF'
<configuration><packageSources><clear /></packageSources></configuration>
EOF
printf 'public static class Proof { public static int Value => 1; }\\n' > /tmp/private/Proof.cs
cd /tmp/private
dotnet build Proof.csproj --configfile NuGet.Config --verbosity quiet --nologo >/dev/null || fail isolated-build
echo 'PASS: rootless uid, capabilities, no-new-privileges, no host mount or credential sentinel, no socket, no network, read-only root, confined symlink/swap and offline synthetic build'
`;

const args = [
  'run', '--rm', '-i', '--label', 'hic.isolation.proof=true', '--pull', 'never', '--network', 'none',
  '--read-only', '--tmpfs', '/tmp:rw,nosuid,nodev,size=64m',
  '--user', '10000:10000', '--cap-drop', 'ALL',
  '--env', 'HOME=/tmp', '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
  '--security-opt', 'no-new-privileges', '--entrypoint', '/bin/sh', image,
];
const env = { ...process.env, HIC_PROOF_HOST_SENTINEL: 'synthetic-only' };
const result = spawnSync('podman', args, {
  input: script,
  encoding: 'utf8',
  timeout: 60000,
  maxBuffer: 65536,
  env,
  windowsHide: true,
});
if (result.error || result.status !== 0) {
  console.error('Isolation proof failed:', result.error?.message ?? result.stderr);
  process.exitCode = 1;
} else {
  process.stdout.write(result.stdout);
}
