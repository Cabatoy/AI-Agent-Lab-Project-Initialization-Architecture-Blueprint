using System.Text.Json;
using Dapper;
using Npgsql;

internal sealed record GitHubPublication(
    Guid PublicationId, Guid ActorId, Guid? RunId, Guid? ConversationId, long RepositoryId,
    string Kind, string PlanJson, string Digest, string State, string? BlobSha, string? TreeSha,
    string? CommitSha, string? RemoteSha, int? PrNumber, string? PrUrl, string? ErrorCode,
    DateTime CreatedAtUtc, DateTime? ApprovedAtUtc, DateTime UpdatedAtUtc)
{
    internal GitHubPublicationPlan Plan =>
        JsonSerializer.Deserialize<GitHubPublicationPlan>(PlanJson, GitEditExecutor.JsonOptions)
        ?? throw new GitHubPublishException("publication-plan-invalid");
}

internal sealed class GitHubPublicationStore(string connectionString)
{
    private const string Columns = """
        publication_id as PublicationId, actor_id as ActorId, run_id as RunId,
        conversation_id as ConversationId, repository_id as RepositoryId, kind,
        plan_json::text as PlanJson, digest, state, blob_sha as BlobSha, tree_sha as TreeSha,
        commit_sha as CommitSha, remote_sha as RemoteSha, pr_number as PrNumber, pr_url as PrUrl,
        error_code as ErrorCode, created_at_utc as CreatedAtUtc,
        approved_at_utc as ApprovedAtUtc, updated_at_utc as UpdatedAtUtc
        """;

    internal async Task<bool> ReadyAsync(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from app_schema_versions where version=7);", cancellationToken: ct));
    }

    internal async Task CreateAsync(GitHubPublicationPlan plan, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            insert into hic_github_publications(publication_id,actor_id,run_id,conversation_id,
                repository_id,kind,plan_json,digest,state,created_at_utc,updated_at_utc)
            values(@PublicationId,@ActorId,@RunId,@ConversationId,@RepositoryId,@Kind,
                @json::jsonb,@digest,'preview',@now,@now);
            insert into hic_github_publication_events(publication_id,event_code) values(@PublicationId,'preview');
            """, new { plan.PublicationId, plan.ActorId, plan.RunId, plan.ConversationId,
            RepositoryId = plan.Destination.Id, plan.Kind,
            json = JsonSerializer.Serialize(plan, GitEditExecutor.JsonOptions), digest = plan.Digest(),
            now = DateTime.UtcNow }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal async Task<GitHubPublication?> ReadAsync(Guid id, Guid actor, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        return await db.QuerySingleOrDefaultAsync<GitHubPublication>(new CommandDefinition(
            "select " + Columns + " from hic_github_publications where publication_id=@id and actor_id=@actor;",
            new { id, actor }, cancellationToken: ct));
    }

    internal async Task<IReadOnlyList<object>> ConversationAsync(Guid id, Guid actor, CancellationToken ct)
    {
        if (!await ReadyAsync(ct)) return [];
        await using var db = new NpgsqlConnection(connectionString);
        var rows = await db.QueryAsync<GitHubPublication>(new CommandDefinition(
            "select " + Columns + " from hic_github_publications where conversation_id=@id and actor_id=@actor order by created_at_utc;",
            new { id, actor }, cancellationToken: ct));
        var result = new List<object>();
        foreach (var row in rows) result.Add(await ViewAsync(row, ct));
        return result;
    }

    internal async Task<object> ViewAsync(GitHubPublication row, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        var events = await db.QueryAsync<LocalHealthRunEvent>(new CommandDefinition("""
            select event_code as EventCode, recorded_at_utc as RecordedAtUtc
            from hic_github_publication_events where publication_id=@id order by event_id;
            """, new { id = row.PublicationId }, cancellationToken: ct));
        return new { row.PublicationId, row.RunId, row.State, plan = row.Plan, intentDigestSha256 = row.Digest,
            row.CommitSha, row.RemoteSha, row.PrNumber, row.PrUrl, row.ErrorCode, row.UpdatedAtUtc, events };
    }

    internal async Task<IReadOnlyList<object>> BootstrapsAsync(Guid actor, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        var rows = await db.QueryAsync<GitHubPublication>(new CommandDefinition(
            "select " + Columns + " from hic_github_publications where actor_id=@actor and kind='bootstrap' order by created_at_utc desc limit 20;",
            new { actor }, cancellationToken: ct));
        var result = new List<object>();
        foreach (var row in rows) result.Add(await ViewAsync(row, ct));
        return result;
    }

    internal async Task<bool> ApproveAsync(Guid id, Guid actor, string digest, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var changed = await db.ExecuteAsync(new CommandDefinition("""
                update hic_github_publications set state='pending',approved_at_utc=now(),updated_at_utc=now()
                where publication_id=@id and actor_id=@actor and digest=@digest and state='preview'
                    and (plan_json->>'expiresAtUtc')::timestamptz>now();
                """, new { id, actor, digest }, tx, cancellationToken: ct));
            if (changed != 1) return false;
            await db.ExecuteAsync(new CommandDefinition("""
                insert into hic_github_publication_events(publication_id,event_code) values(@id,'approved');
                """, new { id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    internal async Task SaveAsync(GitHubPublication row, string eventCode, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var changed = await db.ExecuteAsync(new CommandDefinition("""
            update hic_github_publications set state=@State,blob_sha=@BlobSha,tree_sha=@TreeSha,
                commit_sha=@CommitSha,remote_sha=@RemoteSha,pr_number=@PrNumber,pr_url=@PrUrl,
                error_code=@ErrorCode,updated_at_utc=now()
            where publication_id=@PublicationId and actor_id=@ActorId and approved_at_utc is not null;
            """, row, tx, cancellationToken: ct));
        GitHubAppBroker.Check(changed == 1, "publication-state-not-recorded");
        await db.ExecuteAsync(new CommandDefinition("""
            insert into hic_github_publication_events(publication_id,event_code) values(@PublicationId,@eventCode);
            """, new { row.PublicationId, eventCode }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal async Task<NpgsqlConnection?> LockAsync(Guid id, CancellationToken ct)
    {
        var db = new NpgsqlConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            var acquired = await db.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select pg_try_advisory_lock(hashtextextended(@id,52));",
                new { id = id.ToString() }, cancellationToken: ct));
            if (acquired) return db;
            await db.DisposeAsync();
            return null;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    internal static async Task UnlockAsync(NpgsqlConnection db, Guid id)
    {
        await db.ExecuteAsync("select pg_advisory_unlock(hashtextextended(@id,52));", new { id = id.ToString() });
    }
}
