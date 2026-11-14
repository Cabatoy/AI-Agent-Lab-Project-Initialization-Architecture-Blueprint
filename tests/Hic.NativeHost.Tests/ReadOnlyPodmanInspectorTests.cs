using Xunit;

public sealed class ReadOnlyPodmanInspectorTests
{
    private const string ValidInfo =
        """{"host":{"security":{"rootless":true},"cgroupVersion":"v2"}}""";

    [Fact]
    public async Task HostInfoDoesNotProveContainerUserNamespaceIsolation()
    {
        var inspector = Inspector(new PodmanInfoResult(0, ValidInfo, ""));

        var observation = await inspector.InspectAsync(CancellationToken.None);

        Assert.True(observation.Rootless);
        Assert.Equal(1000, observation.EffectiveUid);
        Assert.Equal("v2", observation.CgroupVersion);
        Assert.False(observation.UserNamespaceIsolated);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"host":{"security":{"rootless":true}}}""")]
    [InlineData("""{"host":{"security":{"rootless":"true"},"cgroupVersion":"v2"}}""")]
    [InlineData("""{"host":{"security":{"rootless":true},"cgroupVersion":""}}""")]
    public async Task MalformedOrIncompleteInfoFailsClosed(string output)
    {
        var inspector = Inspector(new PodmanInfoResult(0, output, ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inspector.InspectAsync(CancellationToken.None));

        Assert.Equal("Podman inspection response is invalid.", error.Message);
    }

    [Fact]
    public async Task NonzeroExitDoesNotExposeStderr()
    {
        var inspector = Inspector(new PodmanInfoResult(1, ValidInfo, "private-error"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inspector.InspectAsync(CancellationToken.None));

        Assert.Equal("Podman inspection failed.", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedOutputFailsClosed(bool standardOutput)
    {
        var oversized = new string('x', ReadOnlyPodmanInspector.MaxOutputChars + 1);
        var result = standardOutput
            ? new PodmanInfoResult(0, oversized, "")
            : new PodmanInfoResult(0, ValidInfo, oversized);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Inspector(result).InspectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new PodmanInfoResult(0, ValidInfo, "");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ReadOnlyPodmanInspector(runner, () => 1000).InspectAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrTimedOutRunnerFailsClosed(bool timedOut)
    {
        var runner = new FakeRunner(_ => timedOut
            ? Task.FromException<PodmanInfoResult>(new TimeoutException("Podman inspection timed out."))
            : Task.FromException<PodmanInfoResult>(new InvalidOperationException("Podman inspection unavailable.")));
        var inspector = new ReadOnlyPodmanInspector(runner, () => 1000);

        if (timedOut)
            await Assert.ThrowsAsync<TimeoutException>(() => inspector.InspectAsync(CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InspectAsync(CancellationToken.None));
    }


    [Fact]
    public async Task RunnerTimeoutKillsProcess()
    {
        using var process = new LifecycleFakeProcess();
        var runner = new PodmanInfoRunner(
            () => process,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.True(process.KillCalled);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task RunnerOutputLimitKillsProcess()
    {
        using var process = new LifecycleFakeProcess
        {
            Output = new string('x', ReadOnlyPodmanInspector.MaxOutputChars + 1)
        };
        var runner = new PodmanInfoRunner(
            () => process,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.Contains("output exceeded limit", error.Message);
        Assert.True(process.KillCalled);
    }

    [Fact]
    public async Task RunnerKillFailureAndReapTimeoutFailClosed()
    {
        using var process = new LifecycleFakeProcess
        {
            KillThrows = true
        };
        var runner = new PodmanInfoRunner(
            () => process,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<PodmanCleanupException>(
            () => runner.RunAsync(CancellationToken.None));

        Assert.True(process.KillCalled);
        Assert.Equal("timed-out", error.OriginalFailure);
        Assert.Equal(
            "termination-failed-and-reap-timed-out",
            error.CleanupFailure);
    }

    private sealed class LifecycleFakeProcess : IPodmanInfoProcess
    {
        public string Output { get; init; } = "";
        public bool KillThrows { get; init; }
        public bool KillCalled { get; private set; }
        public bool HasExited { get; private set; }
        public int ExitCode => 0;

        private TextReader? outputReader;
        private TextReader? errorReader;

        public TextReader StandardOutput =>
            outputReader ??= new StringReader(Output);

        public TextReader StandardError =>
            errorReader ??= new StringReader("");

        public bool Start() => true;

        public void Kill()
        {
            KillCalled = true;
            if (KillThrows)
                throw new InvalidOperationException("Synthetic kill failure.");
            HasExited = true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            HasExited
                ? Task.CompletedTask
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public void Dispose()
        {
            outputReader?.Dispose();
            errorReader?.Dispose();
        }
    }

    private static ReadOnlyPodmanInspector Inspector(PodmanInfoResult result) =>
        new(new FakeRunner(_ => Task.FromResult(result)), () => 1000);

    private sealed class FakeRunner(Func<CancellationToken, Task<PodmanInfoResult>> run) : IPodmanInfoRunner
    {
        public Task<PodmanInfoResult> RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }
}
