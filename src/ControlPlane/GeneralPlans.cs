using System.Text.Json;
using Dapper;
using Npgsql;

internal sealed record GeneralPlanTask(string Title, string Description);
internal sealed record GeneralPlan(int Revision, string Source, DateTime CreatedAtUtc,
    IReadOnlyList<GeneralPlanTask> Tasks, bool Approved, bool Rejected, IReadOnlyList<Guid> TaskIds);

internal static class GeneralPlanEndpoints
{
    private sealed record StoredPlan(int Revision, string Source, DateTime CreatedAtUtc, string TasksJson);

    internal static void Map(WebApplication app, string connectionString, LocalOperatorSessions? sessions)
    {
        var model = new LocalCodeModel(app.Configuration);
        async Task<IResult> WithConversation(HttpContext context, Guid id, CancellationToken ct,
            Func<NpgsqlConnection, Guid, GeneralConversation, CancellationToken, Task<IResult>> action, bool lockConversation)
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync(ct);
            if (!await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists(select 1 from app_schema_versions where version=9);", cancellationToken: ct)))
                return Results.StatusCode(503);
            var conversation = await GeneralConversationEndpoints.Read(db, id, actor.Value, ct);
            if (conversation is null) return Results.NotFound();
            if (!lockConversation) return await action(db, actor.Value, conversation, ct);
            var acquired = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select pg_try_advisory_lock(hashtextextended(@key, 7007));",
                new { key = id.ToString("D") }, cancellationToken: ct));
            if (!acquired) return Results.Conflict(new { error = "Another conversation operation is in progress." });
            try
            {
                conversation = await GeneralConversationEndpoints.Read(db, id, actor.Value, ct);
                return conversation is null ? Results.NotFound() : await action(db, actor.Value, conversation, ct);
            }
            finally
            {
                await db.ExecuteAsync(new CommandDefinition("select pg_advisory_unlock(hashtextextended(@key, 7007));",
                    new { key = id.ToString("D") }, cancellationToken: CancellationToken.None));
            }
        }

        app.MapGet("/local-runner/general-conversations/{id:guid}/plan", (Guid id, HttpContext context, CancellationToken ct) =>
            WithConversation(context, id, ct, async (db, actor, conversation, token) =>
            {
                var plan = await Read(db, conversation.ConversationId, token);
                return plan is null ? Results.NotFound() : Results.Ok(plan);
            }, false));

        app.MapPost("/local-runner/general-conversations/{id:guid}/plan/generate", (Guid id, HttpContext context, CancellationToken ct) =>
            WithConversation(context, id, ct, async (db, actor, conversation, token) =>
            {
                using var body = await ReadBody(context.Request, token);
                if (!TryRevision(body, 1, out var expected)) return Results.BadRequest(new { error = "Expected revision required." });
                var current = await Read(db, id, token);
                if ((current?.Revision ?? 0) != expected || current?.Approved == true)
                    return Results.Conflict(new { error = "Plan changed or Draft tasks were already created." });
                if (!conversation.Messages.Any(m => m.Role == "user"))
                    return Results.Conflict(new { error = "A conversation message is required before planning." });
                var response = await model.GenerateGeneralPlanAsync(conversation.Messages, token);
                if (response.ErrorCode is not null)
                    return Results.Json(new { code = "model-unavailable", error = "Local model did not return a usable plan; no tasks were created." }, statusCode: 503);
                if (!TryParseTasks(response.Content, out var tasks))
                    return Results.UnprocessableEntity(new { error = "The model did not return a valid bounded task plan." });
                return await Store(db, id, actor, expected + 1, "model", tasks!, token);
            }, true));

        app.MapPost("/local-runner/general-conversations/{id:guid}/plan/revise", (Guid id, HttpContext context, CancellationToken ct) =>
            WithConversation(context, id, ct, async (db, actor, conversation, token) =>
            {
                using var body = await ReadBody(context.Request, token);
                if (!TryRevision(body, 2, out var expected) || body is null ||
                    !body.RootElement.TryGetProperty("tasks", out var value) || !TryTasks(value, out var tasks))
                    return Results.BadRequest(new { error = "A bounded revision and task list are required." });
                var current = await Read(db, id, token);
                if (current is null || current.Revision != expected || current.Approved)
                    return Results.Conflict(new { error = "Plan changed or Draft tasks were already created." });
                return await Store(db, id, actor, expected + 1, "human", tasks!, token);
            }, true));

        app.MapPost("/local-runner/general-conversations/{id:guid}/plan/approve", (Guid id, HttpContext context, CancellationToken ct) =>
            WithConversation(context, id, ct, async (db, actor, conversation, token) =>
            {
                using var body = await ReadBody(context.Request, token);
                if (!TryRevision(body, 1, out var expected)) return Results.BadRequest(new { error = "Expected revision required." });
                var current = await Read(db, id, token);
                if (current is null || current.Revision != expected || current.Rejected)
                    return Results.Conflict(new { error = "Plan revision changed; review the current revision." });
                if (current.Approved) return Results.Ok(current);
                await using var tx = await db.BeginTransactionAsync(token);
                await db.ExecuteAsync(new CommandDefinition(@"
                    insert into hic_general_plan_approvals(conversation_id,revision,actor_id,approved_at_utc)
                    values(@id,@expected,@actor,now());", new { id, expected, actor }, tx, cancellationToken: token));
                for (var index = 0; index < current.Tasks.Count; index++)
                {
                    var taskId = Guid.NewGuid();
                    var task = current.Tasks[index];
                    await db.ExecuteAsync(new CommandDefinition(@"
                        insert into tasks(id,title,description,status,attempt_count,result,error_message,created_at_utc,updated_at_utc)
                        values(@taskId,@title,@description,'Draft',0,null,null,now(),now());
                        insert into hic_general_plan_tasks(conversation_id,ordinal,task_id) values(@id,@index,@taskId);",
                        new { id, index, taskId, task.Title, task.Description }, tx, cancellationToken: token));
                }
                await tx.CommitAsync(token);
                return Results.Ok(await Read(db, id, token));
            }, true));

        app.MapPost("/local-runner/general-conversations/{id:guid}/plan/reject", (Guid id, HttpContext context, CancellationToken ct) =>
            WithConversation(context, id, ct, async (db, actor, conversation, token) =>
            {
                using var body = await ReadBody(context.Request, token);
                if (!TryRevision(body, 1, out var expected)) return Results.BadRequest(new { error = "Expected revision required." });
                var current = await Read(db, id, token);
                if (current is null || current.Revision != expected || current.Approved)
                    return Results.Conflict(new { error = "Plan changed or Draft tasks were already created." });
                await db.ExecuteAsync(new CommandDefinition(@"
                    insert into hic_general_plan_rejections(conversation_id,revision,actor_id,rejected_at_utc)
                    values(@id,@expected,@actor,now()) on conflict (conversation_id,revision) do nothing;",
                    new { id, expected, actor }, cancellationToken: token));
                return Results.Ok(await Read(db, id, token));
            }, true));
    }

    private static async Task<IResult> Store(NpgsqlConnection db, Guid id, Guid actor, int revision,
        string source, IReadOnlyList<GeneralPlanTask> tasks, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(@"
            insert into hic_general_plan_revisions(conversation_id,revision,actor_id,tasks_json,source,created_at_utc)
            values(@id,@revision,@actor,@tasks::jsonb,@source,now());",
            new { id, revision, actor, tasks = JsonSerializer.Serialize(tasks, GitEditExecutor.JsonOptions), source },
            cancellationToken: ct));
        return Results.Ok(await Read(db, id, ct));
    }

    private static async Task<GeneralPlan?> Read(NpgsqlConnection db, Guid id, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<StoredPlan>(new CommandDefinition(@"
            select revision, source, created_at_utc as CreatedAtUtc, tasks_json::text as TasksJson
            from hic_general_plan_revisions where conversation_id=@id order by revision desc limit 1;",
            new { id }, cancellationToken: ct));
        if (row is null) return null;
        var approved = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from hic_general_plan_approvals where conversation_id=@id and revision=@revision);",
            new { id, row.Revision }, cancellationToken: ct));
        var rejected = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from hic_general_plan_rejections where conversation_id=@id and revision=@revision);",
            new { id, row.Revision }, cancellationToken: ct));
        var taskIds = (await db.QueryAsync<Guid>(new CommandDefinition(
            "select task_id from hic_general_plan_tasks where conversation_id=@id order by ordinal;",
            new { id }, cancellationToken: ct))).ToArray();
        return new(row.Revision, row.Source, row.CreatedAtUtc,
            JsonSerializer.Deserialize<GeneralPlanTask[]>(row.TasksJson, GitEditExecutor.JsonOptions)
                ?? throw new InvalidOperationException("Stored plan is invalid."), approved, rejected, taskIds);
    }

    internal static bool TryParseTasks(string json, out IReadOnlyList<GeneralPlanTask>? tasks)
    {
        tasks = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 &&
                root.TryGetProperty("tasks", out var array) && TryTasks(array, out tasks);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryTasks(JsonElement array, out IReadOnlyList<GeneralPlanTask>? tasks)
    {
        tasks = null;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 8) return false;
        var items = new List<GeneralPlanTask>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
                return false;
            var t = title.GetString();
            var d = description.GetString();
            if (string.IsNullOrWhiteSpace(t) || t.Length > 120 || t.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(d) || d.Length > 1000 ||
                d.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
                return false;
            items.Add(new(t.Trim(), d.Trim()));
        }
        tasks = items;
        return true;
    }

    private static bool TryRevision(JsonDocument? body, int propertyCount, out int revision)
    {
        revision = 0;
        return body is not null && body.RootElement.ValueKind == JsonValueKind.Object &&
            body.RootElement.EnumerateObject().Count() == propertyCount &&
            body.RootElement.TryGetProperty("expectedRevision", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out revision) &&
            revision >= 0 && revision < int.MaxValue;
    }

    private static async Task<JsonDocument?> ReadBody(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or < 1 or > 12 * 1024 || request.Headers.TransferEncoding.Count > 0 ||
            !string.Equals(request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 5 }, ct); }
        catch (JsonException) { return null; }
    }
}
