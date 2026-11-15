using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace HIC.DevHandoff.RelayAuthClient;

/// <summary>
/// Git Credential Manager integration for fetching GitHub credentials.
/// Respects local git config and GCM scopes without assuming REST token validity.
/// </summary>
public class GitCredentialManagerProvider : ICredentialProvider
{
    private readonly IOutputRedactor _redactor;
    private readonly string _gitHost;
    private readonly string _gitExecutable;

    public GitCredentialManagerProvider(IOutputRedactor redactor, string gitHost = "github.com")
        : this(redactor, gitHost, "git")
    {
    }

    public GitCredentialManagerProvider(IOutputRedactor redactor, string gitHost, string gitExecutable)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _gitHost = gitHost;
        _gitExecutable = gitExecutable;
    }

    public async Task<GitHubCredential?> GetCredentialAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if git credential-manager is available
            if (!IsGitCredentialManagerAvailable())
            {
                return null;
            }

            // Request credential from GCM
            var credential = await FetchCredentialFromGCMAsync(cancellationToken);
            if (credential == null)
            {
                return null;
            }

            // Validate scope includes GitHub API access
            if (!await ValidateCredentialAsync(credential, cancellationToken))
            {
                return null;
            }

            return credential;
        }
        catch (Exception ex)
        {
            var redacted = _redactor.RedactSensitiveData(ex.Message);
            System.Diagnostics.Debug.WriteLine($"Credential fetch failed: {redacted}");
            return null;
        }
    }

    public async Task<bool> ValidateCredentialAsync(GitHubCredential credential, System.Threading.CancellationToken cancellationToken = default)
    {
        // Validate token format (not content)
        if (string.IsNullOrWhiteSpace(credential.Token))
        {
            return false;
        }

        // Token should be non-empty and not contain obviously invalid characters
        if (credential.Token.Length < 20 || credential.Token.Contains(" "))
        {
            return false;
        }

        return true;
    }

    private bool IsGitCredentialManagerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _gitExecutable,
                Arguments = "credential-manager --version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return false;

                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task<GitHubCredential?> FetchCredentialFromGCMAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _gitExecutable,
                    Arguments = "credential fill",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null)
                        return null;

                    // Send credential query
                    var input = $"protocol=https\nhost={_gitHost}\n\n";
                    process.StandardInput.Write(input);
                    process.StandardInput.Flush();
                    process.StandardInput.Close();

                    // Parse response
                    var output = new StringBuilder();
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        output.AppendLine(line);
                    }

                    process.WaitForExit(5000);

                    if (process.ExitCode != 0)
                        return null;

                    return ParseCredentialOutput(output.ToString());
                }
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    private GitHubCredential? ParseCredentialOutput(string output)
    {
        var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var credential = new GitHubCredential();

        foreach (var line in lines)
        {
            if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
            {
                credential.Token = line.Substring("password=".Length);
            }
            else if (line.StartsWith("username=", StringComparison.OrdinalIgnoreCase))
            {
                credential.Identity = line.Substring("username=".Length);
            }
        }

        return !string.IsNullOrEmpty(credential.Token) ? credential : null;
    }
}

/// <summary>
/// Output redactor that prevents credential leakage in logs/exceptions.
/// </summary>
public class OutputRedactor : IOutputRedactor
{
    public string RedactSensitiveData(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Redact common token patterns
        var redacted = input;

        // Redact Authorization headers, including the credential after its scheme.
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"\bauthorization\s*[:=]\s*(?:(?:bearer|token)\s+)?[^\s,;]+",
            "Authorization: ***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Redact explicitly labelled hex tokens and other long token-like strings.
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"\btoken(?:\s*[:=]\s*|\s+)[a-f0-9]{32,}\b|(?:token[=:]*\s*)?[a-zA-Z0-9_-]{40,}",
            "***REDACTED_TOKEN***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Redact bearer tokens
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"[Bb]earer\s+[^\s]+",
            "Bearer ***REDACTED***");

        return redacted;
    }
}
