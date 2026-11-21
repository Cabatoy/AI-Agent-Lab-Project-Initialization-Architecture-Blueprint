using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class LocalCredentialPostgresTests
{
    [PostgresFact]
    public async Task Passwords_PersistAndAreBoundToOneIdentity()
    {
        await WithCredentialTable(async (cs, db) =>
        {
            var first = await CreateUser(cs);
            var second = await CreateUser(cs);
            var password = Guid.NewGuid().ToString();
            var otherPassword = Guid.NewGuid().ToString();
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, first, password));
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, second, otherPassword));

            var hash = await db.QuerySingleAsync<string>(
                "select password_hash from local_password_credentials where user_id = @first", new { first });
            Assert.NotEqual(password, hash);
            Assert.DoesNotContain(password, hash);
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, first, password));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, second, otherPassword));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, second, password));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, first, otherPassword));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, Guid.NewGuid(), password));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, first, null));
        });
    }

    [PostgresFact]
    public async Task InitialPassword_RejectsMissingIdentityAndConcurrentDuplicates()
    {
        await WithCredentialTable(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            var missing = Guid.NewGuid();
            Assert.False(await LocalCredentialStore.SetInitialPasswordAsync(cs, missing, Guid.NewGuid().ToString()));
            Assert.False(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, ""));
            await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
                "insert into local_password_credentials (user_id, password_hash) values (@missing, 'invalid')",
                new { missing }));

            var passwords = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Create(string password)
            {
                await start.Task;
                return await LocalCredentialStore.SetInitialPasswordAsync(cs, user, password);
            }
            var tasks = passwords.Select(Create).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(tasks);
            Assert.Single(results, result => result);
            Assert.Single(results, result => !result);
            Assert.Equal(1, await db.QuerySingleAsync<int>("select count(*)::int from local_password_credentials"));
            for (var i = 0; i < passwords.Length; i++)
                Assert.Equal(results[i], await LocalCredentialStore.VerifyAsync(cs, user, passwords[i]));
        });
    }

    [PostgresFact]
    public async Task Replacement_RequiresCurrentPasswordAndCannotCreateCredential()
    {
        await WithCredentialTable(async (cs, _) =>
        {
            var user = await CreateUser(cs);
            var withoutCredential = await CreateUser(cs);
            var oldPassword = Guid.NewGuid().ToString();
            var newPassword = Guid.NewGuid().ToString();
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, oldPassword));
            Assert.False(await LocalCredentialStore.ReplacePasswordAsync(cs, user, newPassword, newPassword));
            Assert.False(await LocalCredentialStore.ReplacePasswordAsync(cs, withoutCredential, oldPassword, newPassword));
            Assert.False(await LocalCredentialStore.ReplacePasswordAsync(cs, user, oldPassword, ""));
            Assert.True(await LocalCredentialStore.ReplacePasswordAsync(cs, user, oldPassword, newPassword));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user, oldPassword));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, newPassword));
        });
    }

    [PostgresFact]
    public async Task ConcurrentReplacements_OnlyOneSucceeds()
    {
        await WithCredentialTable(async (cs, _) =>
        {
            var user = await CreateUser(cs);
            var oldPassword = Guid.NewGuid().ToString();
            var replacements = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, oldPassword));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Replace(string password)
            {
                await start.Task;
                return await LocalCredentialStore.ReplacePasswordAsync(cs, user, oldPassword, password);
            }
            var tasks = replacements.Select(Replace).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(tasks);
            Assert.Single(results, result => result);
            Assert.Single(results, result => !result);
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user, oldPassword));
            for (var i = 0; i < replacements.Length; i++)
                Assert.Equal(results[i], await LocalCredentialStore.VerifyAsync(cs, user, replacements[i]));
        });
    }

    [PostgresFact]
    public async Task ObsoleteHash_IsRehashedOnlyAfterSuccessfulVerification()
    {
        await WithCredentialTable(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            var password = Guid.NewGuid().ToString();
            var legacyHasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
                { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
            var legacyHash = legacyHasher.HashPassword(new object(), password);
            await db.ExecuteAsync("insert into local_password_credentials (user_id, password_hash) values (@user, @legacyHash)",
                new { user, legacyHash });
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user, Guid.NewGuid().ToString()));
            Assert.Equal(legacyHash, await ReadHash(db, user));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, password));
            Assert.NotEqual(legacyHash, await ReadHash(db, user));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, password));
        });
    }

    [PostgresFact]
    public async Task ConcurrentVerificationOfObsoleteHash_SucceedsForBoth()
    {
        await WithCredentialTable(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            var password = Guid.NewGuid().ToString();
            var legacyHasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
                { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
            var legacyHash = legacyHasher.HashPassword(new object(), password);
            await db.ExecuteAsync("insert into local_password_credentials (user_id, password_hash) values (@user, @legacyHash)",
                new { user, legacyHash });
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Verify()
            {
                await start.Task;
                return await LocalCredentialStore.VerifyAsync(cs, user, password);
            }
            var first = Verify();
            var second = Verify();
            start.SetResult();
            Assert.All(await Task.WhenAll(first, second), Assert.True);
            Assert.NotEqual(legacyHash, await ReadHash(db, user));
        });
    }

    [PostgresFact]
    public async Task InvalidStoredHash_FailsClosedAndDeletingIdentityDeletesCredential()
    {
        await WithCredentialTable(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            await db.ExecuteAsync("insert into local_password_credentials (user_id, password_hash) values (@user, 'invalid')",
                new { user });
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user, Guid.NewGuid().ToString()));
            await db.ExecuteAsync("delete from user_identities where id = @user", new { user });
            Assert.Equal(0, await db.QuerySingleAsync<int>("select count(*)::int from local_password_credentials"));
        });
    }

    private static async Task<Guid> CreateUser(string cs)
    {
        var id = Guid.NewGuid().ToString("N");
        var result = await UserIdentityStore.CreateAsync(cs, $"{id}@example.test", id);
        Assert.Equal(UserIdentityCreateOutcome.Created, result.Outcome);
        return Assert.IsType<UserIdentity>(result.Identity).Id;
    }

    private static Task<string> ReadHash(NpgsqlConnection db, Guid user) =>
        db.QuerySingleAsync<string>("select password_hash from local_password_credentials where user_id = @user", new { user });

    private static Task WithCredentialTable(Func<string, NpgsqlConnection, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (cs, db, _) =>
        {
            await db.ExecuteAsync(UserIdentitySchema.MigrationSql);
            try
            {
                await db.ExecuteAsync(LocalCredentialSchema.MigrationSql);
                try
                {
                    await test(cs, db);
                }
                finally
                {
                    await db.ExecuteAsync("drop table local_password_credentials restrict;");
                }
            }
            finally
            {
                await db.ExecuteAsync("drop table user_identities restrict;");
            }
        });
}
