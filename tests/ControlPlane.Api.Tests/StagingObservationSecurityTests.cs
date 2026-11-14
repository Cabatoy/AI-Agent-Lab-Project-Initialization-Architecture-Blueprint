using System.Text.Json;
namespace ControlPlane.Api.Tests;

public sealed class StagingObservationSecurityTests
{
    private const string Id = "f86ff6cd-61cc-4252-bfe6-4831e2c9ddd7";

    [Fact]
    public void ValidObservation_IsReadOnlyAndNonExecutable()
    {
        WithDirectory(dir =>
        {
            Write(dir, Id, new { orderId = Id, status = "observed-untrusted",
                observedAt = "2026-11-15T00:00:00Z", executionAllowed = false });
            var entries = StagingObservationEndpoints.ReadObservations(dir);
            Assert.Single(entries);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(entries[0]));
            Assert.Equal("observed-untrusted", json.RootElement.GetProperty("status").GetString());
            Assert.False(json.RootElement.GetProperty("executionAllowed").GetBoolean());
        });
    }

    [Fact]
    public void ForgedExecutionPermission_IsRejected()
    {
        WithDirectory(dir =>
        {
            Write(dir, Id, new { orderId = Id, status = "observed-untrusted",
                observedAt = "2026-11-15T00:00:00Z", executionAllowed = true });
            Assert.Throws<InvalidDataException>(() => StagingObservationEndpoints.ReadObservations(dir));
        });
    }

    [Fact]
    public void UnexpectedFields_AreRejected()
    {
        WithDirectory(dir =>
        {
            Write(dir, Id, new { orderId = Id, status = "observed-untrusted",
                observedAt = "2026-11-15T00:00:00Z", executionAllowed = false, approved = true });
            Assert.Throws<InvalidDataException>(() => StagingObservationEndpoints.ReadObservations(dir));
        });
    }

    [Fact]
    public void UnexpectedFilename_IsRejected()
    {
        WithDirectory(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "arbitrary.json"), "{}");
            Assert.Throws<InvalidDataException>(() => StagingObservationEndpoints.ReadObservations(dir));
        });
    }

    private static void Write(string dir, string id, object value) =>
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(value));

    private static void WithDirectory(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hic-staging-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { action(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
