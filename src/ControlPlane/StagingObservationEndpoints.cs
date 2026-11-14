using System.Text.Json;
using System.Text.RegularExpressions;

// Opt-in, read-only status endpoint. Container mount/configuration is intentionally NOT enabled here.
internal static class StagingObservationEndpoints
{
    internal static void Map(WebApplication app, LocalOperatorSessions? sessions)
    {
        app.MapGet("/local-runner/staging-observations", (HttpContext context) =>
        {
            if (sessions is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (sessions.Authenticate(context.Request.Headers.Authorization) is null)
                return Results.Unauthorized();

            var directory = Environment.GetEnvironmentVariable("HIC_STAGING_OBSERVATIONS_DIRECTORY");
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            try
            {
                var entries = ReadObservations(directory);
                return Results.Ok(new
                {
                    version = "hic.staging-ui-status/1",
                    trust = "untrusted-local-observation",
                    executionAllowed = false,
                    observations = entries
                });
            }
            catch (IOException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (JsonException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
    }

    internal static IReadOnlyList<object> ReadObservations(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException("Missing staging status directory.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Unsafe directory.");
        var paths = Directory.EnumerateFileSystemEntries(directory).Take(1001).ToArray();
        if (paths.Length > 1000) throw new InvalidDataException("Too many observations.");
        var entries = new List<(Guid Id, DateTimeOffset At)>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, "^[0-9a-f-]{36}\\.json$", RegexOptions.CultureInvariant) ||
                !Guid.TryParseExact(name[..^5], "D", out var id) ||
                id.ToString("D") != name[..^5])
                throw new InvalidDataException("Unexpected status file.");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 or > 1024)
                throw new InvalidDataException("Unsafe status file.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                !root.TryGetProperty("orderId", out var orderId) || orderId.GetString() != name[..^5] ||
                !root.TryGetProperty("status", out var status) || status.GetString() != "observed-untrusted" ||
                !root.TryGetProperty("executionAllowed", out var allowed) || allowed.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("observedAt", out var at) || at.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(at.GetString(), out var timestamp))
                throw new InvalidDataException("Invalid status record.");
            entries.Add((id, timestamp));
        }
        return entries.OrderByDescending(x => x.At).ThenBy(x => x.Id).Take(20)
            .Select(x => (object)new { orderId = x.Id, status = "observed-untrusted",
                observedAt = x.At, executionAllowed = false }).ToArray();
    }
}
