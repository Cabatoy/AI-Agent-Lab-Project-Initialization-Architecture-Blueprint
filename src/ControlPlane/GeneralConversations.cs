using System.Text.Json;
using Dapper;
using Npgsql;

internal sealed record GeneralConversationSummary(Guid ConversationId, string Title, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
internal sealed record GeneralConversation(Guid ConversationId, string Title, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, IReadOnlyList<ModelConversationMessage> Messages);
internal sealed record GeneralCreateRequest(string? Title);
internal sealed record GeneralMessageRequest(Guid ClientMessageId, string? Content);

internal static class GeneralConversationEndpoints
{
    private sealed record StoredMessage(Guid MessageId, string Role, string Content, string? Provider,
        string? ModelName, string Status, DateTime CreatedAtUtc);
    private sealed record ExistingTurn(Guid MessageId, string Content, bool HasReply);

    internal static void Map(WebApplication app, string connectionString, LocalOperatorSessions? sessions)
    {
        var model = new LocalCodeModel(app.Configuration);
        Guid? Actor(HttpContext context) =>
            sessions?.Authenticate(context.Request.Headers.Authorization);
        async Task<bool> Ready(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists(select 1 from app_schema_versions where version=7);",
                cancellationToken: cancellationToken));
        }

        app.MapGet("/local-runner/general-conversations", async (HttpContext context, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await Ready(ct)) return Results.StatusCode(503);
            await using var db = new NpgsqlConnection(connectionString);
            var rows = await db.QueryAsync<GeneralConversationSummary>(new CommandDefinition(@"
                select conversation_id as ConversationId, title, created_at_utc as CreatedAtUtc,
                    updated_at_utc as UpdatedAtUtc from hic_general_conversations
                where actor_id=@actor order by updated_at_utc desc limit 50;",
                new { actor }, cancellationToken: ct));
            return Results.Ok(rows);
        });
        app.MapPost("/local-runner/general-conversations", async (HttpContext context, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await Ready(ct)) return Results.StatusCode(503);
            var request = await ReadRequest<GeneralCreateRequest>(context.Request, ct);
            if (request?.Title is null || string.IsNullOrWhiteSpace(request.Title) ||
                request.Title.Length > 120 || request.Title.Any(char.IsControl))
                return Results.BadRequest(new { error = "A bounded conversation title is required." });
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using var db = new NpgsqlConnection(connectionString);
            await db.ExecuteAsync(new CommandDefinition(@"
                insert into hic_general_conversations(conversation_id,actor_id,title,created_at_utc,updated_at_utc)
                values(@id,@actor,@title,@now,@now);",
                new { id, actor, title = request.Title.Trim(), now }, cancellationToken: ct));
            return Results.Created($"/local-runner/general-conversations/{id}",
                new GeneralConversation(id, request.Title.Trim(), now, now, []));
        });
        app.MapGet("/local-runner/general-conversations/{id:guid}", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await Ready(ct)) return Results.StatusCode(503);
            await using var db = new NpgsqlConnection(connectionString);
            var result = await Read(db, id, actor.Value, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        app.MapPost("/local-runner/general-conversations/{id:guid}/messages", async (
            Guid id, HttpContext context, CancellationToken ct) =>
        {
            var actor = Actor(context);
            if (actor is null) return sessions is null ? Results.StatusCode(503) : Results.Unauthorized();
            if (!await Ready(ct)) return Results.StatusCode(503);
            var request = await ReadRequest<GeneralMessageRequest>(context.Request, ct);
            if (request is null || request.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Content) ||
                request.Content.Length > 4000 || request.Content.Any(c =>
                    c == '\0' || char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
                return Results.BadRequest(new { error = "A bounded message and client turn identifier are required." });
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync(ct);
            var conversation = await Read(db, id, actor.Value, ct);
            if (conversation is null) return Results.NotFound();
            var acquired = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select pg_try_advisory_lock(hashtextextended(@key, 7007));",
                new { key = id.ToString("D") }, cancellationToken: ct));
            if (!acquired) return Results.Conflict(new { error = "Another message is in progress." });
            try
            {
                conversation = await Read(db, id, actor.Value, ct);
                if (conversation is null) return Results.NotFound();
                var existing = await db.QuerySingleOrDefaultAsync<ExistingTurn>(new CommandDefinition(@"
                    select u.message_id as MessageId, u.content,
                        exists(select 1 from hic_general_messages a where a.reply_to_message_id=u.message_id) as HasReply
                    from hic_general_messages u where u.conversation_id=@id and u.client_message_id=@client;",
                    new { id, client = request.ClientMessageId }, cancellationToken: ct));
                if (existing is not null)
                {
                    if (existing.Content != request.Content.Trim() || !existing.HasReply)
                        return Results.Conflict(new { error = "This turn was already accepted or its content changed; review history before sending a new message." });
                    return Results.Ok(await Read(db, id, actor.Value, ct));
                }
                var userId = Guid.NewGuid();
                await db.ExecuteAsync(new CommandDefinition(@"
                    insert into hic_general_messages(message_id,conversation_id,role,content,status,client_message_id,created_at_utc)
                    values(@userId,@id,'user',@content,'completed',@client,now());
                    update hic_general_conversations set updated_at_utc=now() where conversation_id=@id;",
                    new { userId, id, content = request.Content.Trim(), client = request.ClientMessageId }, cancellationToken: ct));
                var reply = await model.GenerateGeneralAsync(conversation.Messages, request.Content.Trim(), ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await db.ExecuteAsync(new CommandDefinition(@"
                    insert into hic_general_messages(message_id,conversation_id,role,content,provider,model_name,status,reply_to_message_id,created_at_utc)
                    values(@messageId,@id,'assistant',@content,@provider,@modelName,@status,@userId,now());
                    update hic_general_conversations set updated_at_utc=now() where conversation_id=@id;",
                    new { messageId = Guid.NewGuid(), id, content = reply.Content,
                        provider = model.Info.Configured ? model.Info.Provider : null,
                        modelName = model.Info.Configured ? model.Info.Model : null,
                        status = reply.ErrorCode ?? "completed", userId }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                return Results.Ok(await Read(db, id, actor.Value, ct));
            }
            finally
            {
                await db.ExecuteAsync(new CommandDefinition("select pg_advisory_unlock(hashtextextended(@key, 7007));",
                    new { key = id.ToString("D") }, cancellationToken: CancellationToken.None));
            }
        });
    }

    private static async Task<GeneralConversation?> Read(NpgsqlConnection db, Guid id, Guid actor, CancellationToken ct)
    {
        var row = await db.QuerySingleOrDefaultAsync<GeneralConversationSummary>(new CommandDefinition(@"
            select conversation_id as ConversationId, title, created_at_utc as CreatedAtUtc,
                updated_at_utc as UpdatedAtUtc from hic_general_conversations
            where conversation_id=@id and actor_id=@actor;", new { id, actor }, cancellationToken: ct));
        if (row is null) return null;
        var messages = await db.QueryAsync<StoredMessage>(new CommandDefinition(@"
            select message_id as MessageId, role, content, provider, model_name as ModelName,
                status, created_at_utc as CreatedAtUtc from hic_general_messages
            where conversation_id=@id order by sequence;", new { id }, cancellationToken: ct));
        return new(row.ConversationId, row.Title, row.CreatedAtUtc, row.UpdatedAtUtc,
            messages.Select(m => new ModelConversationMessage(m.MessageId, m.Role, m.Content,
                m.Provider, m.ModelName, m.Status, null, m.CreatedAtUtc)).ToArray());
    }

    private static async Task<T?> ReadRequest<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (request.ContentLength is null or < 1 or > 12 * 1024 ||
            request.Headers.TransferEncoding.Count > 0 ||
            !string.Equals(request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            return null;
        try { return await JsonSerializer.DeserializeAsync<T>(request.Body, GitEditExecutor.JsonOptions, ct); }
        catch (JsonException) { return null; }
    }
}
