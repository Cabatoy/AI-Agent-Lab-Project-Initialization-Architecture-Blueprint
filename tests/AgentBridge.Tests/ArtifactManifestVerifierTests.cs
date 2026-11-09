using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Hic.AgentBridge;

namespace AgentBridge.Tests;

public sealed class ArtifactManifestVerifierTests
{
    private static readonly byte[] Content = "safe\n"u8.ToArray();
    private static readonly string Hash = CanonicalJson.Hash(Content);
    private static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ApprovalId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static ArtifactFile File(string path = "message.txt") => new(path, Hash, Content.Length, "regular", "100644");
    private static ArtifactInputFields Input() => new(
        ArtifactManifestVerifier.InputVersion, TaskId, RunId, ApprovalId, "sample/project", 123,
        new string('a', 40), new string('b', 64), "edit",
        new(new string('c', 64), "none", 1000, 1024 * 1024, 16, 60, 10, 1024, 4096,
            [new("message.txt", [Hash])]), [File()]);
    private static ArtifactOutputFields Output(ArtifactInputFields input) => new(
        ArtifactManifestVerifier.OutputVersion, input.TaskId, input.RunId, input.ApprovalId,
        input.Repository, input.InstallationId, input.BaseCommitSha,
        ArtifactManifestVerifier.Digest(input), [File()]);
    private static byte[] InputJson(ArtifactInputFields fields) =>
        CanonicalJson.Serialize(new ArtifactInputManifest(fields, ArtifactManifestVerifier.Digest(fields)));
    private static byte[] OutputJson(ArtifactOutputFields fields) =>
        CanonicalJson.Serialize(new ArtifactOutputManifest(fields, ArtifactManifestVerifier.Digest(fields)));
    private static IReadOnlyDictionary<string, byte[]> Bytes(string path = "message.txt") =>
        new Dictionary<string, byte[]> { [path] = Content };
    private static void Code(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<BridgeException>(action).Message);

    [Fact]
    public void ValidInputAndOutputVerifyAndDigestExcludesEnvelope()
    {
        var input = Input();
        var first = ArtifactManifestVerifier.VerifyInput(InputJson(input), input, Bytes());
        var output = ArtifactManifestVerifier.VerifyOutput(OutputJson(Output(input)), input, first.ManifestSha256, Bytes());
        Assert.Equal(ArtifactManifestVerifier.Digest(input), first.ManifestSha256);
        Assert.Equal(1, output.FileCount);
        Assert.Equal(Content.Length, output.TotalBytes);
        var changedEnvelope = new ArtifactInputManifest(input, new string('f', 64));
        Code("MANIFEST_HASH_MISMATCH", () =>
            ArtifactManifestVerifier.VerifyInput(CanonicalJson.Serialize(changedEnvelope), input, Bytes()));
    }

