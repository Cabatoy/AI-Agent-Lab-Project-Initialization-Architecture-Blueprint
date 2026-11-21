using System.Security.Claims;
using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class UserAuthenticationPostgresTests
{
    [PostgresFact]
    public async Task ThreeSyntheticAccounts_AuthenticateIndependentlyAndRejectUnknownOrWrongCredentials()
    {
        await WithAuthTables(async (connectionString, _) =>
        {
            var accounts = new[]
            {
                await CreateAccount(connectionString),
                await CreateAccount(connectionString),
                await CreateAccount(connectionString)
            };

            Assert.Equal(3, accounts.Select(account => account.Identity.Id).Distinct().Count());
            foreach (var account in accounts)
            {
                Assert.Equal(account.Identity, await HicUserAuthenticator.AuthenticateAsync(
                    connectionString, account.Identity.NormalizedEmail, account.Password));
                Assert.Equal(account.Identity, await HicUserAuthenticator.AuthenticateAsync(
                    connectionString, account.Identity.NormalizedUsername.ToUpperInvariant(), account.Password));
                Assert.Null(await HicUserAuthenticator.AuthenticateAsync(
                    connectionString, account.Identity.NormalizedEmail, RandomPassword()));
            }

            Assert.Null(await HicUserAuthenticator.AuthenticateAsync(
                connectionString, $"missing-{Guid.NewGuid():N}@example.test", RandomPassword()));
            Assert.Null(await HicUserAuthenticator.AuthenticateAsync(
                connectionString, "invalid identifier", RandomPassword()));
            Assert.Null(await HicUserAuthenticator.AuthenticateAsync(connectionString, null, null));
        });
    }

    [PostgresFact]
    public async Task Sessions_AreUserBoundRotateExpireAndCannotReplayAfterRevocation()
    {
        await WithAuthTables(async (connectionString, db) =>
        {
            var first = await CreateAccount(connectionString);
            var second = await CreateAccount(connectionString);
            var sessions = new HicUserSessionStore(connectionString);
            var oldSession = Guid.NewGuid();
            var currentSession = Guid.NewGuid();
            var expiresAt = DateTime.UtcNow.Add(HicUserSessionStore.Lifetime);
            await sessions.CreateAsync(oldSession, first.Identity.Id, expiresAt);
            await sessions.CreateAsync(currentSession, first.Identity.Id, expiresAt);

            Assert.NotEqual(oldSession, currentSession);
            Assert.True(await sessions.IsActiveAsync(oldSession, first.Identity.Id));
            Assert.False(await sessions.IsActiveAsync(oldSession, second.Identity.Id));
            Assert.Equal(first.Identity, await HicUserSession.GetCurrentIdentityAsync(connectionString,
                PrincipalFor(first.Identity.Id, currentSession)));
            Assert.False(await sessions.IsActiveAsync(Guid.NewGuid(), first.Identity.Id));

            Assert.True(await sessions.RevokeAsync(oldSession));
            Assert.False(await sessions.IsActiveAsync(oldSession, first.Identity.Id));
            Assert.True(await sessions.IsActiveAsync(currentSession, first.Identity.Id));
            Assert.True(await sessions.RevokeAsync(currentSession));
            Assert.False(await sessions.IsActiveAsync(currentSession, first.Identity.Id));
            Assert.False(await sessions.RevokeAsync(currentSession));

            var expiredSession = Guid.NewGuid();
            await sessions.CreateAsync(expiredSession, first.Identity.Id, DateTime.UtcNow.AddMinutes(1));
            await db.ExecuteAsync(
                "update hic_user_sessions set expires_at_utc = now() - interval '1 second' where id = @expiredSession",
                new { expiredSession });
            Assert.False(await sessions.IsActiveAsync(expiredSession, first.Identity.Id));
        });
    }

    private static ClaimsPrincipal PrincipalFor(Guid userId, Guid sessionId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(HicUserSession.SessionClaim, sessionId.ToString())
        ], HicUserSession.Scheme));

    private static async Task<(UserIdentity Identity, string Password)> CreateAccount(string connectionString)
    {
        var identifier = Guid.NewGuid().ToString("N");
        var password = RandomPassword();
        var result = await UserIdentityStore.CreateAsync(
            connectionString, $"{identifier}@example.test", identifier);
        Assert.Equal(UserIdentityCreateOutcome.Created, result.Outcome);
        var identity = Assert.IsType<UserIdentity>(result.Identity);
        Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(connectionString, identity.Id, password));
        return (identity, password);
    }

    private static string RandomPassword() => Convert.ToHexString(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static Task WithAuthTables(Func<string, NpgsqlConnection, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (connectionString, connection, _) =>
        {
            try
            {
                await connection.ExecuteAsync(UserIdentitySchema.MigrationSql);
                await connection.ExecuteAsync(LocalCredentialSchema.MigrationSql);
                await connection.ExecuteAsync(HicUserSessionSchema.MigrationSql);
                await test(connectionString, connection);
            }
            finally
            {
                await connection.ExecuteAsync("drop table if exists hic_user_sessions restrict;");
                await connection.ExecuteAsync("drop table if exists local_password_credentials restrict;");
                await connection.ExecuteAsync("drop table if exists user_identities restrict;");
            }
        });
}
