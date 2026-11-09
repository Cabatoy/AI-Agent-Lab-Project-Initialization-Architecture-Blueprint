using Dapper;
using Npgsql;

internal sealed record IssueImportPreview(Guid PreviewId, Guid ActorId, long RepositoryId, long IssueId,
    int IssueNumber, string Reference, string RevisionSha256, string Title, string Description, DateTime ExpiresAtUtc);
internal sealed record IssueImportResult(Guid TaskId, bool Replayed);

internal sealed class GitHubIssueImportStore(string connectionString)
{
    internal async Task<bool> Ready(CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists(select 1 from app_schema_versions where version=11)", cancellationToken: ct));
    }

    internal async Task<IssueImportPreview> Prepare(Guid actor, GitHubIssueSource source,
        string title, string description, CancellationToken ct)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(description) || description.Length > 4000 ||
            description.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new GitHubPublishException("issue-draft-invalid");
        var preview = new IssueImportPreview(Guid.NewGuid(), actor, source.RepositoryId, source.IssueId,
            source.Reference.Number, source.Reference.Url, source.RevisionSha256, title.Trim(), description.Trim(),
            DateTime.UtcNow.AddMinutes(5));
        await using var db = new NpgsqlConnection(connectionString);
        await db.ExecuteAsync(new CommandDefinition(@"
            insert into hic_issue_import_previews(preview_id,actor_id,repository_id,issue_id,issue_number,
              reference,revision_sha256,title,description,expires_at_utc)
            values(@PreviewId,@ActorId,@RepositoryId,@IssueId,@IssueNumber,@Reference,
              @RevisionSha256,@Title,@Description,@ExpiresAtUtc)", preview, cancellationToken: ct));
        return preview;
    }

    internal async Task<IssueImportPreview?> Read(Guid id, Guid actor, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(connectionString);
        return await db.QuerySingleOrDefaultAsync<IssueImportPreview>(new CommandDefinition(@"
            select preview_id as PreviewId, actor_id as ActorId, repository_id as RepositoryId,
              issue_id as IssueId, issue_number as IssueNumber, reference, revision_sha256 as RevisionSha256,
              title, description, expires_at_utc as ExpiresAtUtc
            from hic_issue_import_previews where preview_id=@id and actor_id=@actor",
            new { id, actor }, cancellationToken: ct));
    }

    internal async Task<IssueImportResult?> Confirm(IssueImportPreview preview, GitHubIssueSource current,
        LocalOperatorSessions sessions, string? header, CancellationToken ct)
    {
        if (current.RepositoryId != preview.RepositoryId || current.IssueId != preview.IssueId ||
            current.Reference.Number != preview.IssueNumber || current.RevisionSha256 != preview.RevisionSha256 ||
            current.Reference.Url != preview.Reference)
            throw new GitHubPublishException("issue-revision-changed");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtextextended(@key, 7272))",
            new { key = $"{preview.RepositoryId}:{preview.IssueNumber}:{preview.RevisionSha256}" },
            tx, cancellationToken: ct));
        IssueImportResult? result = null;
        var authorized = await sessions.CommitDecisionAsync(header, preview.ActorId, async () =>
        {
            var stored = await db.QuerySingleOrDefaultAsync<(Guid TaskId, Guid ActorId)>(new CommandDefinition(@"
                select task_id as TaskId,actor_id as ActorId from hic_issue_imports
                where repository_id=@RepositoryId and issue_number=@IssueNumber and revision_sha256=@RevisionSha256",
                preview, tx, cancellationToken: ct));
            if (stored.TaskId != Guid.Empty)
            {
                if (stored.ActorId != preview.ActorId) throw new GitHubPublishException("issue-import-already-exists");
                result = new(stored.TaskId, true);
            }
            else
            {
                if (DateTime.UtcNow >= preview.ExpiresAtUtc) throw new GitHubPublishException("issue-preview-expired");
                var taskId = Guid.NewGuid();
                await db.ExecuteAsync(new CommandDefinition(@"
                    insert into tasks(id,title,description,status,attempt_count,result,error_message,created_at_utc,updated_at_utc)
                    values(@taskId,@Title,@Description,'Draft',0,null,null,now(),now());
                    insert into hic_issue_imports(repository_id,issue_number,revision_sha256,issue_id,actor_id,preview_id,task_id)
                    values(@RepositoryId,@IssueNumber,@RevisionSha256,@IssueId,@ActorId,@PreviewId,@taskId)",
                    new { taskId, preview.Title, preview.Description, preview.RepositoryId, preview.IssueNumber,
                        preview.RevisionSha256, preview.IssueId, preview.ActorId, preview.PreviewId }, tx, cancellationToken: ct));
                result = new(taskId, false);
            }
            await tx.CommitAsync(ct);
        }, ct);
        return authorized ? result : null;
    }
}
