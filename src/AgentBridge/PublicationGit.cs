using System.Diagnostics;
using System.Text;

namespace Hic.AgentBridge;

public sealed class PublicationGit(string directory, string? credentialHelper = null)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public string Text(params string[] args) => Encoding.UTF8.GetString(Run(args)).TrimEnd('\r', '\n');

    public byte[] Run(string[] args, byte[]? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var key in start.Environment.Keys.Where(k =>
            k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var option in new[] { "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false",
            "-c", "core.autocrlf=false", "-c", "core.hooksPath=" + Path.Combine(DirectoryPath, "disabled-hooks"),
            "-c", "protocol.allow=never", "-c", "protocol.file.allow=always",
            "-c", "protocol.https.allow=always", "-c", "http.followRedirects=false",
            "-c", "fetch.fsckObjects=true", "-c", "transfer.fsckObjects=true",
            "-c", "credential.helper=", "-c", "credential.useHttpPath=true" })
            start.ArgumentList.Add(option);
        if (credentialHelper is not null)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("credential.helper=" + credentialHelper);
        }
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new BridgeException("PUBLICATION_GIT_START_FAILED");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var stdout = ReadBounded(process.StandardOutput.BaseStream);
        var stdin = WriteInput(process.StandardInput.BaseStream, input);
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new BridgeException("PUBLICATION_GIT_TIMEOUT_OUTCOME_UNKNOWN");
        }
        stdin.GetAwaiter().GetResult();
        stderr.GetAwaiter().GetResult();
        var output = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new BridgeException("PUBLICATION_GIT_FAILED_OUTCOME_UNKNOWN");
        return output;
    }

    private static async Task WriteInput(Stream stream, byte[]? bytes)
    {
        await using (stream)
        {
            if (bytes is not null)
                await stream.WriteAsync(bytes);
        }
    }

    private static async Task<byte[]> ReadBounded(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        var exceeded = false;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > 1_048_576)
                exceeded = true;
            if (!exceeded)
                buffer.Write(chunk, 0, count);
        }
        if (exceeded)
            throw new BridgeException("PUBLICATION_GIT_OUTPUT_SIZE");
        return buffer.ToArray();
    }

    public string? BlobId(string commit, string path)
    {
        Safety.Sha(commit);
        var line = Text("ls-tree", commit, "--", path);
        if (line.Length == 0)
            return null;
        var parts = line.Split('\t');
        var fields = parts[0].Split(' ');
        if (parts.Length != 2 || parts[1] != path || fields.Length != 3 || fields[0] != "100644" || fields[1] != "blob")
            throw new BridgeException("INVALID_REMOTE_FILE_MODE");
        Safety.Sha(fields[2]);
        return fields[2];
    }

    public byte[] Blob(string commit, string path)
    {
        var oid = BlobId(commit, path) ?? throw new BridgeException("REMOTE_ARTIFACT_MISSING");
        if (!int.TryParse(Text("cat-file", "-s", oid), out var size) || size is < 1 or > CanonicalJson.MaxBytes)
            throw new BridgeException("REMOTE_ARTIFACT_SIZE");
        return Run(["cat-file", "blob", oid]);
    }
}
