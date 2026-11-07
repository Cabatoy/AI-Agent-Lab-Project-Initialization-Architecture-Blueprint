using System.Text.Json;
using Xunit;

public sealed class NativeActionPolicyTests
{
    [Theory]
    [InlineData("{\"action\":\"workspace.inspect\"}", true)]
    [InlineData("{\"action\":\"workspace.inspect\",\"command\":\"whoami\"}", false)]
    [InlineData("{\"action\":\"workspace.inspect\",\"path\":\"C:\\\\\"}", false)]
    [InlineData("{\"action\":\"shell.run\"}", false)]
    [InlineData("{\"action\":\"workspace.inspect\",\"action\":\"workspace.inspect\"}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public void OnlyFixedActionWithoutExtraFields(string json, bool allowed)
    {
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(allowed, NativeActionPolicy.AllowedPayload(payload.RootElement));
    }

    [Fact]
    public void TokenValidationRejectsMissingOrDifferentTokens()
    {
        const string expected = "synthetic-secret-test-only";
        Assert.True(NativeActionPolicy.ValidToken(expected, expected));
        Assert.False(NativeActionPolicy.ValidToken(null, expected));
        Assert.False(NativeActionPolicy.ValidToken("wrong", expected));
    }

    [Fact]
    public void InspectionReportsOnlyConfiguredWorkspacePresence()
    {
        var root = Path.Combine(Path.GetTempPath(), "hic-native-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, NativeActionPolicy.Solution), "");
            var result = NativeActionPolicy.Inspect(root);
            Assert.Equal("workspace.inspect", result.Action);
            Assert.NotEqual(Guid.Empty, result.RunId);
            Assert.True(result.WorkspaceAvailable);
            Assert.True(result.GitCheckoutFound);
            Assert.True(result.SolutionFound);
            Assert.False(JsonSerializer.Serialize(result).Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InspectsOnlyAbsolutePaths()
    {
        Assert.Throws<InvalidOperationException>(() => NativeActionPolicy.Inspect("relative"));
    }
}
