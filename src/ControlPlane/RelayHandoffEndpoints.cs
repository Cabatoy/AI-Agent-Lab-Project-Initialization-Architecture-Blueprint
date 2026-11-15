using System.Text.Json;

// Operator-authenticated, observe-only API. This route never calls approve/retry,
// Redis, a worker, GitHub write APIs, or any AWS endpoint.
internal static class RelayHandoffEndpoints
{
    internal static void Map(WebApplication app, string postgresConnectionString, LocalOperatorSessions? sessions)
    {
        var store = new RelayHandoffStore(postgresConnectionString);
        app.MapGet("/local-runner/relay/tasks/{taskId:guid}/intent", async (
            Guid taskId, string repository, string expectedBaseRef,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null
                ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken)) return Results.StatusCode(503);
            var digest = await store.GetIntentDigestAsync(taskId, repository, expectedBaseRef, cancellationToken);
            return digest is null ? Results.Conflict(new { error = "Draft task or intent unavailable." }) :
                Results.Ok(new { taskId, version = RelayHandoffIntent.Version,
                    scope = RelayHandoffIntent.Scope, intentDigestSha256 = digest,
                    repository, expectedBaseRef, executionAllowed = false });
        });

        app.MapPost("/local-runner/relay/tasks/{taskId:guid}/handoffs", async (
            Guid taskId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null
                ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken)) return Results.StatusCode(503);
            var request = await ReadRequestAsync(context.Request, cancellationToken);
            if (request is null) return Results.BadRequest(new { error = "Invalid handoff request." });
            var result = await store.RecordAsync(taskId, actor.Value,
                request.IdempotencyKey, request.IntentDigestSha256,
                request.Repository, request.ExpectedBaseRef, cancellationToken);
            return result.Code switch
            {
                "recorded" or "replay" => Results.Ok(new
                {
                    handoffId = result.Record!.HandoffId, taskId,
                    status = result.Record.Status, scope = result.Record.Scope,
                    executionAllowed = false, replay = result.Code == "replay"
                }),
                "not-found" => Results.NotFound(),
                "invalid" => Results.BadRequest(new { error = "Invalid handoff intent." }),
                _ => Results.Conflict(new { error = "Handoff rejected; task changed or key already used." })
            };
        });

        app.MapGet("/local-runner/relay/handoffs/{handoffId:guid}", async (
            Guid handoffId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null
                ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await store.IsSchemaReadyAsync(cancellationToken)) return Results.StatusCode(503);
            var record = await store.GetAsync(handoffId, actor.Value, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(new
            {
                record.HandoffId, record.TaskId, record.Status, record.Scope,
                record.CreatedAtUtc, executionAllowed = false
            });
        });
    }

    private sealed record Request(string IdempotencyKey, string IntentDigestSha256,
        string Repository, string ExpectedBaseRef);

    private static async Task<Request?> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is null or < 1 or > 1024 ||
            request.Headers.TransferEncoding.Count > 0 ||
            !string.Equals(request.ContentType?.Split(';', 2)[0], "application/json",
                StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body,
                new JsonDocumentOptions { MaxDepth = 3 }, cancellationToken);
            var root = doc.RootElement;
            // Reject duplicate and unknown JSON properties rather than allowing
            // last-value-wins parsing to hide an ambiguous handoff intent.
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    property.Name is not ("idempotencyKey" or "intentDigestSha256"
                        or "repository" or "expectedBaseRef"))
                    return null;
            }
            if (names.Count != 4) return null;
            static string? Read(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
            var key = Read(root, "idempotencyKey");
            var digest = Read(root, "intentDigestSha256");
            var repository = Read(root, "repository");
            var expectedBaseRef = Read(root, "expectedBaseRef");
            if (!RelayHandoffIntent.IsValidIdempotencyKey(key) ||
                digest is null || digest.Length != 64 ||
                repository is null || expectedBaseRef is null) return null;
            return new Request(key!, digest, repository, expectedBaseRef);
        }
        catch (JsonException) { return null; }
    }
}
