using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Cryptography;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class TestUserProvisioningPostgresTests
{
    [PostgresFact]
    public async Task ThreeTestUsers_AreProvisionedPersistedAndIdempotent()
    {
        await WithProvisioningTables(async (connectionString, _) =>
        {
            var configuration = ConfigurationForThreeUsers();
            var first = await HicTestUserProvisioner.ProvisionAsync(
                connectionString, configuration, "Development");

            Assert.Equal(3, first.IdentityCount);
            Assert.Equal(3, first.CredentialCount);

            var second = await HicTestUserProvisioner.ProvisionAsync(
                connectionString, configuration, "Development");
            Assert.Equal(0, second.IdentityCount);
            Assert.Equal(0, second.CredentialCount);

            for (var number = 1; number <= 3; number++)
            {
                var identity = await HicUserAuthenticator.AuthenticateAsync(
                    connectionString,
                    configuration[$"HIC_TEST_USER_{number}_EMAIL"],
                    configuration[$"HIC_TEST_USER_{number}_PASSWORD"]);
                Assert.NotNull(identity);
            }
        });
    }

    [PostgresFact]
    public async Task WeakOrDuplicateConfiguration_IsRejectedWithoutCreation()
    {
        await WithProvisioningTables(async (connectionString, connection) =>
        {
            var weak = ConfigurationForThreeUsers();
            weak["HIC_TEST_USER_3_PASSWORD"] = "weak";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HicTestUserProvisioner.ProvisionAsync(
                    connectionString, weak, "Development"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "select count(*) from user_identities"));

            var duplicate = ConfigurationForThreeUsers();
            duplicate["HIC_TEST_USER_3_EMAIL"] = duplicate["HIC_TEST_USER_1_EMAIL"];
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HicTestUserProvisioner.ProvisionAsync(
                    connectionString, duplicate, "Development"));
        });
    }

    [PostgresFact]
    public async Task ProductionOrMissingSecret_IsRejectedFailClosed()
    {
        await WithProvisioningTables(async (connectionString, connection) =>
        {
            var missing = ConfigurationForThreeUsers();
            missing["HIC_TEST_USER_2_PASSWORD"] = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HicTestUserProvisioner.ProvisionAsync(
                    connectionString, missing, "Development"));

            var production = ConfigurationForThreeUsers();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HicTestUserProvisioner.ProvisionAsync(
                    connectionString, production, "Production"));

            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "select count(*) from user_identities"));
        });
    }

    private static IConfigurationRoot ConfigurationForThreeUsers()
    {
        var values = new Dictionary<string, string?>
        {
            [HicTestUserProvisioner.EnabledKey] = "true",
            ["HIC_TEST_USER_1_EMAIL"] = "hic-test-one@example.test",
            ["HIC_TEST_USER_1_USERNAME"] = "hic-test-one",
            ["HIC_TEST_USER_2_EMAIL"] = "hic-test-two@example.test",
            ["HIC_TEST_USER_2_USERNAME"] = "hic-test-two",
            ["HIC_TEST_USER_3_EMAIL"] = "hic-test-three@example.test",
            ["HIC_TEST_USER_3_USERNAME"] = "hic-test-three"
        };
        for (var number = 1; number <= 3; number++)
            values[$"HIC_TEST_USER_{number}_PASSWORD"] =
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Task WithProvisioningTables(Func<string, NpgsqlConnection, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (connectionString, connection, _) =>
        {
            try
            {
                await connection.ExecuteAsync(UserIdentitySchema.MigrationSql);
                await connection.ExecuteAsync(LocalCredentialSchema.MigrationSql);
                await test(connectionString, connection);
            }
            finally
            {
                await connection.ExecuteAsync("drop table if exists local_password_credentials restrict;");
                await connection.ExecuteAsync("drop table if exists user_identities restrict;");
            }
        });
}
