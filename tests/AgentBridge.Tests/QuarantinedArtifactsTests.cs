using Hic.AgentBridge;

namespace AgentBridge.Tests;

public sealed class QuarantinedArtifactsTests
{
    private static readonly byte[] Source = "original\n"u8.ToArray();
    private static readonly byte[] Output = "candidate\n"u8.ToArray();
    private static ArtifactInputFields Input() => new(
        ArtifactManifestVerifier.InputVersion, Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "example/project", 123,
        new string('a', 40), new string('b', 64), "edit",
        new(new string('c', 64), "none", 1000, 1048576, 16, 60, 10, 1024, 4096,
            [new("message.txt", [CanonicalJson.Hash(Source)])]),
        [new("message.txt", CanonicalJson.Hash(Source), Source.Length, "regular", "100644")]);
    private static byte[] Json(ArtifactInputFields input) =>
        CanonicalJson.Serialize(new ArtifactInputManifest(input, ArtifactManifestVerifier.Digest(input)));
    private static IReadOnlyDictionary<string, byte[]> Sources() => new Dictionary<string, byte[]> { ["message.txt"] = Source };
    private static TrustedCandidateFile File(byte[]? bytes = null, string path = "message.txt") =>
        new(path, "regular", "100644", (bytes ?? Output).Length, CanonicalJson.Hash(bytes ?? Output), bytes ?? Output);
    private static QuarantinedCandidate Import(ArtifactInputFields input, params TrustedCandidateFile[] files) =>
        QuarantinedArtifacts.Import(Json(input), input, Sources(), files);
    private static void Code(string expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<BridgeException>(action).Message);

    [Fact]
    public void CandidateCopiesBytesAndDoesNotBroadenApprovedManifestHashes()
    {
        var input = Input();
        var bytes = (byte[])Output.Clone();
        var candidate = Import(input, File(bytes));
        bytes[0] ^= 1;
        var copy = candidate.Files[0].CopyBytes();
        Assert.Equal(Output, copy);
        copy[0] ^= 1;
        Assert.Equal(Output, candidate.Files[0].CopyBytes());
        var output = new ArtifactOutputFields(ArtifactManifestVerifier.OutputVersion, input.TaskId, input.RunId,
            input.ApprovalId, input.Repository, input.InstallationId, input.BaseCommitSha,
            ArtifactManifestVerifier.Digest(input),
            [new("message.txt", CanonicalJson.Hash(Output), Output.Length, "regular", "100644")]);
        var json = CanonicalJson.Serialize(new ArtifactOutputManifest(output, ArtifactManifestVerifier.Digest(output)));
        Code("HASH_NOT_ALLOWED", () => ArtifactManifestVerifier.VerifyOutput(json, input,
            ArtifactManifestVerifier.Digest(input), new Dictionary<string, byte[]> { ["message.txt"] = Output }));
    }

