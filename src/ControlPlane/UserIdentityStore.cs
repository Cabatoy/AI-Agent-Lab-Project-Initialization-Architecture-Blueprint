using Dapper;
using Npgsql;

internal sealed record UserIdentity(Guid Id, string NormalizedEmail, string NormalizedUsername,
    DateTime CreatedAtUtc);

internal enum UserIdentityCreateOutcome
{
    Created,
    DuplicateIdentifier,
    InvalidIdentifier
}

internal sealed record UserIdentityCreateResult(UserIdentityCreateOutcome Outcome, UserIdentity? Identity = null);

internal static class UserIdentityStore
{
    internal static string? NormalizeEmail(string? email) => Normalize(email, 320);

    internal static string? NormalizeUsername(string? username) => Normalize(username, 64);

    internal static async Task<UserIdentityCreateResult> CreateAsync(string connectionString,
        string? email, string? username, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedUsername = NormalizeUsername(username);
        if (normalizedEmail is null || normalizedUsername is null ||
            normalizedEmail.IndexOf('@') <= 0 ||
            normalizedEmail.LastIndexOf('@') != normalizedEmail.IndexOf('@') ||
            normalizedEmail.LastIndexOf('@') == normalizedEmail.Length - 1 ||
            normalizedEmail.Any(char.IsWhiteSpace))
            return new(UserIdentityCreateOutcome.InvalidIdentifier);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var identity = await connection.QuerySingleOrDefaultAsync<UserIdentity>(new CommandDefinition(@"
            insert into user_identities (id, normalized_email, normalized_username)
            values (@Id, @NormalizedEmail, @NormalizedUsername)
            on conflict do nothing
            returning id, normalized_email as NormalizedEmail,
                      normalized_username as NormalizedUsername, created_at_utc as CreatedAtUtc;",
            new
            {
                Id = Guid.NewGuid(),
                NormalizedEmail = normalizedEmail,
                NormalizedUsername = normalizedUsername
            }, cancellationToken: cancellationToken));

        return identity is null
            ? new(UserIdentityCreateOutcome.DuplicateIdentifier)
            : new(UserIdentityCreateOutcome.Created, identity);
    }

    internal static Task<UserIdentity?> FindByEmailAsync(string connectionString, string? email,
        CancellationToken cancellationToken = default) =>
        FindAsync(connectionString, NormalizeEmail(email), "normalized_email", cancellationToken);

    internal static Task<UserIdentity?> FindByUsernameAsync(string connectionString, string? username,
        CancellationToken cancellationToken = default) =>
        FindAsync(connectionString, NormalizeUsername(username), "normalized_username", cancellationToken);

    private static async Task<UserIdentity?> FindAsync(string connectionString, string? identifier,
        string column, CancellationToken cancellationToken)
    {
        if (identifier is null)
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<UserIdentity>(new CommandDefinition($@"
            select id, normalized_email as NormalizedEmail,
                   normalized_username as NormalizedUsername, created_at_utc as CreatedAtUtc
            from user_identities
            where {column} = @Identifier;",
            new { Identifier = identifier }, cancellationToken: cancellationToken));
    }

    private static string? Normalize(string? identifier, int maxLength)
    {
        var normalized = identifier?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) || normalized.Length > maxLength ? null : normalized;
    }
}

internal static class UserIdentitySchema
{
    internal static string MigrationSql
    {
        get
        {
            using var stream = typeof(UserIdentitySchema).Assembly.GetManifestResourceStream("UserIdentityMigration")
                ?? throw new InvalidOperationException("User identity migration resource is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
