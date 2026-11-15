using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

public sealed class RelayHandoffStoreTests
{
    [PostgresFact]
    public async Task Record_IsImmutableObserveOnly_AndIdempotent()
    {
        await WithLedger(async (store, db, taskId) =>
        {
            var actor = Guid.NewGuid();
            var key = new string('a', 32);
            var digest = await GetDigest(db, taskId);
            var first = await store.RecordAsync(taskId, actor, key, digest,
                "Cabatoy/example", "develop", CancellationToken.None);
            Assert.Equal("recorded", first.Code);
            Assert.NotNull(first.Record);
            Assert.False(first.Record.ExecutionAllowed);
            Assert.Equal("recorded-untrusted", first.Record.Status);
            Assert.Equal("staging:observe-only", first.Record.Scope);

            var replay = await store.RecordAsync(taskId, actor, key, digest,
                "Cabatoy/example", "develop", CancellationToken.None);
            Assert.Equal("replay", replay.Code);
            Assert.Equal(first.Record.HandoffId, replay.Record!.HandoffId);
            Assert.Equal("conflict", (await store.RecordAsync(taskId, actor,
                new string('b', 32), digest, "Cabatoy/example", "develop",
                CancellationToken.None)).Code);
            Assert.Equal("conflict", (await store.RecordAsync(taskId, Guid.NewGuid(),
                key, digest, "Cabatoy/example", "develop",
                CancellationToken.None)).Code);
            Assert.Null(await store.GetAsync(first.Record.HandoffId, Guid.NewGuid(), CancellationToken.None));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>(
                "select count(*)::int from hic_relay_handoffs"));
            Assert.Equal("Draft", await db.ExecuteScalarAsync<string>(
                "select status from tasks where id=@taskId", new { taskId }));
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
                "update hic_relay_handoffs set execution_allowed=true"));
        });
    }

    [PostgresFact]
    public async Task ChangedSnapshot_RejectsOldDigest_WithoutWriting()
    {
        await WithLedger(async (store, db, taskId) =>
        {
            var digest = await GetDigest(db, taskId);
            await db.ExecuteAsync("update tasks set description='Modified', updated_at_utc=now() where id=@taskId",
                new { taskId });
            var outcome = await store.RecordAsync(taskId, Guid.NewGuid(),
                new string('k', 32), digest, "Cabatoy/example", "develop",
                CancellationToken.None);
            Assert.Equal("intent-changed", outcome.Code);
            Assert.Equal(0, await db.ExecuteScalarAsync<int>(
                "select count(*)::int from hic_relay_handoffs"));
        });
    }

    [PostgresFact]
    public async Task ParallelSameKey_RecordsExactlyOnce()
    {
        await WithLedger(async (store, db, taskId) =>
        {
            var actor = Guid.NewGuid();
            var digest = await GetDigest(db, taskId);
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                store.RecordAsync(taskId, actor, new string('q', 32), digest,
                    "Cabatoy/example", "develop", CancellationToken.None)));
            Assert.Contains(results, result => result.Code == "recorded");
            Assert.Contains(results, result => result.Code == "replay");
            Assert.Equal(1, await db.ExecuteScalarAsync<int>(
                "select count(*)::int from hic_relay_handoffs"));
        });
    }

    private static async Task<string> GetDigest(NpgsqlConnection db, Guid taskId)
    {
        var task = await db.QuerySingleAsync<(string Title, string Description, DateTime UpdatedAtUtc)>(
            @"select title as Title, description as Description,
                updated_at_utc as UpdatedAtUtc from tasks where id=@taskId",
            new { taskId });
        return RelayHandoffIntent.Digest(taskId, task.Title, task.Description,
            task.UpdatedAtUtc, "Cabatoy/example", "develop");
    }

    private static Task WithLedger(
        Func<RelayHandoffStore, NpgsqlConnection, Guid, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (cs, db, taskId) =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HumanInControl.sln")))
                root = root.Parent;
            Assert.NotNull(root);
            await db.ExecuteAsync(
                "alter table tasks add column title text not null default 'Build', add column description text not null default 'Test';");
            var migration = await File.ReadAllTextAsync(Path.Combine(root.FullName,
                "src", "ControlPlane", "Migrations", "005-relay-handoff-observe-only.sql"));
            await db.ExecuteAsync(migration);
            Assert.True(await new RelayHandoffStore(cs).IsSchemaReadyAsync(CancellationToken.None));
            await test(new RelayHandoffStore(cs), db, taskId);
        });
}
