using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record WorkspaceInspection(
    string Action,
    Guid RunId,
    DateTime CheckedAtUtc,
    bool WorkspaceAvailable,
    bool GitCheckoutFound,
    bool SolutionFound);

internal static class NativeActionPolicy
{
    internal const string Action = "workspace.inspect";
    internal const string Solution = "HumanInControl.sln";

    internal static bool ValidToken(string? candidate, string expected)
    {
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var candidateDigest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate ?? ""));
        return CryptographicOperations.FixedTimeEquals(expectedDigest, candidateDigest);
    }

    // Strict allowlist: no paths, args, scripts, or other keys are accepted.
    internal static bool AllowedPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            count++;
            if (count > 1 || property.Name != "action" ||
                property.Value.ValueKind != JsonValueKind.String ||
                property.Value.GetString() != Action)
                return false;
        }
        return count == 1;
    }

    internal static WorkspaceInspection Inspect(string configuredWorkspace)
    {
        // Only the process owner's fixed, absolute configured workspace can be inspected.
        if (!Path.IsPathFullyQualified(configuredWorkspace))
            throw new InvalidOperationException("HIC_WORKSPACE_ROOT must be an absolute path.");

        var root = Path.GetFullPath(configuredWorkspace);
        var present = Directory.Exists(root);
        return new WorkspaceInspection(
            Action,
            Guid.NewGuid(),
            DateTime.UtcNow,
            present,
            present && (Directory.Exists(Path.Combine(root, ".git")) ||
                        File.Exists(Path.Combine(root, ".git"))),
            present && File.Exists(Path.Combine(root, Solution)));
    }
}
