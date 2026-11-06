using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIC_R1_TEST_POSTGRES")))
            Skip = "Requires separately approved isolated PostgreSQL via HIC_R1_TEST_POSTGRES.";
    }
}

[Trait("Category", "PostgreSQLIntegration")]
public class TaskTransitionPostgresTests
{
    [PostgresFact]
    public async Task AcceptedTransition_CommitsStateAndSingleAudit()
    {
        await WithDatabase(async (cs, db, id) =>
        {
            var result = await Apply(cs, id, "Executing", "Approved");
            Assert.Equal(TransitionOutcome.Accepted, result.Outcome);
            Assert.Equal("Executing", await db.QuerySingleAsync<string>("select status from tasks where id = @id", new { id }));
            Assert.Equal("Approved>Executing", await db.QuerySingleAsync<string>(
                "select from_status || '>' || to_status from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task InvalidTransition_DoesNotMutateStateOrAudit()
    {
        await WithDatabase(async (cs, db, id) =>
        {
            var result = await Apply(cs, id, "Completed", "Approved");
            Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
            await AssertUnchanged(db, id);
        });
    }

    [PostgresFact]
    public async Task ConcurrentExpectedStateTransitions_OnlyOneCommits()
    {
        await WithDatabase(async (cs, db, id) =>
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<TransitionResult> Contend()
            {
                await start.Task;
                return await Apply(cs, id, "Executing", "Approved");
            }
            var first = Contend();
            var second = Contend();
            start.SetResult();
            var outcomes = await Task.WhenAll(first, second);
            Assert.Single(outcomes, x => x.Outcome == TransitionOutcome.Accepted);
            Assert.Single(outcomes, x => x.Outcome == TransitionOutcome.Conflict);
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task AuditInsertFailure_RollsBackTaskUpdate()
    {
        await WithDatabase(async (cs, db, id) =>
        {
            await db.ExecuteAsync("alter table task_status_transitions add constraint reject_test_audit check (source <> 'R1Test')");
            await Assert.ThrowsAsync<PostgresException>(() => Apply(cs, id, "Executing", "Approved"));
            await AssertUnchanged(db, id);
        });
    }

    private static Task<TransitionResult> Apply(string cs, Guid id, string target, string expected) =>
        TaskTransitionStore.ApplyAsync(cs, id, target, "R1Test",
            new UpdateTaskStatusRequest(target, 1, null, null, expected));

    private static async Task AssertUnchanged(NpgsqlConnection db, Guid id)
    {
        Assert.Equal("Approved", await db.QuerySingleAsync<string>("select status from tasks where id = @id", new { id }));
        Assert.Equal(0, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
    }

    private static async Task WithDatabase(Func<string, NpgsqlConnection, Guid, Task> test)
    {
        var cs = PostgresFixtureSafety.Configuration(Environment.GetEnvironmentVariable("HIC_R1_TEST_POSTGRES"));
        var schema = "hic_r1_" + Guid.NewGuid().ToString("N");
        var marker = "HIC:R1:fixture:" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(cs);
        await admin.OpenAsync();
        await VerifyTarget(admin);
        FixtureIdentity? identity = null;
        await using (var setup = await admin.BeginTransactionAsync())
        {
            await admin.ExecuteAsync($@"
                create schema {schema};
                comment on schema {schema} is '{marker}';
                create table {schema}.tasks (
                    id uuid primary key, status text not null, attempt_count integer not null default 0,
                    result text, error_message text, updated_at_utc timestamptz not null default now());
                create table {schema}.task_status_transitions (
                    id uuid primary key, task_id uuid not null references {schema}.tasks(id),
                    from_status text not null, to_status text not null, source text not null,
                    changed_at_utc timestamptz not null, note text);", transaction: setup);
            identity = await ReadFixture(admin, schema, setup);
            if (identity is null || identity.Marker != marker)
                throw new InvalidOperationException("Fixture initialization identity mismatch.");
            PostgresFixtureSafety.Cleanup(identity, identity);
            await setup.CommitAsync();
        }
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema, Pooling = false }.ConnectionString;
            await using var db = new NpgsqlConnection(isolated);
            await db.OpenAsync();
            await VerifyTarget(db);
            var id = Guid.NewGuid();
            await db.ExecuteAsync("insert into tasks (id, status) values (@id, 'Approved')", new { id });
            await test(isolated, db, id);
        }
        finally
        {
            await VerifyTarget(admin);
            await using var cleanup = await admin.BeginTransactionAsync();
            await admin.ExecuteAsync(
                $"lock table {schema}.tasks, {schema}.task_status_transitions in access exclusive mode;",
                transaction: cleanup);
            PostgresFixtureSafety.Cleanup(identity,
                await ReadFixture(admin, schema, cleanup));
            await admin.ExecuteAsync($@"
                drop table {schema}.task_status_transitions restrict;
                drop table {schema}.tasks restrict;
                drop schema {schema} restrict;", transaction: cleanup);
            await cleanup.CommitAsync();
        }
    }

    private static async Task VerifyTarget(NpgsqlConnection connection)
    {
        var identity = await connection.QuerySingleAsync<TargetIdentity>(@"
            select d.datname as DatabaseName, pg_get_userbyid(d.datdba) as Owner,
                   current_user::text as CurrentRole,
                   shobj_description(d.oid, 'pg_database') as Marker,
                   r.rolsuper as Superuser, r.rolcreaterole as CreateRole, r.rolcreatedb as CreateDatabase
            from pg_database d join pg_roles r on r.rolname = current_user
            where d.datname = current_database();");
        PostgresFixtureSafety.Target(identity);
    }

    private static Task<FixtureIdentity?> ReadFixture(
        NpgsqlConnection connection, string schema, NpgsqlTransaction transaction) =>
        connection.QuerySingleOrDefaultAsync<FixtureIdentity>(@"
            select n.nspname::text as Schema, pg_get_userbyid(n.nspowner) as Owner,
                   obj_description(n.oid, 'pg_namespace') as Marker, n.oid::bigint as SchemaOid,
                   t.oid::bigint as TasksOid, a.oid::bigint as AuditOid
            from pg_namespace n
            join pg_class t on t.relnamespace = n.oid and t.relname = 'tasks' and t.relkind = 'r'
            join pg_class a on a.relnamespace = n.oid and a.relname = 'task_status_transitions' and a.relkind = 'r'
            where n.nspname = @schema
              and t.relowner = n.nspowner and a.relowner = n.nspowner;",
            new { schema }, transaction);
}
