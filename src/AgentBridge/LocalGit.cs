using System.Diagnostics;

namespace Hic.AgentBridge;

public interface ILocalGit
{
    Source ReadSource();
    byte[] ReadApprovedBlob(string commit, string path);
}

public sealed class LocalGit(string repositoryRoot, string expectedRepository = SnapshotProducer.Repository) : ILocalGit
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));

    public Source ReadSource()
    {
        Safety.Repository(expectedRepository);
        var top = Text("rev-parse", "--show-toplevel");
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)), root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new BridgeException("REPOSITORY_ROOT_REQUIRED");
        var origin = Text("config", "--local", "--no-includes", "--get", "remote.origin.url");
        if (origin != $"https://github.com/{expectedRepository}.git"
            && origin != $"git@github.com:{expectedRepository}.git")
            throw new BridgeException("REPOSITORY_NOT_ALLOWED");
        var source = new Source(expectedRepository, Text("branch", "--show-current"),
            Text("rev-parse", "HEAD"), Text("status", "--porcelain", "--untracked-files=normal").Length != 0);
        SnapshotProducer.ValidateSource(source);
        return source;
    }

    public byte[] ReadApprovedBlob(string commit, string path)
    {
        Safety.Sha(commit);
        Safety.Reference(path);
        var entry = Text("ls-tree", commit, "--", path).Split('\t')[0].Split(' ');
        if (entry.Length != 3 || entry[1] != "blob" || entry[0] is not ("100644" or "100755"))
            throw new BridgeException("COMMITTED_REGULAR_FILE_REQUIRED");
        Safety.Sha(entry[2]);
        if (!long.TryParse(Text("cat-file", "-s", entry[2]), out var size) || size is < 0 or > 1_048_576)
            throw new BridgeException("ARTIFACT_SIZE");
        return Run("cat-file", "blob", entry[2]);
    }

    private string Text(params string[] args) =>
        System.Text.Encoding.UTF8.GetString(Run(args)).TrimEnd('\r', '\n');

    private byte[] Run(params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        info.ArgumentList.Add("--no-pager");
        info.ArgumentList.Add("--no-optional-locks");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.fsmonitor=false");
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new BridgeException("GIT_START_FAILED");
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var output = ReadOutput(process.StandardOutput.BaseStream);
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new BridgeException("GIT_TIMEOUT");
        }
        var bytes = output.GetAwaiter().GetResult();
        error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new BridgeException("GIT_READ_FAILED");
        return bytes;
    }

    private static async Task<byte[]> ReadOutput(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > 1_048_576)
                throw new BridgeException("GIT_OUTPUT_SIZE");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
