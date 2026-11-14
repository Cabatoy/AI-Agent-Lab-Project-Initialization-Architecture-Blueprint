using System.Text;
using System.Text.Json.Nodes;
using Hic.AgentBridge;

namespace AgentBridge.Tests;

public sealed class PublicationTests : IDisposable
{
    private const string SourceRepository = "synthetic-owner/source";
    private static readonly PublicationTarget Destination = new("synthetic-owner/destination", "handoffs/synthetic");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "hic-publication-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string source;
    private readonly string remote;
    private readonly string workspace;
    private readonly PublicationGit sourceGit;
    private readonly PublicationGit remoteGit;
    private readonly string sourceCommit;
    private readonly string baseCommit;
    private readonly Snapshot snapshot;
    private readonly PublicationTransport transport;

    public PublicationTests()
    {
        Directory.CreateDirectory(root);
        source = Path.Combine(root, "source");
        remote = Path.Combine(root, "remote.git");
        workspace = Path.Combine(root, "publication");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(remote);
        sourceGit = new(source);
        remoteGit = new(remote);
        sourceGit.Run(["init", "--quiet", "--initial-branch=feature/source"]);
        sourceGit.Run(["config", "--local", "remote.origin.url", $"https://github.com/{SourceRepository}.git"]);
        Directory.CreateDirectory(Path.Combine(source, "docs"));
        File.WriteAllText(Path.Combine(source, "docs", "result.md"), "Synthetic approved result");
        sourceGit.Run(["add", "--", "docs/result.md"]);
        sourceCommit = Commit(sourceGit, null, "Synthetic source");
        sourceGit.Run(["update-ref", "refs/heads/feature/source", sourceCommit]);
        remoteGit.Run(["init", "--quiet", "--bare", "--initial-branch=" + Destination.Branch]);
        remoteGit.Run(["config", "--local", "hic.bridge.repository", Destination.Repository]);
        remoteGit.Run(["read-tree", "--empty"]);
        baseCommit = Commit(remoteGit, null, "Synthetic destination base");
        remoteGit.Run(["update-ref", "refs/heads/" + Destination.Branch, baseCommit]);
        transport = PublicationTransport.Local(Destination, remote);
        snapshot = WriteSnapshot(Now.AddMinutes(-5), "snapshot-foundation");
    }

