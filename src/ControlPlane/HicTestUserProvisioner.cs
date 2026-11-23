using Microsoft.Extensions.Configuration;

internal sealed record HicTestUser(string Email, string Username, string Password);

internal sealed record HicTestProvisionResult(int IdentityCount, int CredentialCount);

internal static class HicTestUserProvisioner
{
    internal const string EnabledKey = "HIC_TEST_USER_PROVISIONING_ENABLED";

    internal static IReadOnlyList<HicTestUser> Parse(IConfiguration configuration)
    {
        if (!IsExplicitlyEnabled(configuration[EnabledKey]))
            return [];

        var users = new List<HicTestUser>(5);
        for (var number = 1; number <= 5; number++)
        {
            var prefix = $"HIC_TEST_USER_{number}";
            var email = configuration[$"{prefix}_EMAIL"];
            var username = configuration[$"{prefix}_USERNAME"];
            var password = configuration[$"{prefix}_PASSWORD"];
            if (string.IsNullOrWhiteSpace(email) &&
                string.IsNullOrWhiteSpace(username) &&
                string.IsNullOrWhiteSpace(password))
                break;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrEmpty(password))
                throw new InvalidOperationException(
                    $"Incomplete test-user configuration at slot {number}: email, username and password are required.");

            var normalizedEmail = UserIdentityStore.NormalizeEmail(email);
            var normalizedUsername = UserIdentityStore.NormalizeUsername(username);
            if (normalizedEmail is null || normalizedUsername is null ||
                email.Length > 320 || username.Length > 64 ||
                !UserIdentityStore.IsValidEmail(normalizedEmail))
                throw new InvalidOperationException(
                    $"Invalid test-user identifiers in slot {number}.");

            if (password.Length < 12 || password.Length > 1024 ||
                password.All(char.IsWhiteSpace))
                throw new InvalidOperationException(
                    $"Unsafe test-user password in slot {number}; use at least 12 characters.");

            users.Add(new HicTestUser(normalizedEmail, normalizedUsername, password));
        }

        if (users.Count is < 3 or > 5)
            throw new InvalidOperationException(
                "Development test-user provisioning requires 3 to 5 configured accounts.");

        if (users.Select(user => user.Email).Distinct(StringComparer.Ordinal).Count() != users.Count ||
            users.Select(user => user.Username).Distinct(StringComparer.Ordinal).Count() != users.Count)
            throw new InvalidOperationException(
                "Development test-user identifiers must be unique.");

        return users;
    }

    internal static async Task<HicTestProvisionResult> ProvisionAsync(string connectionString,
        IConfiguration configuration, string environmentName,
        CancellationToken cancellationToken = default)
    {
        if (!IsExplicitlyEnabled(configuration[EnabledKey]))
            return new HicTestProvisionResult(0, 0);

        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Test-user provisioning is forbidden outside the Development environment.");

        var users = Parse(configuration);
        var identityCount = 0;
        var credentialCount = 0;

        foreach (var user in users)
        {
            var existingByEmail = await UserIdentityStore.FindByEmailAsync(
                connectionString, user.Email, cancellationToken);
            var existingByUsername = await UserIdentityStore.FindByUsernameAsync(
                connectionString, user.Username, cancellationToken);
            if ((existingByEmail is not null && existingByEmail.NormalizedUsername != user.Username) ||
                (existingByUsername is not null && existingByUsername.NormalizedEmail != user.Email))
                throw new InvalidOperationException(
                    "A configured test-user identifier conflicts with an existing identity.");
        }

        foreach (var user in users)
        {
            var existingByEmail = await UserIdentityStore.FindByEmailAsync(
                connectionString, user.Email, cancellationToken);
            var existingByUsername = await UserIdentityStore.FindByUsernameAsync(
                connectionString, user.Username, cancellationToken);
            if ((existingByEmail is not null && existingByEmail.NormalizedUsername != user.Username) ||
                (existingByUsername is not null && existingByUsername.NormalizedEmail != user.Email))
                throw new InvalidOperationException(
                    "A configured test-user identifier conflicts with an existing identity.");

            var identity = existingByEmail ?? existingByUsername;
            if (identity is null)
            {
                var create = await UserIdentityStore.CreateAsync(
                    connectionString, user.Email, user.Username, cancellationToken);
                if (create.Outcome != UserIdentityCreateOutcome.Created)
                    throw new InvalidOperationException(
                        "Test-user provisioning could not create a unique identity.");
                identity = create.Identity!;
                identityCount++;
            }

            if (!await LocalCredentialStore.SetInitialPasswordAsync(
                    connectionString, identity.Id, user.Password, cancellationToken))
            {
                if (!await LocalCredentialStore.VerifyAsync(
                        connectionString, identity.Id, user.Password, cancellationToken))
                    throw new InvalidOperationException(
                        "Test-user credential does not match an existing identity.");
            }
            else
            {
                credentialCount++;
            }
        }

        return new HicTestProvisionResult(identityCount, credentialCount);
    }

    private static bool IsExplicitlyEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
