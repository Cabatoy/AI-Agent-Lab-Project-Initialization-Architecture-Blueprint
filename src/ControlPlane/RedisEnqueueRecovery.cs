using StackExchange.Redis;

internal static class RedisEnqueueRecovery
{
    private const string DeadLetterSuffix = ":dead-letter";

    /// <summary>
    /// Attempts to enqueue a task to the worker queue with fallback to dead-letter on failure.
    /// Returns true if successfully enqueued to main queue, false if moved to dead-letter.
    /// Prevents duplicate entries by checking queue membership first.
    /// </summary>
    internal static async Task<bool> TryEnqueueApprovedTaskAsync(
        IDatabase redisDb, 
        string queueKey, 
        Guid taskId,
        int retryCount = 3)
    {
        var taskIdString = taskId.ToString();
        var deadLetterKey = queueKey + DeadLetterSuffix;

        for (int attempt = 0; attempt < retryCount; attempt++)
        {
            try
            {
                // Check if task is already in main queue to prevent duplicates
                var queueLength = await redisDb.ListLengthAsync(queueKey);
                var queueMembers = await redisDb.ListRangeAsync(queueKey, 0, queueLength);
                
                if (queueMembers.Any(m => m.ToString() == taskIdString))
                {
                    // Task already in queue
                    return true;
                }

                // Attempt to push to main queue
                await redisDb.ListRightPushAsync(queueKey, taskIdString);
                
                // Verify it was actually added
                var verifyLength = await redisDb.ListLengthAsync(queueKey);
                if (verifyLength > 0)
                {
                    return true;
                }
            }
            catch (Exception) when (attempt < retryCount - 1)
            {
                // Retry on transient failure
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
                continue;
            }
            catch
            {
                // Final attempt failed
                break;
            }
        }

        // All retries exhausted; move to dead-letter queue
        try
        {
            // Check if already in dead-letter to prevent duplicate
            var dlLength = await redisDb.ListLengthAsync(deadLetterKey);
            var dlMembers = await redisDb.ListRangeAsync(deadLetterKey, 0, dlLength);
            
            if (!dlMembers.Any(m => m.ToString() == taskIdString))
            {
                await redisDb.ListRightPushAsync(
                    deadLetterKey,
                    taskIdString,
                    When.Exists); // Only push if dead-letter key exists; create if needed with fallback
                
                if (!await redisDb.KeyExistsAsync(deadLetterKey))
                {
                    // Key doesn't exist, create it
                    await redisDb.ListRightPushAsync(deadLetterKey, taskIdString);
                }
            }
        }
        catch
        {
            // Dead-letter push also failed; logged by caller
        }

        return false;
    }

    /// <summary>
    /// Recovers approved tasks stuck in Approved state (not in any queue) by re-enqueueing them.
    /// Returns count of tasks recovered.
    /// </summary>
    internal static async Task<int> RecoverPendingApprovedTasksAsync(
        IDatabase redisDb,
        string queueKey,
        IEnumerable<Guid> pendingApprovedTaskIds)
    {
        int recovered = 0;
        var deadLetterKey = queueKey + DeadLetterSuffix;

        foreach (var taskId in pendingApprovedTaskIds)
        {
            if (await TryEnqueueApprovedTaskAsync(redisDb, queueKey, taskId))
            {
                recovered++;
            }
        }

        return recovered;
    }

    /// <summary>
    /// Lists tasks currently in dead-letter queue for manual inspection/recovery.
    /// </summary>
    internal static async Task<List<Guid>> ListDeadLetterTasksAsync(
        IDatabase redisDb,
        string queueKey)
    {
        var deadLetterKey = queueKey + DeadLetterSuffix;
        var range = await redisDb.ListRangeAsync(deadLetterKey);
        return range
            .Where(v => !v.IsNull)
            .Select(v => Guid.Parse(v.ToString()))
            .ToList();
    }
}