    private Snapshot WriteSnapshot(DateTimeOffset observed, string summary, DateTimeOffset? validationNow = null)
    {
        var input = new SnapshotInput
        {
            ContractVersion = SnapshotProducer.Version, ProjectId = "synthetic-project", TaskId = null,
            ExecutionId = null, CorrelationId = null, SourceCommit = sourceCommit, ObservedAtUtc = observed,
            ExecutionStatus = "Unknown", Summary = summary, ChangedFiles = [],
            VerificationEvidence = [new Evidence("runtime", "NOT_RUN", "NOT_RUN", null, sourceCommit, observed)],
            KnownRisks = ["runtime-not-run"], RecommendedNextAction = "review-before-publication",
            Usage = new Usage("unknown", null, null, null, null, null),
            ArtifactReferences = [new ArtifactInput("docs/result.md")]
        };
        var bytes = CanonicalJson.Serialize(input);
        var value = new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes),
            new LocalGit(source, SourceRepository), validationNow ?? Now);
        new ArtifactStore(source).Write(value, validationNow ?? Now);
        return value;
    }

    private SnapshotPublisher Publisher(IPublicationTransport? custom = null, string? location = null,
        PublicationTarget? target = null, string? expectedSource = null) =>
        new(source, location ?? workspace, expectedSource ?? SourceRepository, target ?? Destination, custom ?? transport);

    private PublicationPreview Prepare() => Publisher().Prepare(snapshot.ProjectId, Now);

    private static string Commit(PublicationGit git, string? parent, string message)
    {
        var tree = git.Text("write-tree");
        var args = new List<string>
        {
            "-c", "user.name=Synthetic Publisher", "-c", "user.email=synthetic@localhost", "commit-tree", tree
        };
        if (parent is not null)
            args.AddRange(["-p", parent]);
        return Encoding.UTF8.GetString(git.Run(args.ToArray(), Encoding.UTF8.GetBytes(message + "\n"))).Trim();
    }

    private string RemoteHead() => remoteGit.Text("rev-parse", "refs/heads/" + Destination.Branch);
    private string JournalStage() => new PublicationWorkspace(workspace)
        .Read<PublicationJournal>(Path.Combine(workspace, "journal.json")).Stage;
    private static string SnapshotPath(Snapshot value) =>
        $"handoffs/{value.ProjectId}/snapshots/{value.SnapshotId}.json";
    private static string PointerPath(Snapshot value) => $"handoffs/{value.ProjectId}/latest.json";

    private void AdvanceRemote(Action<PublicationGit>? edit = null)
    {
        var current = RemoteHead();
        remoteGit.Run(["read-tree", current]);
        edit?.Invoke(remoteGit);
        var next = Commit(remoteGit, current, "Synthetic concurrent update");
        remoteGit.Run(["update-ref", "refs/heads/" + Destination.Branch, next, current]);
    }

    private static void StageBytes(PublicationGit git, string path, byte[] bytes)
    {
        var blob = Encoding.UTF8.GetString(git.Run(["hash-object", "-w", "--stdin"], bytes)).Trim();
        git.Run(["update-index", "--add", "--cacheinfo", "100644," + blob + "," + path]);
    }

    [Fact]
    public void ValidCandidateHasExactTwoPathManifestAndReadableApprovalPreview()
    {
        var preview = Prepare();
        Assert.Equal("hic.publication/1.0", preview.Manifest.ContractVersion);
        Assert.Equal(Destination, preview.Manifest.Destination);
        Assert.Equal(baseCommit, preview.Manifest.ExpectedRemoteCommit);
        Assert.Equal(sourceCommit, preview.Manifest.Source.CommitSha);
        Assert.Equal(snapshot.SnapshotId, preview.Manifest.SnapshotId);
        Assert.Equal(SnapshotPublisher.Digest(preview.Manifest), preview.Digest);
        Assert.Equal([PointerPath(snapshot), SnapshotPath(snapshot)],
            preview.Manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(preview.Manifest.Files, f => Assert.Null(f.PreviousBlob));
        Assert.Contains(preview.Digest, preview.Text);
        Assert.Contains(Destination.Repository, preview.Text);
        Assert.Contains("unknown", preview.Text);
        Assert.Equal(baseCommit, RemoteHead());
        Assert.Equal("prepared", JournalStage());
        Assert.InRange(CanonicalJson.Serialize(preview.Manifest).Length, 1, 16_384);
    }

    [Fact]
    public void ExplicitApprovalAndExactDigestRequiredBeforeAnyCommit()
    {
        var candidate = Prepare();
        Assert.Equal("EXPLICIT_PUBLICATION_APPROVAL_REQUIRED",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, false, Now)).Message);
        Assert.Equal("PUBLICATION_APPROVAL_DIGEST_MISMATCH",
            Assert.Throws<BridgeException>(() => Publisher().Publish(new string('0', 64), true, Now)).Message);
        Assert.Equal("prepared", JournalStage());
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void ChangedLocalCandidateInvalidatesPreviousApproval()
    {
        var candidate = Prepare();
        WriteSnapshot(Now.AddMinutes(-1), "work-pending");
        Assert.Equal("PUBLICATION_CANDIDATE_CHANGED",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(baseCommit, RemoteHead());
        Assert.Equal("prepared", JournalStage());
    }

    [Fact]
    public void ManifestTamperingCannotReuseApproval()
    {
        var candidate = Prepare();
        var state = new PublicationWorkspace(workspace);
        state.Save(state.ManifestPath, candidate.Manifest with { ProjectId = "changed-project" });
        Assert.Equal("PUBLICATION_APPROVAL_DIGEST_MISMATCH",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
    }

    [Fact]
    public void WrongDestinationOwnerOrRepositoryRejected()
    {
        var wrong = Destination with { Repository = "other-owner/destination" };
        Assert.Equal("DESTINATION_NOT_APPROVED",
            Assert.Throws<BridgeException>(() => Publisher(target: wrong).Prepare(snapshot.ProjectId, Now)).Message);
        Assert.False(Directory.Exists(workspace));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/hic-agent-bridge")]
    [InlineData("--all")]
    [InlineData("handoffs/a:main")]
    [InlineData("handoffs/a/../../main")]
    public void WrongBranchAndGitArgumentsRejected(string branch)
    {
        Assert.Throws<BridgeException>(() =>
            Publisher(target: Destination with { Branch = branch }).Prepare(snapshot.ProjectId, Now));
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void DifferentValidPublicationBranchStillRequiresNewApproval()
    {
        var candidate = Prepare();
        Assert.Throws<BridgeException>(() => Publisher(target: Destination with { Branch = "handoffs/other" })
            .Publish(candidate.Digest, true, Now));
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void WrongSourceRepositoryRejected()
    {
        Assert.Equal("REPOSITORY_NOT_ALLOWED", Assert.Throws<BridgeException>(() =>
            Publisher(expectedSource: "other-owner/source").Prepare(snapshot.ProjectId, Now)).Message);
        Assert.False(Directory.Exists(workspace));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CanonicalOriginSupportsDefaultAndExplicitIdentity(bool ssh, bool explicitIdentity)
    {
        Assert.Equal("Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint",
            SnapshotProducer.Repository);
        var repository = SnapshotProducer.Repository;
        sourceGit.Run(["config", "--local", "remote.origin.url",
            ssh ? $"git@github.com:{repository}.git" : $"https://github.com/{repository}.git"]);
        var reader = explicitIdentity ? new LocalGit(source, repository) : new LocalGit(source);
        Assert.Equal(repository, reader.ReadSource().Repository);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyOriginRequiresExplicitIdentityWithoutFallback(bool ssh)
    {
        const string legacy = "cahatayOzdemir/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint";
        sourceGit.Run(["config", "--local", "remote.origin.url",
            ssh ? $"git@github.com:{legacy}.git" : $"https://github.com/{legacy}.git"]);
        Assert.Equal("REPOSITORY_NOT_ALLOWED",
            Assert.Throws<BridgeException>(() => new LocalGit(source).ReadSource()).Message);
        Assert.Equal(legacy, new LocalGit(source, legacy).ReadSource().Repository);
    }

    [Fact]
    public void CanonicalOriginRejectsExplicitLegacyIdentity()
    {
        sourceGit.Run(["config", "--local", "remote.origin.url",
            $"https://github.com/{SnapshotProducer.Repository}.git"]);
        Assert.Equal("REPOSITORY_NOT_ALLOWED", Assert.Throws<BridgeException>(() =>
            new LocalGit(source,
                "cahatayOzdemir/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint")
                .ReadSource()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultGeneratorCliUsesCanonicalOriginAndRejectsLegacy(bool legacy)
    {
        var repository = legacy
            ? "cahatayOzdemir/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint"
            : SnapshotProducer.Repository;
        sourceGit.Run(["config", "--local", "remote.origin.url", $"https://github.com/{repository}.git"]);
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var input = new SnapshotInput
        {
            ContractVersion = SnapshotProducer.Version, ProjectId = "synthetic-project",
            TaskId = null, ExecutionId = null, CorrelationId = null, SourceCommit = sourceCommit,
            ObservedAtUtc = observed, ExecutionStatus = "Unknown",
            Summary = "snapshot-foundation", ChangedFiles = [],
            VerificationEvidence = [new Evidence("runtime", "NOT_RUN", "NOT_RUN", null,
                sourceCommit, observed)],
            KnownRisks = ["runtime-not-run"], RecommendedNextAction = "review-before-publication",
            Usage = new Usage("unknown", null, null, null, null, null), ArtifactReferences = []
        };
        var bytes = CanonicalJson.Serialize(input);
        File.WriteAllBytes(Path.Combine(source, "docs", "input.json"), bytes);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(SnapshotProducer).Assembly.Location,
            "generate", "--repo", source, "--input", "docs/input.json",
            "--approval-sha256", CanonicalJson.Hash(bytes) })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Synthetic CLI process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Synthetic CLI verification timed out.");
        }
        Assert.Equal(legacy ? 1 : 0, process.ExitCode);
        if (legacy)
            Assert.Contains("REPOSITORY_NOT_ALLOWED", await stderr);
        else
            Assert.Contains("DRY_RUN_VALID", await stdout);
    }

    [Fact]
    public void SourceAndDestinationIdentityCanDifferWithoutHardcodedAccount()
    {
        var candidate = Prepare();
        Assert.NotEqual(candidate.Manifest.Source.Repository, candidate.Manifest.Destination.Repository);
        var receipt = Publisher().Publish(candidate.Digest, true, Now);
        Assert.Equal("published", receipt.Disposition);
        Assert.Equal(sourceCommit, receipt.SourceCommit);
        Assert.Equal(Destination.Repository,
            remoteGit.Text("config", "--local", "--get", "hic.bridge.repository"));
    }

    [Fact]
    public void OfflineCliEmitsApprovalPreviewThenVerifiedReceipt()
    {
        // The CLI uses the real UTC clock. Refresh only this CLI fixture rather than
        // weakening production snapshot freshness checks or changing deterministic tests.
        var cliNow = DateTimeOffset.UtcNow;
        WriteSnapshot(cliNow.AddMinutes(-1), "snapshot-foundation", cliNow);
        var previousOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, PublicationCli.Run([
                "prepare-publication", "--repo", source, "--workspace", workspace,
                "--source-repository", SourceRepository, "--destination", Destination.Repository,
                "--branch", Destination.Branch, "--project", snapshot.ProjectId, "--offline-remote", remote
            ]));
            var state = new PublicationWorkspace(workspace);
            var manifest = state.Read<PublicationManifest>(state.ManifestPath);
            var digest = SnapshotPublisher.Digest(manifest);
            Assert.Contains(digest, output.ToString());
            Assert.Contains("not authorization", output.ToString());
            output.GetStringBuilder().Clear();
            Assert.Equal(0, PublicationCli.Run([
                "publish-publication", "--repo", source, "--workspace", workspace,
                "--source-repository", SourceRepository, "--destination", Destination.Repository,
                "--branch", Destination.Branch, "--offline-remote", remote,
                "--approval-digest", digest, "--approve-publication"
            ]));
            var receipt = CanonicalJson.Parse<PublicationReceipt>(Encoding.UTF8.GetBytes(output.ToString()));
            Assert.Equal("published", receipt.Disposition);
            Assert.Equal(RemoteHead(), receipt.RemoteCommit);
            Assert.Equal(sourceCommit, receipt.SourceCommit);
        }
        finally
        {
            Console.SetOut(previousOutput);
        }
    }

    [Fact]
    public void UnexpectedSourceStagedFileRejectedBeforePreparation()
    {
        StageBytes(sourceGit, "docs/unapproved.txt", Encoding.UTF8.GetBytes("Synthetic unapproved file"));
        Assert.Equal("UNEXPECTED_SOURCE_STAGED_FILES",
            Assert.Throws<BridgeException>(() => Prepare()).Message);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void SourceIndexChangeAfterApprovalRejected()
    {
        var candidate = Prepare();
        StageBytes(sourceGit, "docs/unapproved.txt", Encoding.UTF8.GetBytes("Synthetic unapproved file"));
        Assert.Equal("UNEXPECTED_SOURCE_STAGED_FILES",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void UnexpectedPublisherStagedFileRejected()
    {
        var candidate = Prepare();
        StageBytes(new PublicationGit(workspace), "docs/unapproved.txt", Encoding.UTF8.GetBytes("Synthetic"));
        Assert.Equal("UNEXPECTED_PUBLICATION_STAGED_FILES",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void SecretPatternInCandidateRejectedWithoutEcho()
    {
        var path = Path.Combine(source, SnapshotPath(snapshot));
        var node = JsonNode.Parse(File.ReadAllBytes(path))!;
        node["summary"] = "Password=synthetic-only";
        File.WriteAllText(path, node.ToJsonString());
        var failure = Assert.Throws<BridgeException>(() => Prepare());
        Assert.Equal("UNSAFE_STRING", failure.Message);
        Assert.DoesNotContain("synthetic-only", failure.Message);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void SuccessfulLocalPublicationHasOnlyApprovedPathsAndNoSourceHistory()
    {
        var candidate = Prepare();
        var receipt = Publisher().Publish(candidate.Digest, true, Now);
        Assert.Equal("published", receipt.Disposition);
        Assert.Equal(receipt.PublicationCommit, RemoteHead());
        Assert.Equal(receipt.PublicationCommit, receipt.RemoteCommit);
        Assert.NotEqual(sourceCommit, receipt.PublicationCommit);
        Assert.Equal(baseCommit, remoteGit.Text("rev-parse", receipt.PublicationCommit + "^"));
        Assert.Equal(sourceCommit, sourceGit.Text("rev-parse", "HEAD"));
        Assert.Equal(receipt.SnapshotDigest,
            CanonicalJson.Hash(remoteGit.Blob(receipt.RemoteCommit, SnapshotPath(snapshot))));
        Assert.Equal(candidate.Manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal),
            remoteGit.Text("diff-tree", "--no-commit-id", "--name-only", "-r", receipt.RemoteCommit)
                .Split('\n').Order(StringComparer.Ordinal));
        Assert.Equal("verified", JournalStage());
        Assert.Equal("2", remoteGit.Text("rev-list", "--count", receipt.RemoteCommit));
        var message = remoteGit.Text("log", "-1", "--format=%B", receipt.RemoteCommit);
        Assert.Contains("Publication-Manifest: " + candidate.Digest, message);
        Assert.Contains("Source-Revision: " + sourceCommit, message);
        Assert.Contains("Co-authored-by: Copilot", message);
    }

    [Fact]
    public void SecondPublicationPreservesHistoricalBytesAndUpdatesLatest()
    {
        var firstCandidate = Prepare();
        var firstReceipt = Publisher().Publish(firstCandidate.Digest, true, Now);
        var oldBytes = remoteGit.Blob(firstReceipt.RemoteCommit, SnapshotPath(snapshot));
        var second = WriteSnapshot(Now.AddMinutes(-1), "work-pending");
        var nextWorkspace = Path.Combine(root, "publication-second");
        var publisher = Publisher(location: nextWorkspace);
        var secondCandidate = publisher.Prepare(second.ProjectId, Now);
        var secondReceipt = publisher.Publish(secondCandidate.Digest, true, Now);
        Assert.Equal(oldBytes, remoteGit.Blob(secondReceipt.RemoteCommit, SnapshotPath(snapshot)));
        var secondBytes = remoteGit.Blob(secondReceipt.RemoteCommit, SnapshotPath(second));
        var pointerBytes = remoteGit.Blob(secondReceipt.RemoteCommit, PointerPath(second));
        var checkedSnapshot = ArtifactStore.ValidateBundle(second.ProjectId, pointerBytes, secondBytes, Now);
        Assert.Equal(second.SnapshotId, checkedSnapshot.SnapshotId);
        Assert.Null(checkedSnapshot.PublishedAtUtc);
        Assert.Equal("unknown", checkedSnapshot.Usage.Measurement);
    }

    [Fact]
    public void ImmutableRemoteSnapshotConflictRejected()
    {
        AdvanceRemote(git => StageBytes(git, SnapshotPath(snapshot), Encoding.UTF8.GetBytes("{}")));
        Assert.Equal("IMMUTABLE_REMOTE_SNAPSHOT_CONFLICT",
            Assert.Throws<BridgeException>(() => Prepare()).Message);
    }

    [Fact]
    public void RemoteAdvancementAfterApprovalDoesNotPushOrCommit()
    {
        var candidate = Prepare();
        AdvanceRemote();
        var advanced = RemoteHead();
        Assert.Equal("REMOTE_ADVANCED_RECONCILIATION_REQUIRED",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(advanced, RemoteHead());
        Assert.Equal("prepared", JournalStage());
    }

    [Fact]
    public void NormalPushRejectsRaceAfterLastRemoteCheck()
    {
        var racing = new InterceptTransport(transport, beforePush: () => AdvanceRemote());
        var publisher = Publisher(racing);
        var candidate = publisher.Prepare(snapshot.ProjectId, Now);
        Assert.Equal("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY",
            Assert.Throws<BridgeException>(() => publisher.Publish(candidate.Digest, true, Now)).Message);
        Assert.NotEqual(baseCommit, RemoteHead());
        var journal = new PublicationWorkspace(workspace).Read<PublicationJournal>(Path.Combine(workspace, "journal.json"));
        Assert.NotEqual(journal.PublicationCommit, RemoteHead());
        Assert.Equal("outcome-unknown", journal.Stage);
        Assert.Equal(1, racing.PushCalls);
        Assert.Throws<BridgeException>(() => publisher.Publish(candidate.Digest, true, Now));
        Assert.Equal(1, racing.PushCalls);
    }

    [Fact]
    public void RepeatedPublicationIsReadOnlyIdempotentAfterVerifiedReceipt()
    {
        var intercepted = new InterceptTransport(transport);
        var publisher = Publisher(intercepted);
        var candidate = publisher.Prepare(snapshot.ProjectId, Now);
        var first = publisher.Publish(candidate.Digest, true, Now);
        var second = publisher.Publish(candidate.Digest, true, Now.AddMinutes(1));
        Assert.Equal("already-published", second.Disposition);
        Assert.Equal(first.PublicationCommit, second.PublicationCommit);
        Assert.Equal(1, intercepted.PushCalls);
        Assert.Equal("2", remoteGit.Text("rev-list", "--count", RemoteHead()));
    }

    [Fact]
    public void NewCandidateForAlreadyPublishedBytesDoesNotCreateAnotherCommit()
    {
        var first = Prepare();
        var receipt = Publisher().Publish(first.Digest, true, Now);
        var nextWorkspace = Path.Combine(root, "publication-already");
        var publisher = Publisher(location: nextWorkspace);
        var candidate = publisher.Prepare(snapshot.ProjectId, Now.AddMinutes(1));
        var second = publisher.Publish(candidate.Digest, true, Now.AddMinutes(1));
        Assert.Equal("already-published", second.Disposition);
        Assert.Equal(receipt.PublicationCommit, RemoteHead());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownPushOutcomeNeverRetriesWhetherRemoteAcceptedOrNot(bool accepted)
    {
        var uncertain = new InterceptTransport(transport, loseResponse: true, acceptBeforeLoss: accepted);
        var publisher = Publisher(uncertain);
        var candidate = publisher.Prepare(snapshot.ProjectId, Now);
        Assert.Equal("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY",
            Assert.Throws<BridgeException>(() => publisher.Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal("outcome-unknown", JournalStage());
        if (accepted)
            Assert.NotEqual(baseCommit, RemoteHead());
        else
            Assert.Equal(baseCommit, RemoteHead());
        Assert.Equal("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY",
            Assert.Throws<BridgeException>(() => publisher.Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(1, uncertain.PushCalls);
    }

    [Fact]
    public void InvalidRemoteLatestContentRejected()
    {
        AdvanceRemote(git => StageBytes(git, PointerPath(snapshot), Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<BridgeException>(() => Prepare());
        Assert.False(File.Exists(Path.Combine(workspace, "manifest.json")));
    }

    [Fact]
    public void RemoteSymlinkModeRejected()
    {
        AdvanceRemote(git =>
        {
            var blob = Encoding.UTF8.GetString(git.Run(["hash-object", "-w", "--stdin"], Encoding.UTF8.GetBytes("../outside"))).Trim();
            git.Run(["update-index", "--add", "--cacheinfo", "120000," + blob + "," + PointerPath(snapshot)]);
        });
        Assert.Equal("INVALID_REMOTE_FILE_MODE", Assert.Throws<BridgeException>(() => Prepare()).Message);
    }

    [Fact]
    public void OfflineTransportUsesOnlyLocalBareRemoteAndNoCredentials()
    {
        var candidate = Prepare();
        var receipt = Publisher().Publish(candidate.Digest, true, Now);
        Assert.Equal("published", receipt.Disposition);
        Assert.Null(transport.CredentialHelper);
        Assert.Equal(CanonicalJson.Hash(Encoding.UTF8.GetBytes("local:" + Path.GetFullPath(remote))), transport.Fingerprint);
        Assert.Throws<BridgeException>(() => PublicationTransport.GitHub(Destination, "manager", false));
        Assert.Throws<BridgeException>(() => PublicationTransport.GitHub(Destination, "!arbitrary-command", true));
    }

    [Fact]
    public void NonBareOrWrongIdentityLocalEndpointRejected()
    {
        Assert.Throws<BridgeException>(() => PublicationTransport.Local(Destination, source));
        Assert.Throws<BridgeException>(() => PublicationTransport.Local(Destination with { Repository = "other-owner/repo" }, remote));
    }

    [Fact]
    public void ExpiredApprovalFailsBeforeMutation()
    {
        var candidate = Prepare();
        Assert.Equal("PUBLICATION_APPROVAL_EXPIRED",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now.AddHours(2))).Message);
        Assert.Equal(baseCommit, RemoteHead());
        Assert.Equal("prepared", JournalStage());
    }

    [Fact]
    public void ExistingWorkspaceCannotBeAdoptedOrOverwritten()
    {
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "keep.txt"), "Synthetic preserved file");
        Assert.Equal("FRESH_SEPARATE_PUBLICATION_WORKSPACE_REQUIRED",
            Assert.Throws<BridgeException>(() => Prepare()).Message);
        Assert.Equal("Synthetic preserved file", File.ReadAllText(Path.Combine(workspace, "keep.txt")));
    }

    [Fact]
    public void WorkspaceInsideSourceRejected()
    {
        Assert.Throws<BridgeException>(() => Publisher(location: Path.Combine(source, "publication"))
            .Prepare(snapshot.ProjectId, Now));
    }

    [Fact]
    public void PublisherConfigTamperingRejected()
    {
        var candidate = Prepare();
        new PublicationGit(workspace).Run(["config", "--local", "url.file:///untrusted.insteadOf", "https://github.com/"]);
        Assert.Equal("UNEXPECTED_PUBLICATION_GIT_CONFIG",
            Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Fact]
    public void JournalInProgressCannotBeAutomaticallyRetried()
    {
        var candidate = Prepare();
        var state = new PublicationWorkspace(workspace);
        foreach (var stage in new[] { "committing", "commit-created", "push-started", "outcome-unknown" })
        {
            state.Save(state.JournalPath, new PublicationJournal(stage, candidate.Digest, null, null, null));
            Assert.Equal("PUBLICATION_OUTCOME_UNKNOWN_NO_RETRY",
                Assert.Throws<BridgeException>(() => Publisher().Publish(candidate.Digest, true, Now)).Message);
        }
        Assert.Equal(baseCommit, RemoteHead());
    }

    [Theory]
    [InlineData("owner/repo --upload-pack=bad")]
    [InlineData("owner/repo.git")]
    [InlineData("owner/repo/extra")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("../repo")]
    public void DestinationIdentifierCannotInjectRemoteOrGitArguments(string repository)
    {
        Assert.Throws<BridgeException>(() => PublicationTransport.GitHub(
            Destination with { Repository = repository }, "manager", true));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private sealed class InterceptTransport(
        IPublicationTransport inner,
        Action? beforePush = null,
        bool loseResponse = false,
        bool acceptBeforeLoss = false) : IPublicationTransport
    {
        public int PushCalls { get; private set; }
        public string Fingerprint => inner.Fingerprint;
        public void ValidateTarget(PublicationTarget target) => inner.ValidateTarget(target);
        public string Head(PublicationGit git, PublicationTarget target) => inner.Head(git, target);
        public void Fetch(PublicationGit git, PublicationTarget target) => inner.Fetch(git, target);
        public void Push(PublicationGit git, PublicationTarget target, string commit)
        {
            PushCalls++;
            beforePush?.Invoke();
            if (!loseResponse || acceptBeforeLoss)
                inner.Push(git, target, commit);
            if (loseResponse)
                throw new BridgeException("SYNTHETIC_PUSH_RESPONSE_LOST");
        }
    }
}
