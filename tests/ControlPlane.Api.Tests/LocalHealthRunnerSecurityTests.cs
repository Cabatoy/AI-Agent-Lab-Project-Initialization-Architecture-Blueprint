using System.Security.Cryptography;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class LocalHealthRunnerSecurityTests
{
    [Fact]
    public void PersistedHealthResult_UsesTheWebUiJsonContract()
    {
        var result = new LocalDiagnosticSnapshot(
            "system.health", DateTime.SpecifyKind(new DateTime(2026, 11, 8, 12, 0, 0), DateTimeKind.Utc),
            "ok", "ok", "unavailable", "not-checked");

        using var document = JsonDocument.Parse(LocalHealthRunStore.SerializeResult(result));
        var root = document.RootElement;
        Assert.Equal("system.health", root.GetProperty("action").GetString());
        Assert.Equal("ok", root.GetProperty("controlPlane").GetString());
        Assert.Equal("ok", root.GetProperty("postgres").GetString());
        Assert.Equal("unavailable", root.GetProperty("redis").GetString());
        Assert.Equal("not-checked", root.GetProperty("worker").GetString());
        Assert.False(root.TryGetProperty("Action", out _));
    }

    [Fact]
    public void IntentDigest_IsStableAndBindsTaskVersionTextActionAndScope()
    {
        var taskId = Guid.NewGuid();
        var version = DateTime.SpecifyKind(new DateTime(2026, 11, 8, 12, 0, 0), DateTimeKind.Utc);
        var digest = LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version);

        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Changed", "Inspect local services.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Changed.", version));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(taskId, "Health check", "Inspect local services.", version.AddSeconds(1)));
        Assert.NotEqual(digest, LocalHealthRunIntent.Digest(Guid.NewGuid(), "Health check", "Inspect local services.", version));
        Assert.Equal("system.health", LocalHealthRunIntent.Action);
        Assert.Equal("control-plane:health:read", LocalHealthRunIntent.Scope);
    }

    [Fact]
    public void IntentDigestComparison_RequiresCanonicalLowercaseSha256()
    {
        var digest = new string('a', 64);
        Assert.True(LocalHealthRunIntent.Matches(digest, digest));
        Assert.False(LocalHealthRunIntent.Matches(digest.ToUpperInvariant(), digest));
        Assert.False(LocalHealthRunIntent.Matches("not-a-hash", digest));
        Assert.False(LocalHealthRunIntent.Matches(null, digest));
    }

    [Fact]
    public void LocalSession_RequiresConfiguredRandomBootstrapTokenAndActor()
    {
        var bootstrap = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var actorId = Guid.NewGuid();
        var sessions = LocalOperatorSessions.Create(bootstrap, actorId.ToString());
        Assert.NotNull(sessions);

        var token = sessions.IssueSessionToken(bootstrap);
        Assert.NotNull(token);
        Assert.NotEqual(bootstrap, token);
        Assert.Equal(actorId, sessions.Authenticate($"Bearer {token}"));
        Assert.Null(sessions.Authenticate($"Bearer {bootstrap}"));
        Assert.Null(sessions.Authenticate($"Bearer {new string('A', 44)}"));
        Assert.Null(sessions.Authenticate(token));
        Assert.Null(sessions.IssueSessionToken(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    [Theory]
    [InlineData(null, "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("short", "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("not-base64!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!", "018f23a2-8d75-4a00-8000-000000000001")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "not-a-guid")]
    public void InvalidRegistrationConfiguration_DisablesLocalSessions(string? token, string actorId)
    {
        Assert.Null(LocalOperatorSessions.Create(token, actorId));
    }
}
