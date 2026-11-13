using Dapper;
using Npgsql;

internal static class TaskEnqueueOutboxSchema
{
    internal const int Version = 5;

    internal static string MigrationSql
    {
        get
        {
            using var stream = typeof(TaskEnqueueOutboxSchema).Assembly.GetManifestResourceStream("TaskEnqueueOutboxMigration")
                ?? throw new InvalidOperationException("Task enqueue outbox migration resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    internal static async Task VerifyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        var exists = await connection.ExecuteScalarAsync<bool>(
            "select to_regclass('app_schema_versions') is not null and to_regclass('task_enqueue_outbox') is not null;");
        if (!exists || !await connection.ExecuteScalarAsync<bool>(
                "select exists(select 1 from app_schema_versions where version = @Version);",
                new { Version }))
            throw new InvalidOperationException("Task enqueue outbox v5 requires an explicitly approved migration.");
    }
}
