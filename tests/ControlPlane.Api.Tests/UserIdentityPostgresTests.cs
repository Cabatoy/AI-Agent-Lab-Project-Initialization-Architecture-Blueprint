using Dapper;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class UserIdentityPostgresTests
{
    [PostgresFact]
    public async Task DistinctIdentities_PersistAcrossFreshConnections()
    {
        await WithIdentityTable(async (connectionString, _) =>
        {
            var first = await Create(connectionString, "Alice@example.test", "Alice");
            var second = await Create(connectionString, "bob@example.test", "Bob");

            Assert.NotEqual(first.Id, second.Id);
            var foundByEmail = await UserIdentityStore.FindByEmailAsync(connectionString, " ALICE@EXAMPLE.TEST ");
            var foundByUsername = await UserIdentityStore.FindByUsernameAsync(connectionString, "BOB");

            Assert.Equal(first, foundByEmail);
            Assert.Equal(second, foundByUsername);
        });
    }

    [PostgresFact]
    public async Task DuplicateNormalizedEmail_IsRejectedWithoutLeakingEmail()
    {
        await WithIdentityTable(async (connectionString, _) =>
        {
            var first = await UserIdentityStore.CreateAsync(connectionString, "Alice@example.test", "alice");
            var duplicate = await UserIdentityStore.CreateAsync(connectionString, " ALICE@EXAMPLE.TEST ", "another");

            Assert.Equal(UserIdentityCreateOutcome.Created, first.Outcome);
            Assert.Equal(UserIdentityCreateOutcome.DuplicateIdentifier, duplicate.Outcome);
            Assert.Null(duplicate.Identity);
        });
    }

    [PostgresFact]
    public async Task DuplicateNormalizedUsername_IsRejected()
    {
        await WithIdentityTable(async (connectionString, _) =>
        {
            var first = await UserIdentityStore.CreateAsync(connectionString, "alice@example.test", "Alice");
            var duplicate = await UserIdentityStore.CreateAsync(connectionString, "different@example.test", " ALICE ");

            Assert.Equal(UserIdentityCreateOutcome.Created, first.Outcome);
            Assert.Equal(UserIdentityCreateOutcome.DuplicateIdentifier, duplicate.Outcome);
            Assert.Null(duplicate.Identity);
        });
    }

    [PostgresFact]
    public async Task UnknownIdentity_FailsClosed()
    {
        await WithIdentityTable(async (connectionString, _) =>
        {
            Assert.Null(await UserIdentityStore.FindByEmailAsync(connectionString, "unknown@example.test"));
            Assert.Null(await UserIdentityStore.FindByUsernameAsync(connectionString, "unknown"));
        });
    }

    private static async Task<UserIdentity> Create(string connectionString, string email, string username)
    {
        var result = await UserIdentityStore.CreateAsync(connectionString, email, username);
        Assert.Equal(UserIdentityCreateOutcome.Created, result.Outcome);
        return Assert.IsType<UserIdentity>(result.Identity);
    }

    private static Task WithIdentityTable(Func<string, Npgsql.NpgsqlConnection, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (connectionString, connection, _) =>
        {
            await connection.ExecuteAsync(UserIdentitySchema.MigrationSql);
            try
            {
                await test(connectionString, connection);
            }
            finally
            {
                await connection.ExecuteAsync("drop table user_identities restrict;");
            }
        });
}
