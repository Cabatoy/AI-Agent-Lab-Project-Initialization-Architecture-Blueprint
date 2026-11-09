using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record GitHubIssueReference(string Owner, string Repository, int Number)
{
    internal string Url => $"https://github.com/{Owner}/{Repository}/issues/{Number}";
}

internal sealed record GitHubIssueSource(long RepositoryId, long IssueId, GitHubIssueReference Reference,
    string Title, string Body, DateTimeOffset UpdatedAtUtc, string RevisionSha256);

internal static class GitHubIssueIntake
{
    internal const int MaxBodyBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex RepositoryName = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex OwnerName = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static GitHubIssueReference ParseReference(string? input, GitHubDestination authorized)
    {
        if (input is null || authorized is null || input.Length > 512 || input != input.Trim())
            throw new GitHubPublishException("issue-reference-invalid");
        string[] parts;
        if (input.StartsWith("https://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || input.Contains('%') || input.Contains('\\') ||
                !string.Equals(input, uri.AbsoluteUri, StringComparison.Ordinal))
                throw new GitHubPublishException("issue-reference-invalid");
            parts = uri.AbsolutePath.Split('/');
            if (parts.Length != 5 || parts[0] != "" || parts[3] != "issues")
                throw new GitHubPublishException("issue-reference-invalid");
            parts = [parts[1], parts[2], parts[4]];
        }
        else
        {
            parts = input.Split('/');
            if (parts.Length != 3 || !parts[2].StartsWith('#'))
                throw new GitHubPublishException("issue-reference-invalid");
            parts[2] = parts[2][1..];
        }
        if (!OwnerName.IsMatch(parts[0]) || !RepositoryName.IsMatch(parts[1]) ||
            parts[1].Contains("..") || parts[1].EndsWith('.') ||
            !Regex.IsMatch(parts[2], @"^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant) ||
            !int.TryParse(parts[2], out var number))
            throw new GitHubPublishException("issue-reference-invalid");
        if (!parts[0].Equals(authorized.Owner, StringComparison.OrdinalIgnoreCase) ||
            !parts[1].Equals(authorized.Name, StringComparison.OrdinalIgnoreCase) || authorized.Id <= 0)
            throw new GitHubPublishException("issue-repository-not-authorized");
        return new GitHubIssueReference(authorized.Owner, authorized.Name, number);
    }

    internal static GitHubIssueSource Capture(GitHubDestination authorized, GitHubIssueReference reference,
        long repositoryId, long issueId, string? title, string? body, DateTimeOffset updatedAtUtc)
    {
        if (authorized is null || reference is null || repositoryId <= 0 || issueId <= 0 ||
            authorized.Id != repositoryId || reference.Number <= 0 ||
            reference.Owner != authorized.Owner || reference.Repository != authorized.Name)
            throw new GitHubPublishException("issue-source-binding-mismatch");
        if (updatedAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Year < 2000)
            throw new GitHubPublishException("issue-timestamp-invalid");
        title = Normalize(title, 512, false);
        body = Normalize(body, MaxBodyBytes, true);
        if (string.IsNullOrWhiteSpace(title))
            throw new GitHubPublishException("issue-content-invalid");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "hic-github-issue-source-v1", repositoryId, issueId, reference.Number,
            owner = authorized.Owner, repository = authorized.Name, updatedAtUtc, title, body
        })));
        return new GitHubIssueSource(repositoryId, issueId, reference, title, body, updatedAtUtc, digest);
    }

    private static string Normalize(string? value, int maxBytes, bool allowNewlines)
    {
        if (value is null) throw new GitHubPublishException("issue-content-invalid");
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\n' or '\t')))
            throw new GitHubPublishException("issue-content-invalid");
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes)
                throw new GitHubPublishException("issue-content-too-large");
        }
        catch (EncoderFallbackException)
        {
            throw new GitHubPublishException("issue-content-invalid");
        }
        return value;
    }
}
