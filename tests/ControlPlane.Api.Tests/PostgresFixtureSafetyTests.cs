namespace ControlPlane.Api.Tests;

public class PostgresFixtureSafetyTests
{
    [Fact]
    public void DedicatedTarget_IsAccepted()
    {
        var cs = PostgresFixtureSafety.Configuration(
            "Host=localhost;Database=hic_r1_tests;Username=hic_r1_test_runner");
        Assert.False(new Npgsql.NpgsqlConnectionStringBuilder(cs).Pooling);
        PostgresFixtureSafety.Target(ValidTarget());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("malformed")]
    [InlineData("Host=localhost;Database=hic_test;Username=hic_r1_test_runner")]
    [InlineData("Host=localhost;Database=agentlab;Username=agentlab")]
    [InlineData("Host=localhost;Database=hic_r1_tests;Username=postgres")]
    [InlineData("Database=hic_r1_tests;Username=hic_r1_test_runner")]
    [InlineData("Host=localhost;Database=hic_r1_tests;Username=hic_r1_test_runner;Multiplexing=true")]
    public void InvalidConfiguration_IsRejected(string? configuration)
    {
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureSafety.Configuration(configuration));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("owner")]
    [InlineData("role")]
    [InlineData("marker")]
    [InlineData("superuser")]
    [InlineData("create-role")]
    [InlineData("create-db")]
    [InlineData("null")]
    public void InvalidServerIdentity_IsRejected(string field)
    {
        var valid = ValidTarget();
        var actual = field switch
        {
            "database" => valid with { DatabaseName = "hic_test" },
            "owner" => valid with { Owner = "other" },
            "role" => valid with { CurrentRole = "other" },
            "marker" => valid with { Marker = null },
            "superuser" => valid with { Superuser = true },
            "create-role" => valid with { CreateRole = true },
            "create-db" => valid with { CreateDatabase = true },
            _ => null
        };
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureSafety.Target(actual));
    }

    [Fact]
    public void ExactFixtureIdentity_AllowsCleanup()
    {
        var identity = ValidFixture();
        PostgresFixtureSafety.Cleanup(identity, identity);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("owner")]
    [InlineData("marker")]
    [InlineData("schema-oid")]
    [InlineData("tasks-oid")]
    [InlineData("audit-oid")]
    [InlineData("execution-oid")]
    [InlineData("version-oid")]
    [InlineData("missing")]
    public void CleanupMismatch_IsRejected(string field)
    {
        var expected = ValidFixture();
        var actual = field switch
        {
            "schema" => expected with { Schema = "public" },
            "owner" => expected with { Owner = "other" },
            "marker" => expected with { Marker = "other-run" },
            "schema-oid" => expected with { SchemaOid = 4 },
            "tasks-oid" => expected with { TasksOid = 5 },
            "audit-oid" => expected with { AuditOid = 6 },
            "execution-oid" => expected with { ExecutionOid = 7 },
            "version-oid" => expected with { VersionOid = 8 },
            _ => null
        };
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureSafety.Cleanup(expected, actual));
    }

    [Fact]
    public void InvalidExpectedCleanupScope_IsRejectedEvenWhenItMatches()
    {
        var invalid = ValidFixture() with { Schema = "public" };
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureSafety.Cleanup(invalid, invalid));
        invalid = ValidFixture() with { Marker = "" };
        Assert.Throws<InvalidOperationException>(() => PostgresFixtureSafety.Cleanup(invalid, invalid));
    }

    private static TargetIdentity ValidTarget() =>
        new(PostgresFixtureSafety.Database, PostgresFixtureSafety.Role, PostgresFixtureSafety.Role,
            PostgresFixtureSafety.DatabaseMarker, false, false, false);

    private static FixtureIdentity ValidFixture() =>
        new("hic_r1_" + new string('a', 32), PostgresFixtureSafety.Role,
            "HIC:R1:fixture:" + new string('b', 32), 1, 2, 3, 4, 5, 6);
}