    [Fact]
    public void ExactIndependentAcceptanceIsBoundToCandidateAndExecution()
    {
        var candidate = Import(Input(), File());
        var authorization = new ArtifactAcceptance(Guid.NewGuid(), candidate.Binding, candidate.Sha256);
        var accepted = QuarantinedArtifacts.Accept(candidate, authorization);
        Assert.Equal(authorization.DecisionId, accepted.DecisionId);
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(candidate,
            authorization with { DecisionId = Guid.Empty }));
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(candidate,
            authorization with { DecisionId = candidate.Binding.DecisionId }));
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(candidate,
            authorization with { CandidateSha256 = new string('f', 64) }));
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(candidate,
            authorization with { Binding = candidate.Binding with { RunId = Guid.NewGuid() } }));
        var changed = Import(Input(), File("changed\n"u8.ToArray()));
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(changed, authorization));
    }

    [Theory]
    [InlineData("../out")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x")]
    [InlineData(".git/config")]
    [InlineData("NUL.txt")]
    [InlineData("a\\b")]
    public void TraversalAndAliasesFail(string path) =>
        Code("INVALID_PATH", () => Import(Input(), File(path: path)));

    [Theory]
    [InlineData("symlink", "100644")]
    [InlineData("reparse", "100644")]
    [InlineData("hardlink", "100644")]
    [InlineData("device", "100644")]
    [InlineData("regular", "100755")]
    public void UnsafeTypesAndModesFail(string type, string mode) =>
        Code("INVALID_FILE_TYPE", () => Import(Input(), File() with { Type = type, Mode = mode }));

    [Fact]
    public void AlteredBytesAndOversizedContentFail()
    {
        var file = File();
        Code("CONTENT_MISMATCH", () => Import(Input(), file with { Sha256 = new string('f', 64) }));
        Code("FILE_SIZE", () => Import(Input(), file with { ByteLength = file.ByteLength + 1 }));
        Code("FILE_SIZE", () => Import(Input(), File(new byte[1025])));
        Code("FILE_COUNT", () => Import(Input()));
        Code("PATH_NOT_ALLOWED", () => Import(Input(), File(path: "other.txt")));
    }

    [Fact]
    public void DuplicateAndCaseCollidingFilesFail()
    {
        var input = Input();
        input = input with { Policy = input.Policy with { OutputRules =
            [new("message.txt", [CanonicalJson.Hash(Source)]), new("other.txt", [CanonicalJson.Hash(Source)])] } };
        Code("PATH_COLLISION", () => Import(input, File(), File()));
        Code("PATH_COLLISION", () => Import(input, File(), File(path: "MESSAGE.TXT")));
    }

    [Fact]
    public void ReplayIsContentAddressedAndDifferentRunsCannotReuseAcceptance()
    {
        var input = Input();
        var first = Import(input, File());
        var again = Import(input, File());
        Assert.Equal(first.Sha256, again.Sha256);
        var different = Import(input with { RunId = Guid.NewGuid() }, File());
        Assert.NotEqual(first.Sha256, different.Sha256);
        Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(different,
            new ArtifactAcceptance(Guid.NewGuid(), first.Binding, first.Sha256)));
    }

    [Fact]
    public void InputSubstitutionAndChangedAuthorizationAreRejected()
    {
        var input = Input();
        var json = Json(input);
        Code("INPUT_BINDING_MISMATCH", () => QuarantinedArtifacts.Import(json,
            input with { BaseCommitSha = new string('d', 40) }, Sources(), [File()]));
        Code("CONTENT_MISMATCH", () => QuarantinedArtifacts.Import(json, input,
            new Dictionary<string, byte[]> { ["message.txt"] = "tampered\n"u8.ToArray() }, [File()]));
        var candidate = Import(input, File());
        var acceptance = new ArtifactAcceptance(Guid.NewGuid(), candidate.Binding, candidate.Sha256);
        foreach (var binding in new[] {
            candidate.Binding with { TaskId = Guid.NewGuid() },
            candidate.Binding with { DecisionId = Guid.NewGuid() },
            candidate.Binding with { Repository = "example/other" },
            candidate.Binding with { InstallationId = 124 },
            candidate.Binding with { BaseCommitSha = new string('d', 40) },
            candidate.Binding with { InputManifestSha256 = new string('e', 64) } })
            Code("ACCEPTANCE_BINDING_MISMATCH", () => QuarantinedArtifacts.Accept(candidate,
                acceptance with { Binding = binding }));
    }

    [Fact]
    public void NoHostileFilesystemBackendExists() =>
        Code("UNSAFE_FILESYSTEM_BACKEND", QuarantinedArtifacts.ImportFromUntrustedFilesystem);

    [Fact]
    public void DisposableTrustedFixtureDetectsAlterationBeforeReimport()
    {
        var candidate = Import(Input(), File());
        var root = Path.Combine(Path.GetTempPath(), "hic-quarantine-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, candidate.Files[0].Sha256);
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(candidate.Files[0].CopyBytes());
            var read = System.IO.File.ReadAllBytes(path);
            Assert.Equal(candidate.Files[0].Sha256, CanonicalJson.Hash(read));
            System.IO.File.WriteAllBytes(path, "altered\n"u8.ToArray());
            var altered = System.IO.File.ReadAllBytes(path);
            Code("CONTENT_MISMATCH", () => Import(Input(), new TrustedCandidateFile(
                "message.txt", "regular", "100644", altered.Length, candidate.Files[0].Sha256, altered)));
            Assert.Equal(Output, candidate.Files[0].CopyBytes());
        }
        finally
        {
            System.IO.File.Delete(path);
            Directory.Delete(root);
        }
    }
}
