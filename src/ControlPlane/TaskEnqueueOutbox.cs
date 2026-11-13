using Dapper;
using Npgsql;
using StackExchange.Redis;

internal enum EnqueueAttemptOutcome
{
    Enqueued,
    AlreadyQueued,
    AlreadyClaimed,
    RedisUnavailable,
    NotFound,
    NotPending
}

internal enum QueuePublishOutcome
{
    Enqueued,
    AlreadyQueued,
    Unavailable
}

internal interface ITaskQueuePublisher
{
    Task<QueuePublishOutcome> EnqueueIfAbsentAsync(Guid taskId);
}

internal sealed class RedisTaskQueuePublisher(IDatabase redisDb, string queueKey) : ITaskQueuePublisher
{
    private const string EnqueueIfAbsentScript = """
        local items = redis.call('LRANGE', KEYS[1], 0, -1)
        for i = 1, #items do
            if items[i] == ARGV[1] then
                return 0
            end
        end
        redis.call('RPUSH', KEYS[1], ARGV[1])
        return 1
        """;

    public async Task<QueuePublishOutcome> EnqueueIfAbsentAsync(Guid taskId)
    {
        try
        {
            var result = await redisDb.ScriptEvaluateAsync(
                EnqueueIfAbsentScript,
                new RedisKey[] { queueKey },
                new RedisValue[] { taskId.ToString() });
            return (long)result == 1 ? QueuePublishOutcome.Enqueued : QueuePublishOutcome.AlreadyQueued;
        }
        catch (RedisConnectionException)
        {
            return QueuePublishOutcome.Unavailable;
        }
        catch (RedisTimeoutException)
        {
            return QueuePublishOutcome.Unavailable;
        }
    }
}

internal static class TaskEnqueueOutbox
{
    internal static async Task<EnqueueAttemptOutcome> TryEnqueueAsync(
        string connectionString,
        Guid taskId,
        ITaskQueuePublisher queue,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var status = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "select status from tasks where id = @taskId for update;",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        if (status is null)
            return EnqueueAttemptOutcome.NotFound;

        var claimedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            "select claimed_at_utc from task_enqueue_outbox where task_id = @taskId for update;",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        if (claimedAt is not null)
            return EnqueueAttemptOutcome.AlreadyClaimed;
        if (status != TaskStatuses.Approved || !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists(select 1 from task_enqueue_outbox where task_id = @taskId);",
                new { taskId }, transaction, cancellationToken: cancellationToken)))
            return EnqueueAttemptOutcome.NotPending;

        var publishOutcome = await queue.EnqueueIfAbsentAsync(taskId);
        if (publishOutcome == QueuePublishOutcome.Unavailable)
        {
            await RecordAttemptAsync(connection, transaction, taskId, enqueued: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EnqueueAttemptOutcome.RedisUnavailable;
        }

        await RecordAttemptAsync(
            connection, transaction, taskId, enqueued: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return publishOutcome == QueuePublishOutcome.Enqueued
            ? EnqueueAttemptOutcome.Enqueued
            : EnqueueAttemptOutcome.AlreadyQueued;
    }

    private static Task RecordAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid taskId,
        bool enqueued,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(@"
            update task_enqueue_outbox
            set attempt_count = attempt_count + 1,
                last_attempt_at_utc = now(),
                last_enqueued_at_utc = case when @Enqueued then now() else last_enqueued_at_utc end
            where task_id = @TaskId;",
            new { TaskId = taskId, Enqueued = enqueued }, transaction, cancellationToken: cancellationToken));
}
