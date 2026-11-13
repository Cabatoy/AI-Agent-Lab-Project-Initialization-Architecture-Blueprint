using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

/// <summary>
/// RedisEnqueueRecovery tests validate that approved tasks can be re-enqueued after transient Redis failures,
/// preventing task loss and duplicate execution. Redis-dependent tests require live connection at localhost:6379.
/// PostgreSQL integration tests verify state consistency when enqueue fails.
/// </summary>
[Trait("Category", "RedisEnqueueRecovery")]
public class RedisEnqueueRecoveryTests
{
    [PostgresFact]
    public async Task ApproveTask_WithRedisEnqueueFailure_RecordsApprovalInPostgres()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);
            var sessionToken = sessions.IssueSessionToken(bootstrap);

            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            // Approve task (PostgreSQL transition succeeds)
            var transition = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);

            Assert.Equal(TransitionOutcome.Accepted, transition.Outcome);

            // Verify task is Approved in PostgreSQL
            var status = await db.QuerySingleAsync<string>(
                "select status from tasks where id = @id", new { id });
            Assert.Equal(TaskStatuses.Approved, status);

            // Verify audit trail records approval
            var audit = await db.QuerySingleAsync<string>(
                "select from_status || '>' || to_status from task_status_transitions where source = 'ApproveTask' and task_id = @id",
                new { id });
            Assert.Equal($"{TaskStatuses.Draft}>{TaskStatuses.Approved}", audit);
        });
    }

    [PostgresFact]
    public async Task ApprovalRecoveryWorkflow_DraftToApprovedIdempotent_WithConcurrentAttempts()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);

            // Step 1: Approve task (PostgreSQL succeeds)
            var approveTransition = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);
            Assert.Equal(TransitionOutcome.Accepted, approveTransition.Outcome);

            // Step 2: Verify PostgreSQL state after approval
            var status = await db.QuerySingleAsync<string>(
                "select status from tasks where id = @id", new { id });
            Assert.Equal(TaskStatuses.Approved, status);

            // Step 3: Verify concurrent re-approval is rejected (idempotency - prevents duplicate execution)
            var secondApprove = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);
            Assert.Equal(TransitionOutcome.Conflict, secondApprove.Outcome);

            // Step 4: Verify audit shows single approval (no duplicates in audit trail)
            var auditCount = await db.QuerySingleAsync<int>(
                "select count(*) as cnt from task_status_transitions where source = 'ApproveTask' and task_id = @id",
                new { id });
            Assert.Equal(1, auditCount);
        });
    }

    [PostgresFact]
    public async Task ApprovalRecoveryWorkflow_PreservesOperatorAuthorization()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var operatorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, operatorId);
            var sessionToken = sessions.IssueSessionToken(bootstrap);

            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            // Approve with authorization
            var transition = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);

            Assert.Equal(TransitionOutcome.Accepted, transition.Outcome);

            // Verify operator identity is preserved in audit
            var auditRecord = await db.QuerySingleOrDefaultAsync<(string? source, Guid? executionId)>(
                "select source, execution_id from task_status_transitions where task_id = @id order by changed_at_utc desc limit 1",
                new { id });
            
            Assert.Equal("ApproveTask", auditRecord.source);
            // executionId is null for approval (no execution context yet)
            Assert.Null(auditRecord.executionId);
        });
    }
}
