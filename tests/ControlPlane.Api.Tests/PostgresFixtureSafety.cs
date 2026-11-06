using Npgsql;

namespace ControlPlane.Api.Tests;

internal static class PostgresFixtureSafety
{
    internal const string Database = "hic_r1_tests";
    internal const string Role = "hic_r1_test_runner";
    internal const string DatabaseMarker = "HIC:R1:dedicated-integration:v1";

    internal static string Configuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Dedicated R1 PostgreSQL configuration required.");
        NpgsqlConnectionStringBuilder builder;
        try { builder = new(value); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid R1 PostgreSQL configuration; details withheld."); }
        if (builder.Database != Database || builder.Username != Role ||
            string.IsNullOrWhiteSpace(builder.Host) || builder.Multiplexing)
            throw new InvalidOperationException("Only the dedicated R1 database and test role are permitted.");
        builder.SearchPath = "pg_catalog";
        builder.Pooling = false;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        return builder.ConnectionString;
    }

    internal static void Target(TargetIdentity? identity)
    {
        if (identity is null || identity.DatabaseName != Database ||
            identity.Owner != Role || identity.CurrentRole != Role ||
            identity.Marker != DatabaseMarker || identity.Superuser || identity.CreateRole || identity.CreateDatabase)
            throw new InvalidOperationException("Dedicated R1 database ownership could not be verified.");
    }

    internal static void Cleanup(FixtureIdentity expected, FixtureIdentity? actual)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(expected.Schema, "^hic_r1_[a-f0-9]{32}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(expected.Marker, "^HIC:R1:fixture:[a-f0-9]{32}$") ||
            expected.Owner != Role || expected.SchemaOid == 0 || expected.TasksOid == 0 ||
            expected.AuditOid == 0 || expected.ExecutionOid == 0 || expected.VersionOid == 0 || actual != expected)
            throw new InvalidOperationException("R1 fixture cleanup ownership mismatch; resources preserved.");
    }
}

internal sealed record TargetIdentity(string DatabaseName, string Owner, string CurrentRole,
    string? Marker, bool Superuser, bool CreateRole, bool CreateDatabase);

internal sealed record FixtureIdentity(string Schema, string Owner, string Marker,
    long SchemaOid, long TasksOid, long AuditOid, long ExecutionOid, long VersionOid);
