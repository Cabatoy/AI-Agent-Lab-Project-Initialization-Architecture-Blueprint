using Dapper;
using Npgsql;

internal static class ExecutionSchema
{
    internal const int Version = 2;

    internal static string MigrationSql
    {
        get
        {
            using var stream = typeof(ExecutionSchema).Assembly.GetManifestResourceStream("ExecutionOwnershipMigration")
                ?? throw new InvalidOperationException("Execution schema migration resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    internal static async Task VerifyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        var exists = await connection.ExecuteScalarAsync<bool>(
            "select to_regclass('app_schema_versions') is not null;");
        if (!exists || !await connection.ExecuteScalarAsync<bool>(
                "select exists(select 1 from app_schema_versions where version = @Version);", new { Version }))
            throw new InvalidOperationException("Execution schema v2 requires an explicitly approved migration before R2 startup.");
    }
}
