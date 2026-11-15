using Dapper;
using Npgsql;

// Durable observe-only intent ledger. No queue, GitHub write, or agent execution.
// Schema provisioning and endpoint authorization are separate explicit gates.
internal static class RelayHandoffLedger
{
    internal const string SchemaSql = """
        create table if not exists relay_handoff_intents (
            id uuid primary key,
            task_id uuid not null references tasks(id) on delete restrict,
            idempotency_key text not null unique,
            snapshot_digest char(64) not null,
            repository text not null,
            expected_base_ref text not null,
            state text not null default 'Recorded'
                check (state = 'Recorded'),
            created_at_utc timestamptz not null default now(),
            constraint ck_relay_handoff_digest
                check (snapshot_digest ~ '^[0-9a-f]{64}$'),
            constraint ck_relay_handoff_key_length
                check (length(idempotency_key) between 32 and 128)
        );
        create index if not exists ix_relay_handoff_task
            on relay_handoff_intents(task_id, created_at_utc desc);
        """;

    // Unique key arbitrates concurrent requests atomically; a replay with changed
    // payload MUST be a conflict, never silently treated as the original intent.
    internal static async Task<RelayHandoffRecordResult> RecordAsync(
        string connectionString, Guid taskId, string idempotencyKey,
        string snapshotDigest, string repository, string expectedBaseRef,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty ||
            !RelayHandoffIntent.IsValidIdempotencyKey(idempotencyKey) ||
            !RelayHandoffIntent.Matches(snapshotDigest, snapshotDigest))
            throw new ArgumentException("Invalid handoff ledger identity.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = new CommandDefinition("""
            insert into relay_handoff_intents
                (id, task_id, idempotency_key, snapshot_digest, repository, expected_base_ref)
            values
                (@Id, @TaskId, @Key, @Digest, @Repository, @BaseRef)
            on conflict (idempotency_key) do nothing
            returning id;
            """,
            new { Id = Guid.NewGuid(), TaskId = taskId, Key = idempotencyKey,
                Digest = snapshotDigest, Repository = repository, BaseRef = expectedBaseRef },
            cancellationToken: cancellationToken);
        var inserted = await connection.QuerySingleOrDefaultAsync<Guid?>(command);
        if (inserted.HasValue)
            return new RelayHandoffRecordResult(inserted.Value, true, false);

        var existing = await connection.QuerySingleAsync<RelayHandoffLedgerRow>(
            new CommandDefinition("""
                select id, task_id as TaskId, snapshot_digest as SnapshotDigest,
                       repository, expected_base_ref as ExpectedBaseRef
                from relay_handoff_intents where idempotency_key = @Key;
                """, new { Key = idempotencyKey }, cancellationToken: cancellationToken));
        var replay = existing.TaskId == taskId &&
                     RelayHandoffIntent.Matches(existing.SnapshotDigest, snapshotDigest) &&
                     string.Equals(existing.Repository, repository, StringComparison.Ordinal) &&
                     string.Equals(existing.ExpectedBaseRef, expectedBaseRef, StringComparison.Ordinal);
        return new RelayHandoffRecordResult(existing.Id, false, !replay);
    }

    internal sealed record RelayHandoffRecordResult(Guid Id, bool Created, bool Conflict);
    private sealed record RelayHandoffLedgerRow(Guid Id, Guid TaskId, string SnapshotDigest,
        string Repository, string ExpectedBaseRef);
}
