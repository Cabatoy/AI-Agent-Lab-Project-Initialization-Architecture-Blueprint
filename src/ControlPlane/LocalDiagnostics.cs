using Npgsql;
using StackExchange.Redis;

// A deliberately small, read-only probe. It does NOT execute a task, shell command or model.
// Report only health categories: never return connection strings, exception details or secrets.
internal sealed record LocalDiagnosticSnapshot(
    string Action,
    DateTime CheckedAtUtc,
    string ControlPlane,
    string Postgres,
    string Redis,
    string Worker);

internal static class LocalDiagnostics
{
    internal static async Task<LocalDiagnosticSnapshot> InspectAsync(
        string postgresConnectionString,
        IDatabase redisDb,
        CancellationToken cancellationToken)
    {
        var postgres = "unavailable";
        var redis = "unavailable";

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var connection = new NpgsqlConnection(postgresConnectionString);
                await connection.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("select 1", connection) { CommandTimeout = 3 };
                var value = await command.ExecuteScalarAsync(timeout.Token);
                postgres = Convert.ToInt32(value) == 1 ? "ok" : "unavailable";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Fail closed without sending DB exception text or credentials to the browser.
            }
        }

        try
        {
            await redisDb.PingAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            redis = "ok";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An unavailable or timed-out Redis connection is reported only as unavailable.
        }

        // We do not have a trustworthy Worker heartbeat/lease signal yet (R3/R4 pending).
        return new LocalDiagnosticSnapshot("system.health", DateTime.UtcNow,
            "ok", postgres, redis, "not-checked");
    }
}
