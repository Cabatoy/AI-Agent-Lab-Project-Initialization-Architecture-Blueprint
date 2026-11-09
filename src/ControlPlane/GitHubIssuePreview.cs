using System.Text.Json;

internal static class GitHubIssuePreview
{
    internal static void Map(WebApplication app, LocalOperatorSessions? sessions, string? connectionString = null,
        Func<GitHubDestination>? destination = null, Func<CancellationToken, Task<GitHubApi>>? open = null)
    {
        var broker = new GitHubAppBroker(app.Configuration);
        destination ??= broker.Destination;
        open ??= broker.OpenAsync;
        var store = connectionString is null ? null : new GitHubIssueImportStore(connectionString);
        async Task<GitHubIssueSource> Fetch(string? text, CancellationToken ct)
        {
            var configured = destination();
            var reference = GitHubIssueIntake.ParseReference(text, configured);
            using var api = await open(ct);
            GitHubAppBroker.Check(api.Destination == configured, "issue-repository-not-authorized");
            var repository = await api.GetAsync(api.RepoPath, ct);
            GitHubAppBroker.VerifyRepository(repository, configured);
            GitHubAppBroker.Check(repository.GetProperty("private").ValueKind == JsonValueKind.False,
                "issue-private-read-not-configured");
            var issue = await api.GetAsync(api.RepoPath + "/issues/" + reference.Number, ct);
            return GitHubIssueIntake.CaptureResponse(configured, reference, repository, issue);
        }
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
                var source = await Fetch(value.GetString(), ct);
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
        app.MapPost("/local-runner/github/issues/import-preview", async (HttpContext context, CancellationToken ct) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (store is null || !await store.Ready(ct)) return Results.StatusCode(503);
            if (context.Request.ContentLength is null or < 1 or > 16000 ||
                context.Request.Headers.TransferEncoding.Count != 0 ||
                !string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "issue-request-invalid" });
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, new JsonDocumentOptions { MaxDepth = 3 }, ct);
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 ||
                    !root.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("revisionSha256", out var digest) || digest.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "issue-request-invalid" });
                var source = await Fetch(reference.GetString(), ct);
                if (source.RevisionSha256 != digest.GetString()) throw new GitHubPublishException("issue-revision-changed");
                if (sessions!.Authenticate(context.Request.Headers.Authorization) != actor) return Results.Unauthorized();
                var prepared = await store.Prepare(actor.Value, source, title.GetString()!, description.GetString()!, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(prepared);
            }
            catch (Exception exception) when (exception is GitHubPublishException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                var code = exception is GitHubPublishException known ? known.Code : "issue-response-invalid";
                app.Logger.LogWarning("Issue import preview rejected: {Code}; details withheld.", code);
                return Results.Json(new { error = code }, statusCode: 409);
            }
        });
        app.MapPost("/local-runner/github/issues/import-previews/{id:guid}/confirm", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (store is null || !await store.Ready(ct)) return Results.StatusCode(503);
            if (context.Request.ContentLength is not (null or 0) || context.Request.Headers.TransferEncoding.Count != 0)
                return Results.BadRequest(new { error = "issue-request-invalid" });
            try
            {
                var prepared = await store.Read(id, actor.Value, ct);
                if (prepared is null) return Results.NotFound();
                var source = await Fetch(prepared.Reference, ct);
                var result = await store.Confirm(prepared, source, sessions!, context.Request.Headers.Authorization, ct);
                if (result is null) return Results.Unauthorized();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(result);
            }
            catch (Exception exception) when (exception is GitHubPublishException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                var code = exception is GitHubPublishException known ? known.Code : "issue-response-invalid";
                app.Logger.LogWarning("Issue import rejected: {Code}; details withheld.", code);
                return Results.Json(new { error = code }, statusCode: 409);
            }
        });
    }
}
