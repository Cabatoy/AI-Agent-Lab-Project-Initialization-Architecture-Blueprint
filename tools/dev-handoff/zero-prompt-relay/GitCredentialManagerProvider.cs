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

    public GitCredentialManagerProvider(IOutputRedactor redactor, string gitHost = "github.com")
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _gitHost = gitHost;
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
                FileName = "git",
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
                    FileName = "git",
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

        // Redact PAT-like tokens (40+ hex chars, token= prefix)
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"(?:token[=:]*\s*)?[a-zA-Z0-9_-]{40,}",
            "***REDACTED_TOKEN***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Redact Authorization headers
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"[Aa]uthorization[=:]*\s*[^\s]+",
            "Authorization: ***REDACTED***");

        // Redact bearer tokens
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"[Bb]earer\s+[^\s]+",
            "Bearer ***REDACTED***");

        return redacted;
    }
}
