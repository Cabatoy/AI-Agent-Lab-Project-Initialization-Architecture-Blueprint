using System.Text.Json;

internal static class GitHubIssuePreview
{
    internal static void Map(WebApplication app, LocalOperatorSessions? sessions,
        Func<GitHubDestination>? destination = null, Func<CancellationToken, Task<GitHubApi>>? open = null)
    {
        var broker = new GitHubAppBroker(app.Configuration);
        destination ??= broker.Destination;
        open ??= broker.OpenAsync;
        app.MapPost("/local-runner/github/issues/preview", async (HttpContext context, CancellationToken ct) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (context.Request.ContentLength is null or < 1 or > 2048 ||
                context.Request.Headers.TransferEncoding.Count != 0 ||
                !string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "issue-request-invalid" });
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body,
                    new JsonDocumentOptions { MaxDepth = 3 }, ct);
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("reference", out var value) || value.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "issue-request-invalid" });
                var configured = destination();
                var reference = GitHubIssueIntake.ParseReference(value.GetString(), configured);
                using var api = await open(ct);
                GitHubAppBroker.Check(api.Destination == configured, "issue-repository-not-authorized");
                var repository = await api.GetAsync(api.RepoPath, ct);
                GitHubAppBroker.VerifyRepository(repository, configured);
                GitHubAppBroker.Check(repository.GetProperty("private").ValueKind == JsonValueKind.False,
                    "issue-private-read-not-configured");
                var issue = await api.GetAsync(api.RepoPath + "/issues/" + reference.Number, ct);
                var source = GitHubIssueIntake.CaptureResponse(configured, reference, repository, issue);
                if (sessions?.Authenticate(context.Request.Headers.Authorization) != actor)
                    return Results.Unauthorized();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(source);
            }
            catch (Exception exception) when (exception is GitHubPublishException or JsonException or
                InvalidOperationException or KeyNotFoundException)
            {
                var code = exception is GitHubPublishException known ? known.Code : "issue-response-invalid";
                app.Logger.LogWarning("GitHub issue preview rejected: {Code}; details withheld.", code);
                return Results.Json(new { error = code }, statusCode: 409);
            }
        });
    }
}
