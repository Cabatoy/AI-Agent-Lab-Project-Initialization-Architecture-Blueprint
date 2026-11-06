using System.Text;
using System.Text.Json.Nodes;
using Hic.AgentBridge;

namespace AgentBridge.Tests;

public sealed class SnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-01-02T10:00:00Z");
    private static readonly DateTimeOffset Now = Observed.AddMinutes(10);
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Reference = "docs/result.md";
    private readonly string root = Path.Combine(Path.GetTempPath(), "hic-bridge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGit git = new();

    public SnapshotTests() => Directory.CreateDirectory(root);

    private static SnapshotInput Input() => new()
    {
        ContractVersion = "hic.bridge/1.0", ProjectId = "synthetic-project",
        TaskId = null, ExecutionId = null, CorrelationId = null,
        SourceCommit = Commit, ObservedAtUtc = Observed, ExecutionStatus = "Unknown",
        Summary = "snapshot-foundation",
        ChangedFiles = [new ChangedFile("tools/example.cs", "added")],
        VerificationEvidence = [new Evidence("runtime", "NOT_RUN", "NOT_RUN", null, Commit, Observed)],
        KnownRisks = ["runtime-not-run"],
        RecommendedNextAction = "review-before-publication",
        Usage = new Usage("unknown", null, null, null, null, null),
        ArtifactReferences = [new ArtifactInput(Reference)]
    };

    private Snapshot Generate(SnapshotInput? input = null)
    {
        var bytes = CanonicalJson.Serialize(input ?? Input());
        return new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes), git, Now);
    }

    private void RejectJson(Action<JsonObject> mutate, string expected)
    {
        var node = JsonNode.Parse(CanonicalJson.Serialize(Input()))!.AsObject();
        mutate(node);
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        var exception = Assert.Throws<BridgeException>(() =>
            new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes), git, Now));
        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void ValidSnapshotHasContractIdentitiesFreshnessAndNoPublication()
    {
        var snapshot = Generate();
        var roundTrip = CanonicalJson.Parse<Snapshot>(CanonicalJson.Serialize(snapshot));
        Assert.Equal("hic.bridge/1.0", roundTrip.ContractVersion);
        Assert.Equal(64, roundTrip.SnapshotId.Length);
        Assert.Null(roundTrip.TaskId);
        Assert.Null(roundTrip.ExecutionId);
        Assert.Null(roundTrip.CorrelationId);
        Assert.Null(roundTrip.PublishedAtUtc);
        Assert.Equal(Commit, roundTrip.Source.CommitSha);
        Assert.Equal(Observed, roundTrip.ObservedAtUtc);
        Assert.Equal("Unknown", roundTrip.ExecutionStatusConfidence);
        SnapshotProducer.ValidateStored(roundTrip, Now);
    }

    [Theory]
    [InlineData("contractVersion")]
    [InlineData("projectId")]
    [InlineData("taskId")]
    [InlineData("executionId")]
    [InlineData("correlationId")]
    [InlineData("sourceCommit")]
    [InlineData("observedAtUtc")]
    [InlineData("executionStatus")]
    [InlineData("summary")]
    [InlineData("changedFiles")]
    [InlineData("verificationEvidence")]
    [InlineData("knownRisks")]
    [InlineData("recommendedNextAction")]
    [InlineData("usage")]
    [InlineData("artifactReferences")]
    public void EveryInputFieldIsRequired(string field) =>
        RejectJson(node => node.Remove(field), "INVALID_JSON");

    [Theory]
    [InlineData("hic.bridge/2.0")]
    [InlineData("hic.bridge/1.1")]
    [InlineData("")]
    public void UnsupportedVersionFailsClosed(string version) =>
        RejectJson(node => node["contractVersion"] = version, "UNSUPPORTED_VERSION");

    [Fact]
    public void MissingVerificationEvidenceIsNotSuccess() =>
        RejectJson(node => node["verificationEvidence"] = new JsonArray(), "COLLECTION_LIMIT");

    [Fact]
    public void UnknownUsageRemainsNullNotZero()
    {
        var usage = Generate().Usage;
        Assert.Equal("unknown", usage.Measurement);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.Cost);
        Assert.Null(usage.Currency);
        Assert.Null(usage.PricingVersion);
    }

    [Theory]
    [InlineData("inputTokens", "0")]
    [InlineData("outputTokens", "1")]
    [InlineData("cost", "0")]
    [InlineData("measurement", "\"actual\"")]
    [InlineData("currency", "\"USD\"")]
    [InlineData("pricingVersion", "\"v1\"")]
    public void UnsupportedUsageCannotBeInvented(string field, string json) =>
        RejectJson(node => node["usage"]![field] = JsonNode.Parse(json), "USAGE_NOT_MEASURED");

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidCommitShaRejected(string sha) =>
        RejectJson(node => node["sourceCommit"] = sha, "INVALID_SHA");

    [Fact]
    public void WrongSourceRevisionRejected() =>
        RejectJson(node =>
        {
            node["sourceCommit"] = new string('b', 40);
            node["verificationEvidence"]![0]!["sourceCommit"] = new string('b', 40);
        }, "SOURCE_COMMIT_MISMATCH");

    [Theory]
    [InlineData("../private.txt")]
    [InlineData("docs/../private.txt")]
    [InlineData("C:/private.txt")]
    [InlineData("docs\\private.txt")]
    [InlineData("/docs/private.txt")]
    [InlineData("docs/%2e%2e/private.txt")]
    [InlineData(".git/config.json")]
    [InlineData("docs/.env")]
    [InlineData("docs/CON.json")]
    [InlineData("docs/result.json:stream")]
    [InlineData("docs//result.md")]
    [InlineData("logs/output.txt")]
    [InlineData("docs/file.exe")]
    [InlineData("docs/file.json?value")]
    public void UnsafeReferencesCannotEscapeAllowlist(string path) =>
        Assert.Throws<BridgeException>(() => Generate(Input() with { ArtifactReferences = [new ArtifactInput(path)] }));

    [Theory]
    [InlineData("Password=synthetic")]
    [InlineData("Host=localhost;Username=synthetic")]
    [InlineData("Bearer synthetic-value")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("ghp_synthetic_not_a_real_credential")]
    [InlineData("raw_prompt")]
    [InlineData("conversation transcript")]
    [InlineData("Stack trace")]
    [InlineData("https://untrusted.invalid/artifact")]
    public void SensitiveStringsAreRejectedWithoutEcho(string value)
    {
        var exception = Assert.Throws<BridgeException>(() => Generate(Input() with { Summary = value }));
        Assert.Equal("UNSAFE_STRING", exception.Message);
        Assert.DoesNotContain(value, exception.Message);
    }

    [Fact]
    public void ArbitraryTextCannotBypassRedaction() =>
        RejectJson(node => node["summary"] = "Some unapproved company data", "UNAPPROVED_TEXT_CODE");

    [Theory]
    [InlineData("rawPrompt")]
    [InlineData("environment")]
    [InlineData("conversation")]
    [InlineData("arbitraryMetadata")]
    public void UnknownTopLevelFieldsRejected(string field) =>
        RejectJson(node => node[field] = "synthetic", "INVALID_JSON");

    [Fact]
    public void UnknownNestedFieldRejected() =>
        RejectJson(node => node["usage"]!["extra"] = "synthetic", "INVALID_JSON");

    [Fact]
    public void DuplicateJsonFieldsRejected()
    {
        var json = Encoding.UTF8.GetString(CanonicalJson.Serialize(Input()));
        var bytes = Encoding.UTF8.GetBytes(json.Replace("\"projectId\":", "\"projectId\":\"other\",\"projectId\":"));
        Assert.Equal("DUPLICATE_FIELD", Assert.Throws<BridgeException>(() =>
            new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes), git, Now)).Message);
    }

    [Fact]
    public void OversizedInputRejectedBeforeGit()
    {
        var bytes = new byte[CanonicalJson.MaxBytes + 1];
        Assert.Equal("PAYLOAD_SIZE", Assert.Throws<BridgeException>(() =>
            new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes), git, Now)).Message);
        Assert.Equal(0, git.SourceReads);
    }

    [Fact]
    public void OutputByteLimitIsEnforced()
    {
        Assert.Throws<BridgeException>(() => CanonicalJson.Serialize(Generate(), limit: 100));
        Assert.InRange(CanonicalJson.Serialize(Generate()).Length, 1, CanonicalJson.MaxBytes);
    }

    [Fact]
    public void ActualExpandedSnapshotOver16KiBIsRejected()
    {
        static string LongPath(int index) => "docs/" + new string('a', 220) + index + ".md";
        var input = Input() with
        {
            ChangedFiles = Enumerable.Range(0, 20).Select(i => new ChangedFile(LongPath(i), "modified")).ToArray(),
            ArtifactReferences = Enumerable.Range(0, 8).Select(i => new ArtifactInput(LongPath(i))).ToArray(),
            VerificationEvidence = Enumerable.Range(0, 12).Select(i =>
                new Evidence("tests", "Inferred", "PASS", LongPath(i), Commit, Observed)).ToArray()
        };
        var bytes = CanonicalJson.Serialize(input);
        Assert.InRange(bytes.Length, 1, CanonicalJson.MaxBytes);
        Assert.Equal("PAYLOAD_SIZE", Assert.Throws<BridgeException>(() =>
            new SnapshotProducer().Generate(bytes, CanonicalJson.Hash(bytes), git, Now)).Message);
    }

    [Fact]
    public void StringLengthBounded() =>
        RejectJson(node => node["summary"] = new string('a', 513), "UNSAFE_STRING");

    [Theory]
    [InlineData("verificationEvidence", 13)]
    [InlineData("artifactReferences", 9)]
    [InlineData("changedFiles", 21)]
    public void CollectionCountsBounded(string field, int count) =>
        RejectJson(node =>
        {
            var sample = node[field]![0]!.DeepClone();
            var array = new JsonArray();
            for (var index = 0; index < count; index++)
                array.Add(sample.DeepClone());
            node[field] = array;
        }, "COLLECTION_LIMIT");

    [Theory]
    [InlineData("changedFiles")]
    [InlineData("artifactReferences")]
    [InlineData("verificationEvidence")]
    [InlineData("knownRisks")]
    public void NullCollectionItemsRejected(string field) =>
        RejectJson(node => node[field]!.AsArray().Add((JsonNode?)null), "NULL_COLLECTION_ITEM");

    [Fact]
    public void CallerCannotMarkTestPassAsVerified() =>
        RejectJson(node =>
        {
            var item = node["verificationEvidence"]![0]!;
            item["kind"] = "tests";
            item["status"] = "Verified";
            item["result"] = "PASS";
            item["sourceRef"] = Reference;
        }, "UNVERIFIED_EVIDENCE_CLAIM");

    [Fact]
    public void CallerTestPassIsInferredNotIndependentVerification()
    {
        var snapshot = Generate(Input() with
        {
            ExecutionStatus = "Completed",
            VerificationEvidence = [new Evidence("tests", "Inferred", "PASS", Reference, Commit, Observed)]
        });
        Assert.Equal("Inferred", snapshot.ExecutionStatusConfidence);
        Assert.Contains(snapshot.VerificationEvidence, e => e.Kind == "tests" && e.Status == "Inferred");
        Assert.DoesNotContain(snapshot.VerificationEvidence, e => e.Kind == "tests" && e.Status == "Verified");
    }

    [Theory]
    [InlineData("Inferred", "FAIL")]
    [InlineData("NOT_RUN", "NOT_RUN")]
    [InlineData("NOT_MEASURED", "NOT_MEASURED")]
    [InlineData("Unknown", "Unknown")]
    public void EvidenceDistinctionsArePreserved(string status, string result)
    {
        var snapshot = Generate(Input() with
        {
            VerificationEvidence = [new Evidence("tests", status, result, Reference, Commit, Observed)]
        });
        Assert.Contains(snapshot.VerificationEvidence, e => e.Status == status && e.Result == result);
    }

    [Fact]
    public void ApprovalDigestBindsExactInput()
    {
        var bytes = CanonicalJson.Serialize(Input());
        Assert.Equal("APPROVAL_DIGEST_MISMATCH", Assert.Throws<BridgeException>(() =>
            new SnapshotProducer().Generate(bytes, new string('0', 64), git, Now)).Message);
        Assert.Equal(0, git.SourceReads);
    }

    [Fact]
    public void StableCanonicalOutputAndContentAddressedIdentity()
    {
        var first = Generate();
        var second = Generate();
        Assert.Equal(CanonicalJson.Serialize(first), CanonicalJson.Serialize(second));
        var text = Encoding.UTF8.GetString(CanonicalJson.Serialize(first));
        Assert.StartsWith("{\"artifactReferences\":", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("\n", text);
        Assert.NotEqual(first.SnapshotId, Generate(Input() with { Summary = "work-pending" }).SnapshotId);
    }

    [Fact]
    public void TrailingRepositorySeparatorDoesNotChangeArtifactLocation()
    {
        var store = new ArtifactStore(root + Path.DirectorySeparatorChar);
        var snapshot = Generate();
        store.Write(snapshot, Now);
        Assert.Equal(snapshot.SnapshotId,
            store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromDays(1), git).SnapshotId);
    }

    [Fact]
    public void ArrayOrderDoesNotChangeCanonicalSnapshot()
    {
        var input = Input() with
        {
            ChangedFiles = [new ChangedFile("tools/b.cs", "modified"), new ChangedFile("tools/a.cs", "added")],
            KnownRisks = ["runtime-not-run", "usage-not-measured"],
            ArtifactReferences = [new ArtifactInput("docs/b.md"), new ArtifactInput(Reference)],
            VerificationEvidence =
            [
                new Evidence("runtime", "NOT_RUN", "NOT_RUN", null, Commit, Observed),
                new Evidence("tests", "Unknown", "Unknown", null, Commit, Observed)
            ]
        };
        var shuffled = input with
        {
            ChangedFiles = input.ChangedFiles.Reverse().ToArray(),
            KnownRisks = input.KnownRisks.Reverse().ToArray(),
            ArtifactReferences = input.ArtifactReferences.Reverse().ToArray(),
            VerificationEvidence = input.VerificationEvidence.Reverse().ToArray()
        };
        Assert.Equal(CanonicalJson.Serialize(Generate(input)), CanonicalJson.Serialize(Generate(shuffled)));
    }

    [Fact]
    public void GenerationPerformsNoFilesystemWriteAndOnlyExplicitGitReads()
    {
        _ = Generate();
        Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.Equal(1, git.SourceReads);
        Assert.Equal([Reference], git.BlobReads);
        Assert.DoesNotContain(typeof(SnapshotProducer).Assembly.GetReferencedAssemblies(),
            a => a.Name is "System.Net.Http" or "System.Net.Sockets");
    }

    [Fact]
    public void DuplicateSnapshotNeverOverwritesBytesOrPointer()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        var relative = store.Write(snapshot, Now);
        var before = File.ReadAllBytes(Path.Combine(root, relative));
        var latestPath = Path.Combine(root, "handoffs", snapshot.ProjectId, "latest.json");
        var pointerBefore = File.ReadAllBytes(latestPath);
        Assert.Equal("DUPLICATE_SNAPSHOT", Assert.Throws<BridgeException>(() => store.Write(snapshot, Now)).Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, relative)));
        Assert.Equal(pointerBefore, File.ReadAllBytes(latestPath));
        Assert.False(File.Exists(Path.Combine(root, "handoffs", snapshot.ProjectId, ".write.lock")));
    }

    [Fact]
    public void NewSnapshotPreservesHistoryAndUpdatesLatestConsistently()
    {
        var store = new ArtifactStore(root);
        var first = Generate();
        var firstPath = store.Write(first, Now);
        var original = File.ReadAllBytes(Path.Combine(root, firstPath));
        var second = Generate(Input() with { ObservedAtUtc = Observed.AddMinutes(1), Summary = "work-pending" });
        store.Write(second, Now);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, firstPath)));
        Assert.Equal(second.SnapshotId, store.ReadLatest(second.ProjectId, Commit, Now, TimeSpan.FromDays(1), git).SnapshotId);
        var pointerBytes = File.ReadAllBytes(Path.Combine(root, "handoffs", second.ProjectId, "latest.json"));
        var pointer = CanonicalJson.Parse<LatestPointer>(pointerBytes);
        Assert.Equal(CanonicalJson.Hash(CanonicalJson.Serialize(second)), pointer.Sha256);
        Assert.Equal($"handoffs/{second.ProjectId}/snapshots/{second.SnapshotId}.json", pointer.SnapshotPath);
        Assert.Null(pointer.PublishedAtUtc);
    }

    [Fact]
    public void OlderObservationCannotReplaceLatest()
    {
        var store = new ArtifactStore(root);
        var latest = Generate(Input() with { ObservedAtUtc = Observed.AddMinutes(1) });
        store.Write(latest, Now);
        Assert.Equal("LATEST_WOULD_REGRESS", Assert.Throws<BridgeException>(() => store.Write(Generate(), Now)).Message);
    }

    [Fact]
    public void PointerTraversalAndWrongDigestRejected()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        store.Write(snapshot, Now);
        var latest = Path.Combine(root, "handoffs", snapshot.ProjectId, "latest.json");
        var pointer = CanonicalJson.Parse<LatestPointer>(File.ReadAllBytes(latest));
        File.WriteAllBytes(latest, CanonicalJson.Serialize(pointer with { SnapshotPath = "../untrusted.json" }));
        Assert.Equal("INVALID_LATEST_POINTER", Assert.Throws<BridgeException>(() =>
            store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromDays(1), git)).Message);
        File.WriteAllBytes(latest, CanonicalJson.Serialize(pointer with { Sha256 = new string('0', 64) }));
        Assert.Equal("LATEST_INTEGRITY_MISMATCH", Assert.Throws<BridgeException>(() =>
            store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromDays(1), git)).Message);
    }

    [Fact]
    public void SnapshotTamperingRejected()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        var path = Path.Combine(root, store.Write(snapshot, Now));
        File.WriteAllBytes(path, CanonicalJson.Serialize(snapshot with { ExecutionStatus = "Completed" }));
        Assert.Throws<BridgeException>(() => store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromDays(1), git));
    }

    [Fact]
    public void StaleSnapshotAndWrongExpectedRevisionRejected()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        store.Write(snapshot, Now);
        Assert.Equal("STALE_SNAPSHOT", Assert.Throws<BridgeException>(() =>
            store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromMinutes(1), git)).Message);
        Assert.Equal("SOURCE_COMMIT_MISMATCH", Assert.Throws<BridgeException>(() =>
            store.ReadLatest(snapshot.ProjectId, new string('b', 40), Now, TimeSpan.FromDays(1), git)).Message);
    }

    [Fact]
    public void OrphanHistoryWithoutPointerRequiresInspection()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        store.Write(snapshot, Now);
        File.Delete(Path.Combine(root, "handoffs", snapshot.ProjectId, "latest.json"));
        Assert.Equal("ORPHAN_STATE_REQUIRES_INSPECTION", Assert.Throws<BridgeException>(() =>
            store.Write(Generate(Input() with { Summary = "work-pending" }), Now)).Message);
    }

    [Fact]
    public void ChangedArtifactBytesCannotPassReaderIntegrityCheck()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        store.Write(snapshot, Now);
        git.Blob = Encoding.UTF8.GetBytes("different synthetic file");
        Assert.Equal("ARTIFACT_INTEGRITY_MISMATCH", Assert.Throws<BridgeException>(() =>
            store.ReadLatest(snapshot.ProjectId, Commit, Now, TimeSpan.FromDays(1), git)).Message);
    }

    [Fact]
    public void LockCollisionDoesNotReplaceAnySnapshot()
    {
        var store = new ArtifactStore(root);
        var snapshot = Generate();
        var project = Path.Combine(root, "handoffs", snapshot.ProjectId);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".write.lock"), "synthetic interrupted writer");
        Assert.Throws<IOException>(() => store.Write(snapshot, Now));
        Assert.False(Directory.Exists(Path.Combine(project, "snapshots")));
    }

    [Fact]
    public void ForgedVerifiedEvidenceRejectedEvenAfterRehash()
    {
        var snapshot = Generate();
        snapshot = snapshot with
        {
            VerificationEvidence = [..snapshot.VerificationEvidence,
                new Evidence("tests", "Verified", "PASS", Reference, Commit, Observed)]
        };
        snapshot = snapshot with { SnapshotId = SnapshotProducer.Identity(snapshot) };
        Assert.Equal("INVALID_VERIFIED_EVIDENCE", Assert.Throws<BridgeException>(() =>
            new ArtifactStore(root).Write(snapshot, Now)).Message);
    }

    [Fact]
    public void PublicationTimestampCannotBeFabricatedLocally()
    {
        var snapshot = Generate() with { PublishedAtUtc = Observed };
        snapshot = snapshot with { SnapshotId = SnapshotProducer.Identity(snapshot) };
        Assert.Throws<BridgeException>(() => new ArtifactStore(root).Write(snapshot, Now));
    }

    [Theory]
    [InlineData("Proposed")]
    [InlineData("Planned")]
    [InlineData("Completed")]
    public void ExistingTaskStatusNamesAreProjectionsNotTransitions(string status)
    {
        var snapshot = Generate(Input() with { ExecutionStatus = status });
        Assert.Equal(status, snapshot.ExecutionStatus);
        Assert.Equal("Inferred", snapshot.ExecutionStatusConfidence);
    }

    [Fact]
    public void ExecutionWithoutTaskAndEmptyIdentityRejected()
    {
        Assert.Throws<BridgeException>(() => Generate(Input() with { ExecutionId = Guid.NewGuid() }));
        Assert.Throws<BridgeException>(() => Generate(Input() with { TaskId = Guid.Empty }));
        var task = Guid.NewGuid();
        var execution = Guid.NewGuid();
        var snapshot = Generate(Input() with { TaskId = task, ExecutionId = execution });
        Assert.Equal(task, snapshot.TaskId);
        Assert.Equal(execution, snapshot.ExecutionId);
    }

    [Fact]
    public void FutureAndNonUtcObservationsRejected()
    {
        Assert.Throws<BridgeException>(() => Generate(Input() with { ObservedAtUtc = Now.AddMinutes(1) }));
        Assert.Throws<BridgeException>(() => Generate(Input() with { ObservedAtUtc = Observed.ToOffset(TimeSpan.FromHours(3)) }));
    }

    [Fact]
    public void IndependentGitEvidenceUsesCaptureTimeNotCallerEventTime()
    {
        var snapshot = Generate();
        Assert.Equal(Observed, snapshot.ObservedAtUtc);
        Assert.All(snapshot.VerificationEvidence.Where(e => e.Status == "Verified"),
            e => Assert.Equal(Now, e.ObservedAtUtc));
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private sealed class FakeGit : ILocalGit
    {
        public int SourceReads { get; private set; }
        public List<string> BlobReads { get; } = [];
        public byte[] Blob { get; set; } = Encoding.UTF8.GetBytes("Synthetic approved artifact");
        public Source ReadSource()
        {
            SourceReads++;
            return new Source(SnapshotProducer.Repository, "feature/synthetic-bridge", Commit, false);
        }
        public byte[] ReadApprovedBlob(string commit, string path)
        {
            Assert.Equal(Commit, commit);
            BlobReads.Add(path);
            return Blob;
        }
    }
}
