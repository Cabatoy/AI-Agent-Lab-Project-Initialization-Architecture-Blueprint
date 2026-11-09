using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class GitHubPublishException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed record GitHubAppStatus(bool Configured, string Repository, string[] Missing,
    string AppId, string InstallationId, string RuntimeRevision);
internal sealed record GitHubDestination(long Id, string Owner, string Name, string BaseBranch)
{
    internal string FullName => Owner + "/" + Name;
    internal string Url => "https://github.com/" + FullName;
}

internal sealed class GitHubAppBroker(IConfiguration configuration,
    Func<string, GitHubDestination, GitHubApi>? testApiFactory = null)
{
    internal GitHubAppStatus Status
    {
        get
        {
            var names = new[] { "HIC_GITHUB_APP_ID", "HIC_GITHUB_INSTALLATION_ID",
                "HIC_GITHUB_PRIVATE_KEY_PATH", "HIC_GITHUB_REPOSITORY_ID",
                "HIC_GITHUB_REPOSITORY", "HIC_GITHUB_BASE_BRANCH" };
            var missing = names.Where(name => string.IsNullOrWhiteSpace(configuration[name])).ToArray();
            return new(missing.Length == 0, configuration["HIC_GITHUB_REPOSITORY"] ?? "", missing,
                configuration["HIC_GITHUB_APP_ID"] ?? "", configuration["HIC_GITHUB_INSTALLATION_ID"] ?? "",
                configuration["HIC_RUNTIME_REVISION"] ?? "");
        }
    }

    internal GitHubDestination Destination()
    {
        Check(Status.Configured, "github-app-configuration-missing");
        var fullName = configuration["HIC_GITHUB_REPOSITORY"]!;
        Check(Regex.IsMatch(fullName, @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9_.-]{1,100}$") &&
            !fullName.EndsWith(".git", StringComparison.OrdinalIgnoreCase), "github-repository-invalid");
        Check(long.TryParse(configuration["HIC_GITHUB_REPOSITORY_ID"], out var id) && id > 0,
            "github-repository-id-invalid");
        var branch = configuration["HIC_GITHUB_BASE_BRANCH"]!;
        Check(ValidBranch(branch) && !branch.StartsWith("hic/", StringComparison.Ordinal), "github-base-branch-invalid");
        return new(id, fullName.Split('/')[0], fullName.Split('/')[1], branch);
    }

    internal string ScopeDigest() => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "hic-github-app-scope-v1", appId = configuration["HIC_GITHUB_APP_ID"],
            installationId = configuration["HIC_GITHUB_INSTALLATION_ID"], destination = Destination()
        })));

    internal static bool ValidBranch(string branch) =>
        Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$") &&
        !branch.Contains("..") && !branch.Contains("//") && !branch.Contains("@{") &&
        !branch.EndsWith('/') && !branch.EndsWith('.') &&
        branch.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    internal async Task<GitHubApi> OpenAsync(CancellationToken cancellationToken)
    {
        var destination = Destination();
        Check(long.TryParse(configuration["HIC_GITHUB_APP_ID"], out var appId) && appId > 0,
            "github-app-identity-invalid");
        Check(long.TryParse(configuration["HIC_GITHUB_INSTALLATION_ID"], out var installationId) && installationId > 0,
            "github-app-identity-invalid");
        var keyPath = configuration["HIC_GITHUB_PRIVATE_KEY_PATH"]!;
        try
        {
            GitEditExecutor.CheckPath(keyPath);
            Check(new FileInfo(keyPath).Length is > 0 and <= 16384, "github-private-key-invalid");
            using var rsa = RSA.Create();
            rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath, cancellationToken));
            Check(rsa.KeySize >= 2048, "github-private-key-invalid");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var input = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}""")) + "." +
                Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = appId.ToString() }));
            var jwt = input + "." + Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(input),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var appApi = testApiFactory?.Invoke(jwt, destination) ?? new GitHubApi(jwt, destination);
            var app = await appApi.GetAsync("/app", cancellationToken);
            Check(app.GetProperty("id").GetInt64() == appId, "github-app-mismatch");
            CheckPermissions(app.GetProperty("permissions"));
            var installation = await appApi.GetAsync("/app/installations/" + installationId, cancellationToken);
            Check(installation.GetProperty("app_id").GetInt64() == appId &&
                installation.GetProperty("repository_selection").GetString() == "selected" &&
                installation.GetProperty("suspended_at").ValueKind == JsonValueKind.Null,
                "github-installation-not-authorized");
            CheckPermissions(installation.GetProperty("permissions"));
            var inventoryTokenResponse = await appApi.PostAsync("/app/installations/" + installationId + "/access_tokens",
                new { permissions = new { contents = "write", pull_requests = "write", metadata = "read" } },
                cancellationToken);
            CheckPermissions(inventoryTokenResponse.GetProperty("permissions"));
            var inventoryToken = inventoryTokenResponse.GetProperty("token").GetString();
            Check(!string.IsNullOrWhiteSpace(inventoryToken), "github-token-invalid");
            using (var inventoryApi = testApiFactory?.Invoke(inventoryToken!, destination) ??
                new GitHubApi(inventoryToken!, destination))
            {
                var inventory = await inventoryApi.GetAsync("/installation/repositories?per_page=100", cancellationToken);
                Check(inventory.GetProperty("total_count").GetInt32() == 1 &&
                    inventory.GetProperty("repositories").GetArrayLength() == 1,
                    "github-installation-repository-scope-mismatch");
                VerifyRepository(inventory.GetProperty("repositories")[0], destination);
            }
            var tokenResponse = await appApi.PostAsync("/app/installations/" + installationId + "/access_tokens",
                new { repository_ids = new[] { destination.Id },
                    permissions = new { contents = "write", pull_requests = "write", metadata = "read" } },
                cancellationToken);
            CheckPermissions(tokenResponse.GetProperty("permissions"));
            Check(DateTimeOffset.TryParse(tokenResponse.GetProperty("expires_at").GetString(), out var expiry) &&
                expiry > DateTimeOffset.UtcNow.AddMinutes(1) &&
                expiry <= DateTimeOffset.UtcNow.AddMinutes(65), "github-token-expiry-invalid");
            var token = tokenResponse.GetProperty("token").GetString();
            Check(!string.IsNullOrWhiteSpace(token), "github-token-invalid");
            var api = testApiFactory?.Invoke(token!, destination) ?? new GitHubApi(token!, destination);
            try
            {
                var repositories = await api.GetAsync("/installation/repositories?per_page=100", cancellationToken);
                Check(repositories.GetProperty("total_count").GetInt32() == 1, "github-repository-scope-mismatch");
                var repository = repositories.GetProperty("repositories")[0];
                VerifyRepository(repository, destination);
                await api.VerifyDestinationAsync(cancellationToken);
                return api;
            }
            catch
            {
                api.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or JsonException or KeyNotFoundException or
            InvalidOperationException or GitEditDeniedException)
        {
            throw new GitHubPublishException("github-app-key-or-response-invalid");
        }
    }

    internal static void VerifyRepository(JsonElement repository, GitHubDestination destination) =>
        Check(repository.GetProperty("id").GetInt64() == destination.Id &&
            repository.GetProperty("full_name").GetString() == destination.FullName &&
            repository.GetProperty("html_url").GetString() == destination.Url &&
            repository.GetProperty("archived").GetBoolean() == false &&
            repository.GetProperty("disabled").GetBoolean() == false, "github-repository-scope-mismatch");

    internal static void CheckPermissions(JsonElement permissions)
    {
        var expected = new Dictionary<string, string>
        { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" };
        Check(permissions.ValueKind == JsonValueKind.Object &&
            permissions.EnumerateObject().Count() == expected.Count &&
            permissions.EnumerateObject().All(p => expected.TryGetValue(p.Name, out var value) &&
                p.Value.GetString() == value), "github-permissions-not-minimal");
    }

    internal static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new GitHubPublishException(code);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class GitHubApi : IDisposable
{
    private readonly HttpClient client;
    internal GitHubDestination Destination { get; }
    internal string RepoPath => "/repos/" + Destination.FullName;

    internal GitHubApi(string token, GitHubDestination destination, HttpMessageHandler? testHandler = null)
    {
        Destination = destination;
        client = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri("https://api.github.com"), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HIC-GitHub-Publisher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    internal async Task VerifyDestinationAsync(CancellationToken ct) =>
        GitHubAppBroker.VerifyRepository(await GetAsync(RepoPath, ct), Destination);

    internal Task<JsonElement> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);
    internal Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, body, ct);
    internal Task<JsonElement> PutAsync(string path, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, path, body, ct);

    internal async Task<string?> RefAsync(string branch, CancellationToken ct)
    {
        try
        {
            var value = await GetAsync(RepoPath + "/git/ref/heads/" + Uri.EscapeDataString(branch), ct);
            GitHubAppBroker.Check(value.GetProperty("ref").GetString() == "refs/heads/" + branch &&
                value.GetProperty("object").GetProperty("type").GetString() == "commit", "github-ref-invalid");
            return Sha(value.GetProperty("object"));
        }
        catch (GitHubPublishException exception) when (exception.Code is "github-http-404" or "github-repository-empty")
        { return null; }
    }

    internal async Task<bool> IsEmptyAsync(CancellationToken ct)
    {
        try
        {
            var branches = await GetAsync(RepoPath + "/branches?per_page=1", ct);
            if (branches.GetArrayLength() > 0) return false;
            // An empty branches list alone is not sufficient if refs were deleted.
            var commits = await GetAsync(RepoPath + "/commits?per_page=1", ct);
            return commits.GetArrayLength() == 0;
        }
        catch (GitHubPublishException exception) when (exception.Code == "github-repository-empty") { return true; }
    }

    internal static string Sha(JsonElement value)
    {
        var sha = value.GetProperty("sha").GetString();
        GitHubAppBroker.Check(sha is not null && Regex.IsMatch(sha, "^[0-9a-f]{40}$"), "github-sha-invalid");
        return sha!;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        GitHubAppBroker.Check(path.StartsWith('/') && !path.StartsWith("//") &&
            !path.Contains('\\'), "github-api-path-invalid");
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(bytes, ct)) > 0)
            {
                GitHubAppBroker.Check(buffer.Length + count <= 1024 * 1024, "github-response-too-large");
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!response.IsSuccessStatusCode)
            {
                var empty = response.StatusCode == HttpStatusCode.Conflict &&
                    document.RootElement.TryGetProperty("message", out var message) &&
                    message.GetString() is "Git Repository is empty." or "Git Repository is empty";
                throw new GitHubPublishException(empty ? "github-repository-empty" : "github-http-" + (int)response.StatusCode);
            }
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or
            JsonException or IOException)
        {
            throw new GitHubPublishException("github-response-unavailable");
        }
    }

    public void Dispose() => client.Dispose();
}
