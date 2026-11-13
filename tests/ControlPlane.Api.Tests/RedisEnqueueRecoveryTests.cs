using System.Collections.Concurrent;
using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

/// <summary>
/// PostgreSQL integration tests for durable approval delivery and single execution claims.
/// The queue publisher is deterministic so these tests do not require a live Redis service.
/// </summary>
[Trait("Category", "RedisEnqueueRecovery")]
public class RedisEnqueueRecoveryTests
{
    [PostgresFact]
    public async Task ApprovalOutbox_RetainsTaskAfterRedisFailure_AndRecoveryEnqueuesIt()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            var transition = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);
            Assert.Equal(TransitionOutcome.Accepted, transition.Outcome);

            var queue = new MemoryTaskQueuePublisher(failuresRemaining: 1);
            var failedAttempt = await TaskEnqueueOutbox.TryEnqueueAsync(cs, id, queue);
            Assert.Equal(EnqueueAttemptOutcome.RedisUnavailable, failedAttempt);
            Assert.Equal(TaskStatuses.Approved,
                await db.QuerySingleAsync<string>("select status from tasks where id = @id", new { id }));

            var pending = await db.QuerySingleAsync<OutboxState>(@"
                select attempt_count as AttemptCount, last_enqueued_at_utc as LastEnqueuedAtUtc,
                       claimed_at_utc as ClaimedAtUtc
                from task_enqueue_outbox where task_id = @id;", new { id });
            Assert.Equal(1, pending.AttemptCount);
            Assert.Null(pending.LastEnqueuedAtUtc);
            Assert.Null(pending.ClaimedAtUtc);

            var recoveredAttempt = await TaskEnqueueOutbox.TryEnqueueAsync(cs, id, queue);
            Assert.Equal(EnqueueAttemptOutcome.Enqueued, recoveredAttempt);
            Assert.True(queue.Contains(id));

            var delivered = await db.QuerySingleAsync<OutboxState>(@"
                select attempt_count as AttemptCount, last_enqueued_at_utc as LastEnqueuedAtUtc,
                       claimed_at_utc as ClaimedAtUtc
                from task_enqueue_outbox where task_id = @id;", new { id });
            Assert.Equal(2, delivered.AttemptCount);
            Assert.NotNull(delivered.LastEnqueuedAtUtc);
            Assert.Null(delivered.ClaimedAtUtc);
        });
    }

    [PostgresFact]
    public async Task ConcurrentRecoveryAndExecutionClaims_DoNotDuplicateQueueEntryOrExecution()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });
            var approval = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);
            Assert.Equal(TransitionOutcome.Accepted, approval.Outcome);

            var queue = new MemoryTaskQueuePublisher();
            var enqueueAttempts = await Task.WhenAll(
                TaskEnqueueOutbox.TryEnqueueAsync(cs, id, queue),
                TaskEnqueueOutbox.TryEnqueueAsync(cs, id, queue));
            Assert.Single(enqueueAttempts, result => result == EnqueueAttemptOutcome.Enqueued);
            Assert.Single(enqueueAttempts, result => result == EnqueueAttemptOutcome.AlreadyQueued);
            Assert.Equal(1, queue.Count);

            var claims = await Task.WhenAll(
                ExecutionOwnershipStore.ClaimAsync(cs, id,
                    new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid())),
                ExecutionOwnershipStore.ClaimAsync(cs, id,
                    new ClaimExecutionRequest(Guid.NewGuid(), Guid.NewGuid())));
            Assert.Single(claims, claim => claim.Outcome == TransitionOutcome.Accepted);
            Assert.Single(claims, claim => claim.Outcome == TransitionOutcome.Conflict);

            var outbox = await db.QuerySingleAsync<OutboxState>(@"
                select attempt_count as AttemptCount, last_enqueued_at_utc as LastEnqueuedAtUtc,
                       claimed_at_utc as ClaimedAtUtc
                from task_enqueue_outbox where task_id = @id;", new { id });
            Assert.NotNull(outbox.ClaimedAtUtc);
            Assert.Equal(1, await db.QuerySingleAsync<int>(
                "select count(*) from task_executions where task_id = @id", new { id }));
        });
    }

    [PostgresFact]
    public async Task ApprovalAuditRecordsExpectedTransition()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            var transition = await TaskTransitionStore.ApplyAsync(
                cs, id, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);

            Assert.Equal(TransitionOutcome.Accepted, transition.Outcome);
            var audit = await db.QuerySingleAsync<string>(
                "select from_status || '>' || to_status from task_status_transitions where source = 'ApproveTask' and task_id = @id",
                new { id });
            Assert.Equal($"{TaskStatuses.Draft}>{TaskStatuses.Approved}", audit);
        });
    }

    private sealed record OutboxState(int AttemptCount, DateTime? LastEnqueuedAtUtc, DateTime? ClaimedAtUtc);

    private sealed class MemoryTaskQueuePublisher(int failuresRemaining = 0) : ITaskQueuePublisher
    {
        private readonly ConcurrentDictionary<Guid, byte> _queued = new();
        private int _failuresRemaining = failuresRemaining;

        internal int Count => _queued.Count;

        internal bool Contains(Guid taskId) => _queued.ContainsKey(taskId);

        public Task<QueuePublishOutcome> EnqueueIfAbsentAsync(Guid taskId)
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _failuresRemaining);
                if (remaining == 0)
                    break;
                if (Interlocked.CompareExchange(ref _failuresRemaining, remaining - 1, remaining) == remaining)
                    return Task.FromResult(QueuePublishOutcome.Unavailable);
            }

            return Task.FromResult(_queued.TryAdd(taskId, 0)
                ? QueuePublishOutcome.Enqueued
                : QueuePublishOutcome.AlreadyQueued);
        }
    }
}
