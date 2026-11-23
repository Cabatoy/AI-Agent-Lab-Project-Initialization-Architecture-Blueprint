using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class PasswordResetPostgresTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void MissingOrInvalidKeyFailsClosed()
    {
        foreach (var key in new string?[] { null, "", "invalid!", Convert.ToBase64String(new byte[32]),
                     Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)),
                     Convert.ToBase64String(RandomNumberGenerator.GetBytes(65)) })
            Assert.Throws<ArgumentException>(() => new PasswordResetStore("", key));
    }

    [PostgresFact]
    public async Task ResetConsumesTokenRevokesSessionsAndPreservesOtherUser()
    {
        await WithTables(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            var other = await CreateUser(cs);
            var oldPassword = Guid.NewGuid().ToString();
            var newPassword = Guid.NewGuid().ToString();
            var otherPassword = Guid.NewGuid().ToString();
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, oldPassword));
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, other, otherPassword));
            var sessions = new HicUserSessionStore(cs);
            var userSession = Guid.NewGuid();
            var secondSession = Guid.NewGuid();
            var otherSession = Guid.NewGuid();
            foreach (var id in new[] { userSession, secondSession })
                await sessions.CreateAsync(id, user, DateTime.UtcNow.AddMinutes(10));
            await sessions.CreateAsync(otherSession, other, DateTime.UtcNow.AddMinutes(10));
            var store = new PasswordResetStore(cs, NewKey());
            var token = Assert.IsType<string>(await store.IssueAsync(user));
            var anotherToken = Assert.IsType<string>(await store.IssueAsync(user));
            var otherToken = Assert.IsType<string>(await store.IssueAsync(other));
            var verifiers = await db.QueryAsync<byte[]>("select verifier from hic_password_reset_tokens");
            Assert.All(verifiers, value => Assert.NotEqual(Convert.FromBase64String(token), value));

            Assert.False(await store.ResetPasswordAsync(other, token, newPassword));
            Assert.False(await store.ResetPasswordAsync(user, token, ""));
            Assert.False(await store.ResetPasswordAsync(user, token, new string('x', 1025)));
            Assert.True(await store.ResetPasswordAsync(user, token, newPassword));
            Assert.False(await store.ResetPasswordAsync(user, token, oldPassword));
            Assert.False(await store.ResetPasswordAsync(user, anotherToken, oldPassword));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, user, oldPassword));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, newPassword));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, other, otherPassword));
            Assert.False(await sessions.IsActiveAsync(userSession, user));
            Assert.False(await sessions.IsActiveAsync(secondSession, user));
            Assert.True(await sessions.IsActiveAsync(otherSession, other));
            Assert.True(await store.ResetPasswordAsync(other, otherToken, newPassword));
        });
    }

    [PostgresFact]
    public async Task TokenOnlyResetResolvesOwnerAndRejectsReplayAndCrossUserMutation()
    {
        await WithTables(async (cs, _) =>
        {
            var owner = await CreateUser(cs);
            var other = await CreateUser(cs);
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, owner, "owner-old"));
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, other, "other-old"));
            var sessions = new HicUserSessionStore(cs);
            var ownerSession = Guid.NewGuid();
            var otherSession = Guid.NewGuid();
            await sessions.CreateAsync(ownerSession, owner, DateTime.UtcNow.AddMinutes(10));
            await sessions.CreateAsync(otherSession, other, DateTime.UtcNow.AddMinutes(10));
            var store = new PasswordResetStore(cs, NewKey());
            var token = Assert.IsType<string>(await store.IssueAsync(owner));
            Assert.False(await store.ResetPasswordByTokenAsync("invalid", "owner-new"));
            Assert.False(await store.ResetPasswordByTokenAsync(token, ""));
            Assert.True(await store.ResetPasswordByTokenAsync(token, "owner-new"));
            Assert.False(await store.ResetPasswordByTokenAsync(token, "replay"));
            Assert.False(await LocalCredentialStore.VerifyAsync(cs, owner, "owner-old"));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, owner, "owner-new"));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, other, "other-old"));
            Assert.False(await sessions.IsActiveAsync(ownerSession, owner));
            Assert.True(await sessions.IsActiveAsync(otherSession, other));
        });
    }

    [PostgresFact]
    public async Task ExpiredTamperedCancelledAndWrongKeyTokensFail()
    {
        await WithTables(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            var federated = await CreateUser(cs);
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, "original"));
            var store = new PasswordResetStore(cs, NewKey());
            Assert.Null(await store.IssueAsync(federated));
            Assert.Null(await store.IssueAsync(Guid.NewGuid()));
            var token = Assert.IsType<string>(await store.IssueAsync(user));
            var bytes = Convert.FromBase64String(token);
            bytes[0] ^= 1;
            Assert.False(await store.ResetPasswordAsync(user, Convert.ToBase64String(bytes), "new"));
            Assert.False(await store.ResetPasswordAsync(user, "bad", "new"));
            Assert.False(await new PasswordResetStore(cs, NewKey()).ResetPasswordAsync(user, token, "new"));
            Assert.True(await store.CancelAsync(user, token));
            Assert.False(await store.CancelAsync(user, token));
            Assert.False(await store.ResetPasswordAsync(user, token, "new"));

            var expired = Assert.IsType<string>(await store.IssueAsync(user));
            await db.ExecuteAsync(@"update hic_password_reset_tokens
                set created_at_utc = now() - interval '30 minutes', expires_at_utc = now() - interval '1 minute'
                where invalidated_at_utc is null and user_id = @user", new { user });
            Assert.False(await store.ResetPasswordAsync(user, expired, "new"));
            Assert.False(await store.CancelAsync(user, expired));
            Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, "original"));
        });
    }

    [PostgresFact]
    public async Task ConcurrentIssuanceIsBoundedAndRedemptionHasOneWinner()
    {
        await WithTables(async (cs, _) =>
        {
            var user = await CreateUser(cs);
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, "original"));
            var store = new PasswordResetStore(cs, NewKey());
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string?> Issue()
            {
                await start.Task;
                return await store.IssueAsync(user);
            }
            var issues = Enumerable.Range(0, 10).Select(_ => Issue()).ToArray();
            start.SetResult();
            var tokens = (await Task.WhenAll(issues)).Where(t => t is not null).ToArray();
            Assert.Equal(PasswordResetStore.MaxActiveTokens, tokens.Length);

            var resetStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Reset(string password)
            {
                await resetStart.Task;
                return await store.ResetPasswordAsync(user, tokens[0], password);
            }
            var resets = new[] { Reset("first"), Reset("second") };
            resetStart.SetResult();
            var results = await Task.WhenAll(resets);
            Assert.Single(results, result => result);
            Assert.Single(results, result => !result);
            Assert.Equal(results[0], await LocalCredentialStore.VerifyAsync(cs, user, "first"));
            Assert.Equal(results[1], await LocalCredentialStore.VerifyAsync(cs, user, "second"));
            Assert.False(await store.ResetPasswordAsync(user, tokens[1], "third"));
            Assert.NotNull(await store.IssueAsync(user));
        });
    }

    [PostgresFact]
    public async Task FailureRevokingSessionsRollsBackPasswordAndToken()
    {
        await WithTables(async (cs, db) =>
        {
            var user = await CreateUser(cs);
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, user, "original"));
            var sessions = new HicUserSessionStore(cs);
            await sessions.CreateAsync(Guid.NewGuid(), user, DateTime.UtcNow.AddMinutes(10));
            var store = new PasswordResetStore(cs, NewKey());
            var token = Assert.IsType<string>(await store.IssueAsync(user));
            await db.ExecuteAsync(@"
                create function reject_session_update() returns trigger language plpgsql as $$
                begin raise exception 'test failure'; end $$;
                create trigger reject_session_update before update on hic_user_sessions
                for each row execute function reject_session_update();");
            try
            {
                await Assert.ThrowsAsync<PostgresException>(() => store.ResetPasswordAsync(user, token, "new"));
                Assert.True(await LocalCredentialStore.VerifyAsync(cs, user, "original"));
            }
            finally
            {
                await db.ExecuteAsync("drop trigger reject_session_update on hic_user_sessions; drop function reject_session_update();");
            }
            Assert.True(await store.ResetPasswordAsync(user, token, "new"));
        });
    }

    private static async Task<Guid> CreateUser(string cs)
    {
        var id = Guid.NewGuid().ToString("N");
        var result = await UserIdentityStore.CreateAsync(cs, $"{id}@example.test", id);
        Assert.Equal(UserIdentityCreateOutcome.Created, result.Outcome);
        return Assert.IsType<UserIdentity>(result.Identity).Id;
    }

    private static Task WithTables(Func<string, NpgsqlConnection, Task> test) =>
        TaskTransitionPostgresTests.WithDatabase(async (cs, db, _) =>
        {
            await db.ExecuteAsync(UserIdentitySchema.MigrationSql);
            try
            {
                await db.ExecuteAsync(LocalCredentialSchema.MigrationSql);
                try
                {
                    await db.ExecuteAsync(HicUserSessionSchema.MigrationSql);
                    try
                    {
                        await db.ExecuteAsync(PasswordResetSchema.MigrationSql);
                        try { await test(cs, db); }
                        finally { await db.ExecuteAsync("drop table hic_password_reset_tokens restrict;"); }
                    }
                    finally { await db.ExecuteAsync("drop table hic_user_sessions restrict;"); }
                }
                finally { await db.ExecuteAsync("drop table local_password_credentials restrict;"); }
            }
            finally { await db.ExecuteAsync("drop table user_identities restrict;"); }
        });
}