    [Fact]
    public void CanonicalDigestIgnoresJsonObjectOrder()
    {
        var input = Input();
        var node = JsonNode.Parse(InputJson(input))!.AsObject();
        var reversed = new JsonObject();
        foreach (var item in node.Reverse()) reversed.Add(item.Key, item.Value?.DeepClone());
        var result = ArtifactManifestVerifier.VerifyInput(Encoding.UTF8.GetBytes(reversed.ToJsonString()), input, Bytes());
        Assert.Equal(ArtifactManifestVerifier.Digest(input), result.ManifestSha256);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/tmp/x")]
    [InlineData("C:/tmp/x")]
    [InlineData("\\\\server\\share")]
    [InlineData("a\\b")]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData("a/../x")]
    [InlineData("a//b")]
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("con.txt")]
    [InlineData("AUX")]
    [InlineData("com1.txt")]
    [InlineData("lpt9")]
    [InlineData("café.txt")]
    [InlineData("foo:bar")]
    [InlineData("x\u0000y")]
    public void UnsafePathsFailOnEveryHost(string path)
    {
        var input = Input();
        var output = Output(input) with { Files = [File(path)] };
        var expected = path.Contains('\0') ? "UNSAFE_STRING" : "INVALID_PATH";
        Code(expected, () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input,
            ArtifactManifestVerifier.Digest(input), Bytes(path)));
    }

    [Theory]
    [InlineData("symlink", "100644")]
    [InlineData("hardlink", "100644")]
    [InlineData("device", "100644")]
    [InlineData("regular", "100755")]
    [InlineData("regular", "120000")]
    public void RejectsLinksSpecialFilesAndExecutableModes(string type, string mode)
    {
        var input = Input();
        var output = Output(input) with { Files = [File() with { Type = type, Mode = mode }] };
        Code("INVALID_FILE_TYPE", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input,
            ArtifactManifestVerifier.Digest(input), Bytes()));
    }

    [Fact]
    public void RejectsDuplicateCaseCollisionsAndFileDirectoryConflicts()
    {
        var input = Input();
        foreach (var other in new[] { "message.txt", "MESSAGE.TXT", "message.txt/child" })
        {
            var files = ImmutableArray.Create(File(), File(other));
            var rules = ImmutableArray.Create(new ArtifactPathRule("message.txt", [Hash]),
                new ArtifactPathRule(other, [Hash]));
            var candidate = input with { Files = files, Policy = input.Policy with { OutputRules = rules } };
            Code("PATH_COLLISION", () => ArtifactManifestVerifier.VerifyInput(InputJson(candidate), candidate,
                new Dictionary<string, byte[]> { ["message.txt"] = Content }));
        }
    }

    [Fact]
    public void RejectsUnapprovedPathsHashesMissingContentAndSizes()
    {
        var input = Input();
        var digest = ArtifactManifestVerifier.Digest(input);
        var output = Output(input);
        Code("PATH_NOT_ALLOWED", () => ArtifactManifestVerifier.VerifyOutput(
            OutputJson(output with { Files = [File("other.txt")] }), input, digest, Bytes("other.txt")));
        var otherHash = CanonicalJson.Hash("other\n"u8.ToArray());
        Code("HASH_NOT_ALLOWED", () => ArtifactManifestVerifier.VerifyOutput(
            OutputJson(output with { Files = [File() with { Sha256 = otherHash }] }), input, digest, Bytes()));
        Code("CONTENT_MISMATCH", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input, digest,
            new Dictionary<string, byte[]> { ["message.txt"] = "other"u8.ToArray() }));
        Code("FILE_COUNT", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input, digest,
            new Dictionary<string, byte[]>()));
        Code("FILE_SIZE", () => ArtifactManifestVerifier.VerifyOutput(
            OutputJson(output with { Files = [File() with { ByteLength = 1025 }] }), input, digest, Bytes()));
    }

    [Fact]
    public void ReplayAndIdentitySubstitutionAreRejected()
    {
        var input = Input();
        var digest = ArtifactManifestVerifier.Digest(input);
        var output = Output(input);
        foreach (var altered in new[] {
            output with { TaskId = Guid.NewGuid() }, output with { RunId = Guid.NewGuid() },
            output with { ApprovalId = Guid.NewGuid() }, output with { Repository = "sample/other" },
            output with { InstallationId = 124 }, output with { BaseCommitSha = new string('d', 40) },
            output with { InputManifestSha256 = new string('e', 64) }
        })
            Code("OUTPUT_BINDING_MISMATCH", () =>
                ArtifactManifestVerifier.VerifyOutput(OutputJson(altered), input, digest, Bytes()));
        Code("INPUT_BINDING_MISMATCH", () =>
            ArtifactManifestVerifier.VerifyInput(InputJson(input with { RunId = Guid.NewGuid() }), input, Bytes()));
        Code("INPUT_BINDING_MISMATCH", () =>
            ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input, new string('f', 64), Bytes()));
        Code("UNSUPPORTED_VERSION", () =>
            ArtifactManifestVerifier.VerifyOutput(OutputJson(output with { SchemaVersion = "hic.sandbox.output.v0" }),
                input, digest, Bytes()));
    }

    [Fact]
    public void MalformedAndDuplicateJsonFailsClosed()
    {
        var input = Input();
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput("{"u8.ToArray(), input, Bytes()));
        Code("DUPLICATE_FIELD", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes("{\"fields\":{},\"fields\":{},\"manifestSha256\":\"x\"}"), input, Bytes()));
        Code("PAYLOAD_SIZE", () => ArtifactManifestVerifier.VerifyInput(new byte[CanonicalJson.MaxBytes + 1], input, Bytes()));
        var node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["unexpected"] = true;
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
    }

    [Fact]
    public void RejectsInvalidPolicyAndIncompleteOutput()
    {
        var input = Input();
        foreach (var policy in new[] {
            input.Policy with { Network = "full" }, input.Policy with { MaxFiles = 101 },
            input.Policy with { MaxFileBytes = ArtifactManifestVerifier.MaxFileBytes + 1 },
            input.Policy with { MaxTotalBytes = 0 }, input.Policy with { Pids = 0 },
            input.Policy with { OutputRules = [] }
        })
        {
            var altered = input with { Policy = policy };
            Code("INVALID_POLICY", () => ArtifactManifestVerifier.VerifyInput(InputJson(altered), altered, Bytes()));
        }
        var output = Output(input) with { Files = [] };
        Code("FILE_COUNT", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input,
            ArtifactManifestVerifier.Digest(input), new Dictionary<string, byte[]>()));
    }

    [Fact]
    public void InputBindingProtectsEveryTrustedPolicyAndSourceField()
    {
        var input = Input();
        foreach (var changed in new[] {
            input with { TaskId = Guid.NewGuid() }, input with { ApprovalId = Guid.NewGuid() },
            input with { InstallationId = 124 }, input with { Repository = "sample/other" },
            input with { BaseCommitSha = new string('d', 40) },
            input with { SourceTreeSha256 = new string('e', 64) },
            input with { Policy = input.Policy with { TimeoutSeconds = 61 } },
            input with { Policy = input.Policy with { ImageSha256 = new string('f', 64) } }
        })
            Code("INPUT_BINDING_MISMATCH", () =>
                ArtifactManifestVerifier.VerifyInput(InputJson(changed), input, Bytes()));
    }

    [Fact]
    public void TotalQuotaDepthAndContentKeyCasingAreEnforced()
    {
        var input = Input();
        var files = ImmutableArray.Create(File(), File("second.txt"));
        var rules = ImmutableArray.Create(new ArtifactPathRule("message.txt", [Hash]),
            new ArtifactPathRule("second.txt", [Hash]));
        var bounded = input with { Files = files, Policy = input.Policy with { MaxTotalBytes = 9, OutputRules = rules } };
        var bytes = new Dictionary<string, byte[]> { ["message.txt"] = Content, ["second.txt"] = Content };
        Code("FILE_SIZE", () => ArtifactManifestVerifier.VerifyInput(InputJson(bounded), bounded, bytes));
        var deep = string.Join('/', Enumerable.Repeat("x", 17));
        var output = Output(input) with { Files = [File(deep)] };
        Code("INVALID_PATH", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(output), input,
            ArtifactManifestVerifier.Digest(input), Bytes(deep)));
        Code("CONTENT_MISMATCH", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(Output(input)), input,
            ArtifactManifestVerifier.Digest(input),
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["MESSAGE.TXT"] = Content }));
    }

    [Fact]
    public void JsonNullMissingAndCaseAliasedFieldsAreRejected()
    {
        var input = Input();
        var node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["fields"]!["policy"] = null;
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
        node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["fields"]!.AsObject().Remove("approvalId");
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
        node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["Fields"] = node["fields"]!.DeepClone();
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
    }

    [Fact]
    public void NullAndInvalidTrustedFieldsFailWithExplicitCodes()
    {
        var input = Input();
        var json = InputJson(input);
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(null!, input, Bytes()));
        Code("INVALID_BINDING", () => ArtifactManifestVerifier.VerifyInput(json, null!, Bytes()));
        Code("INVALID_POLICY", () => ArtifactManifestVerifier.VerifyInput(json, input with { Policy = null! }, Bytes()));
        Code("INVALID_REPOSITORY", () => ArtifactManifestVerifier.VerifyInput(json, input with { Repository = null! }, Bytes()));
        Code("INVALID_SHA", () => ArtifactManifestVerifier.VerifyInput(json, input with { BaseCommitSha = null! }, Bytes()));
        Code("INVALID_POLICY", () => ArtifactManifestVerifier.VerifyInput(json,
            input with { Policy = input.Policy with { OutputRules = [null!] } }, Bytes()));
        Code("INVALID_SHA", () => ArtifactManifestVerifier.VerifyInput(json,
            input with { Policy = input.Policy with { OutputRules = [new("message.txt", [null!])] } }, Bytes()));
        Code("FILE_COUNT", () => ArtifactManifestVerifier.VerifyInput(json, input with { Files = default }, Bytes()));
        Code("INVALID_FILE_TYPE", () => ArtifactManifestVerifier.VerifyInput(json, input with { Files = [null!] }, Bytes()));
        Code("INVALID_SHA", () => ArtifactManifestVerifier.VerifyOutput(OutputJson(Output(input)), input, null!, Bytes()));
        var invalid = input with { Files = [File() with { Mode = "120000" }] };
        Code("INVALID_FILE_TYPE", () => ArtifactManifestVerifier.VerifyOutput(
            OutputJson(Output(invalid)), invalid, ArtifactManifestVerifier.Digest(invalid), Bytes()));
    }

    [Fact]
    public void NullNestedUntrustedValuesFailClosed()
    {
        var input = Input();
        var node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["fields"] = null;
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
        node = JsonNode.Parse(InputJson(input))!.AsObject();
        node["fields"]!["files"]!.AsArray()[0] = null;
        Code("NULL_COLLECTION_ITEM", () => ArtifactManifestVerifier.VerifyInput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, Bytes()));
        node = JsonNode.Parse(OutputJson(Output(input)))!.AsObject();
        node["fields"]!["files"]![0]!["sha256"] = null;
        Code("INVALID_JSON", () => ArtifactManifestVerifier.VerifyOutput(
            Encoding.UTF8.GetBytes(node.ToJsonString()), input, ArtifactManifestVerifier.Digest(input), Bytes()));
    }
}
