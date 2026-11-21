using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Npgsql;

internal static class LocalCredentialStore
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashUser = new();
    private static readonly string DummyHash = Hasher.HashPassword(HashUser,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    internal static async Task<bool> SetInitialPasswordAsync(string connectionString, Guid userId,
        string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        var hash = Hasher.HashPassword(HashUser, password);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var count = await connection.ExecuteAsync(new CommandDefinition(@"
            insert into local_password_credentials (user_id, password_hash)
            select id, @Hash from user_identities where id = @UserId
            on conflict (user_id) do nothing;",
            new { UserId = userId, Hash = hash }, cancellationToken: cancellationToken));
        return count == 1;
    }

    internal static async Task<bool> VerifyAsync(string connectionString, Guid userId,
        string? password, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var hash = await ReadHashAsync(connection, userId, cancellationToken);
        var result = VerifyHash(hash, password);
        if (result == PasswordVerificationResult.Failed)
            return false;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            return await ReplaceHashAsync(connection, userId, hash!, Hasher.HashPassword(HashUser, password!),
                cancellationToken);
        return true;
    }

    internal static async Task<bool> ReplacePasswordAsync(string connectionString, Guid userId,
        string? currentPassword, string? newPassword, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var hash = await ReadHashAsync(connection, userId, cancellationToken);
        if (VerifyHash(hash, currentPassword) == PasswordVerificationResult.Failed ||
            string.IsNullOrEmpty(newPassword))
            return false;

        return await ReplaceHashAsync(connection, userId, hash!, Hasher.HashPassword(HashUser, newPassword),
            cancellationToken);
    }

    internal static PasswordVerificationResult VerifyHash(string? hash, string? password)
    {
        var validInput = hash is not null && !string.IsNullOrEmpty(password);
        try
        {
            var result = Hasher.VerifyHashedPassword(HashUser, hash ?? DummyHash, password ?? "");
            return validInput ? result : PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            Hasher.VerifyHashedPassword(HashUser, DummyHash, password ?? "");
            return PasswordVerificationResult.Failed;
        }
    }

    private static Task<string?> ReadHashAsync(NpgsqlConnection connection, Guid userId,
        CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "select password_hash from local_password_credentials where user_id = @UserId;",
            new { UserId = userId }, cancellationToken: cancellationToken));

    private static async Task<bool> ReplaceHashAsync(NpgsqlConnection connection, Guid userId,
        string oldHash, string newHash, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(@"
            update local_password_credentials set password_hash = @NewHash, updated_at_utc = now()
            where user_id = @UserId and password_hash = @OldHash;",
            new { UserId = userId, OldHash = oldHash, NewHash = newHash }, cancellationToken: cancellationToken)) == 1;
}

internal static class LocalCredentialSchema
{
    internal static string MigrationSql
    {
        get
        {
            using var stream = typeof(LocalCredentialSchema).Assembly.GetManifestResourceStream("LocalCredentialMigration")
                ?? throw new InvalidOperationException("Local credential migration resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
