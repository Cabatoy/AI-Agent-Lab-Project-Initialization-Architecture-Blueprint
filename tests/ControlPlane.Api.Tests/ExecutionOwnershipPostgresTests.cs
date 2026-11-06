using Dapper;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class ExecutionOwnershipPostgresTests
{
    [PostgresFact]
    public async Task ClaimAndFinalize_PersistIdentityAndAtomicAudit()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var request = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            var claim = await ExecutionOwnershipStore.ClaimAsync(cs, id, request);
            Assert.True(claim.NewlyClaimed);
            Assert.Equal(TransitionOutcome.Accepted, claim.Outcome);
            Assert.Equal(1, claim.Execution!.Generation);
            var proof = new ExecutionIdentity(request.ExecutionId, request.WorkerId, 1);
            var final = await TaskTransitionStore.ApplyAsync(cs, id, "Completed", "R2Test",
                new("Completed", 1, "OK", null, "Executing", proof));
            Assert.Equal(TransitionOutcome.Accepted, final.Outcome);
            Assert.Equal("Completed", await db.QuerySingleAsync<string>("select status from tasks where id=@id", new { id }));
            Assert.Equal("Completed", await db.QuerySingleAsync<string>("select status from task_executions"));
            Assert.Equal(2, await db.QuerySingleAsync<int>(
                "select count(*)::int from task_status_transitions where execution_id=@ExecutionId", request));
            Assert.NotNull(await db.QuerySingleAsync<DateTime?>("select completed_at_utc from task_executions"));
        });
    }

    [PostgresFact]
    public async Task ConcurrentOwners_OnlyOneClaimAndAudit()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<ClaimExecutionResult> Claim()
            {
                await barrier.Task;
                return await ExecutionOwnershipStore.ClaimAsync(cs, id, new(Guid.NewGuid(), Guid.NewGuid()));
            }
            var a = Claim();
            var b = Claim();
            barrier.SetResult();
            var results = await Task.WhenAll(a, b);
            Assert.Single(results, x => x.NewlyClaimed);
            Assert.Single(results, x => x.Outcome == TransitionOutcome.Conflict);
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_executions"));
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task DuplicateClaim_IsReadOnlyReplayAcrossConnections()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var request = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            var first = await ExecutionOwnershipStore.ClaimAsync(cs, id, request);
            var replay = await ExecutionOwnershipStore.ClaimAsync(cs, id, request);
            Assert.Equal(first.Execution, replay.Execution);
            Assert.False(replay.NewlyClaimed);
            Assert.Equal(TransitionOutcome.Accepted, replay.Outcome);
            var other = await ExecutionOwnershipStore.ClaimAsync(cs, id, request with { WorkerId = Guid.NewGuid() });
            Assert.Equal(TransitionOutcome.Conflict, other.Outcome);
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task StaleGenerationAndIdentity_CannotFinalizeNewAttempt()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var firstRequest = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            var first = await ExecutionOwnershipStore.ClaimAsync(cs, id, firstRequest);
            var firstProof = new ExecutionIdentity(firstRequest.ExecutionId, firstRequest.WorkerId, first.Execution!.Generation);
            await TaskTransitionStore.ApplyAsync(cs, id, "Failed", "R2Test",
                new("Failed", 1, null, "synthetic", "Executing", firstProof));
            await TaskTransitionStore.ApplyAsync(cs, id, "Retrying", "R2Test");
            var secondRequest = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            var second = await ExecutionOwnershipStore.ClaimAsync(cs, id, secondRequest);
            Assert.Equal(2, second.Execution!.Generation);
            foreach (var proof in new[]
            {
                firstProof,
                new ExecutionIdentity(secondRequest.ExecutionId, secondRequest.WorkerId, 1),
                new ExecutionIdentity(Guid.NewGuid(), secondRequest.WorkerId, 2)
            })
            {
                var result = await TaskTransitionStore.ApplyAsync(cs, id, "Completed", "R2Test",
                    new("Completed", 1, "stale", null, "Executing", proof));
                Assert.Equal(TransitionOutcome.Conflict, result.Outcome);
            }
            Assert.Equal("Executing", await db.QuerySingleAsync<string>("select status from tasks where id=@id", new { id }));
            Assert.Equal(4, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
            var ownerProof = new ExecutionIdentity(secondRequest.ExecutionId, secondRequest.WorkerId, 2);
            Assert.Equal(TransitionOutcome.Accepted, (await TaskTransitionStore.ApplyAsync(cs, id, "Completed", "R2Test",
                new("Completed", 1, "current", null, "Executing", ownerProof))).Outcome);
        });
    }

    [PostgresFact]
    public async Task LegacyStatusCannotClaimOrBypassOwner()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var legacy = await TaskTransitionStore.ApplyAsync(cs, id, "Executing", "R2Test",
                new("Executing", 1, null, null, "Approved"));
            Assert.Equal(TransitionOutcome.Conflict, legacy.Outcome);
            await ExecutionOwnershipStore.ClaimAsync(cs, id, new(Guid.NewGuid(), Guid.NewGuid()));
            var final = await TaskTransitionStore.ApplyAsync(cs, id, "Completed", "R2Test",
                new("Completed", 1, "legacy", null, "Executing"));
            Assert.Equal(TransitionOutcome.Conflict, final.Outcome);
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task ReviewTransitions_KeepSameOwnerUntilCompletion()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var request = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            await ExecutionOwnershipStore.ClaimAsync(cs, id, request);
            var proof = new ExecutionIdentity(request.ExecutionId, request.WorkerId, 1);
            var current = "Executing";
            foreach (var target in new[] { "Review", "InProgress", "Review", "Completed" })
            {
                var result = await TaskTransitionStore.ApplyAsync(cs, id, target, "R2Test",
                    new(target, 1, null, null, current, proof));
                Assert.Equal(TransitionOutcome.Accepted, result.Outcome);
                Assert.Equal(target, await db.QuerySingleAsync<string>("select status from task_executions"));
                Assert.Equal(target != "Completed",
                    await db.QuerySingleAsync<bool>("select completed_at_utc is null from task_executions"));
                current = target;
            }
            Assert.Equal(5, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task AuditFailure_RollsBackClaimAndGeneration()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            await db.ExecuteAsync("alter table task_status_transitions add constraint reject_claim check (source <> 'ClaimExecution')");
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecutionOwnershipStore.ClaimAsync(cs, id, new(Guid.NewGuid(), Guid.NewGuid())));
            Assert.Equal("Approved", await db.QuerySingleAsync<string>("select status from tasks where id=@id", new { id }));
            Assert.Equal(0, await db.QuerySingleAsync<long>("select execution_generation from tasks where id=@id", new { id }));
            Assert.Equal(0, await db.QuerySingleAsync<int>("select count(*)::int from task_executions"));
            Assert.Equal(0, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }

    [PostgresFact]
    public async Task AuditFailure_RollsBackFinalizationAndOwner()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var request = new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid());
            await ExecutionOwnershipStore.ClaimAsync(cs, id, request);
            await db.ExecuteAsync("alter table task_status_transitions add constraint reject_finish check (source <> 'R2Test')");
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                TaskTransitionStore.ApplyAsync(cs, id, "Completed", "R2Test",
                    new("Completed", 1, "OK", null, "Executing", new(request.ExecutionId, request.WorkerId, 1))));
            Assert.Equal("Executing", await db.QuerySingleAsync<string>("select status from tasks where id=@id", new { id }));
            Assert.Null(await db.QuerySingleAsync<DateTime?>("select completed_at_utc from task_executions"));
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from task_status_transitions"));
        });
    }
}
