using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

internal sealed record PodmanInfoResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IPodmanInfoRunner
{
    Task<PodmanInfoResult> RunAsync(CancellationToken cancellationToken);
}

internal interface IPodmanInfoProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    bool Start();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class PodmanCleanupException(string originalFailure, string cleanupFailure)
    : InvalidOperationException($"Podman inspection {originalFailure}; cleanup {cleanupFailure}.")
{
    internal string OriginalFailure { get; } = originalFailure;
    internal string CleanupFailure { get; } = cleanupFailure;
}

internal sealed class ReadOnlyPodmanInspector : IReadOnlyPodmanInspector
{
    internal const int MaxOutputChars = 65536;
    private readonly IPodmanInfoRunner runner;
    private readonly Func<int> readEffectiveUid;

    internal ReadOnlyPodmanInspector()
        : this(new PodmanInfoRunner(), ReadEffectiveUid)
    {
    }

    internal ReadOnlyPodmanInspector(IPodmanInfoRunner runner, Func<int> readEffectiveUid)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.readEffectiveUid = readEffectiveUid ?? throw new ArgumentNullException(nameof(readEffectiveUid));
    }

    public async Task<PodmanRuntimeObservation> InspectAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await runner.RunAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Podman inspection failed.");
        if (result.StandardOutput is null || result.StandardError is null ||
            result.StandardOutput.Length > MaxOutputChars ||
            result.StandardError.Length > MaxOutputChars)
            throw new InvalidOperationException("Podman inspection output is invalid.");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object ||
                !host.TryGetProperty("security", out var security) || security.ValueKind != JsonValueKind.Object ||
                !security.TryGetProperty("rootless", out var rootless) ||
                rootless.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !host.TryGetProperty("cgroupVersion", out var cgroup) ||
                cgroup.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(cgroup.GetString()))
                throw new InvalidOperationException("Podman inspection response is invalid.");

            var uid = readEffectiveUid();
            if (uid < 0)
                throw new InvalidOperationException("Effective UID is invalid.");

            return new PodmanRuntimeObservation(
                rootless.GetBoolean(),
                uid,
                UserNamespaceIsolated: false,
                cgroup.GetString());
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Podman inspection response is invalid.");
        }
    }

    private static int ReadEffectiveUid()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Podman inspection requires Linux.");

        var line = File.ReadLines("/proc/self/status")
            .First(value => value.StartsWith("Uid:", StringComparison.Ordinal));

        var fields = line.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);

        return int.Parse(
            fields[2],
            System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class PodmanInfoRunner : IPodmanInfoRunner
{
    private readonly Func<IPodmanInfoProcess> createProcess;
    private readonly TimeSpan timeout;
    private readonly TimeSpan cleanupTimeout;

    internal PodmanInfoRunner()
        : this(() => new PodmanInfoProcess(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2))
    {
    }

    internal PodmanInfoRunner(
        Func<IPodmanInfoProcess> createProcess,
        TimeSpan timeout,
        TimeSpan cleanupTimeout)
    {
        this.createProcess = createProcess ?? throw new ArgumentNullException(nameof(createProcess));
        if (timeout <= TimeSpan.Zero || cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        this.cleanupTimeout = cleanupTimeout;
    }

    public async Task<PodmanInfoResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Podman inspection requires Linux.");
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = createProcess();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Podman inspection unavailable.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Podman inspection unavailable.");
        }

        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        var exit = process.WaitForExitAsync(deadline.Token);
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        try
        {
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Append(cancelled));
                await completed;
                pending.Remove(completed);
            }

            return new PodmanInfoResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception error)
        {
            var failure = error is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out"
                : error is PodmanOutputLimitException ? "output-limit" : "failed";
            deadline.Cancel();
            var cleanupFailure = await CleanupAsync(process);

            _ = Task.WhenAll(stdout, stderr, exit).ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            if (cleanupFailure is not null)
                throw new PodmanCleanupException(failure, cleanupFailure);
            if (failure == "timed-out")
                throw new TimeoutException("Podman inspection timed out.");
            if (failure == "cancelled")
                throw new OperationCanceledException(cancellationToken);
            if (failure == "output-limit")
                throw new InvalidOperationException("Podman inspection output exceeded limit.");
            throw new InvalidOperationException("Podman inspection failed.");
        }
    }

    private async Task<string?> CleanupAsync(IPodmanInfoProcess process)
    {
        var killFailed = false;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
                catch (Exception)
                {
                    killFailed = true;
                }
            }
        }
        catch (Exception)
        {
            killFailed = true;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(cleanupTimeout);
        }
        catch (TimeoutException)
        {
            return killFailed ? "termination-failed-and-reap-timed-out" : "reap-timed-out";
        }
        catch (Exception)
        {
            return killFailed ? "termination-and-reap-failed" : "reap-failed";
        }

        return killFailed ? "termination-failed" : null;
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
                return result.ToString();
            if (result.Length + count > ReadOnlyPodmanInspector.MaxOutputChars)
                throw new PodmanOutputLimitException();
            result.Append(buffer, 0, count);
        }
    }

    private sealed class PodmanOutputLimitException : Exception
        {
        }
    }

    internal sealed class PodmanInfoProcess : IPodmanInfoProcess
    {
        private readonly Process process;

        internal PodmanInfoProcess()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/podman",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("info");
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("json");
            process = new Process { StartInfo = startInfo };
        }

        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public bool Start() => process.Start();
        public void Kill() => process.Kill(entireProcessTree: true);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
