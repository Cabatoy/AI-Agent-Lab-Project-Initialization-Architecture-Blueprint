using System.Text.RegularExpressions;

namespace Hic.AgentBridge;

public static class Safety
{
    private static readonly Regex Secrets = new(
        @"(?i)(password|passwd|secret|api[_-]?key|access[_-]?token|bearer\s|connectionstring|private[ _-]?key|BEGIN.{0,30}KEY|gh[pousr]_|github_pat_|sk-[a-z0-9]{12}|AKIA[A-Z0-9]{16}|Host\s*=|Server\s*=|Data Source\s*=|https?://|-----BEGIN|raw[_-]?prompt|transcript|stack[ _-]?trace)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".cs", ".csproj", ".sln", ".json", ".ps1", ".sh", ".sql", ".yml", ".yaml", ".txt", ".trx" };
    private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "handoffs", "secrets", "credentials", "logs", "prompts", "node_modules" };

    public static void CheckString(string value)
    {
        if (value.Length > 512 || value.Any(char.IsControl) || Secrets.IsMatch(value))
            throw new BridgeException("UNSAFE_STRING");
    }

    public static void Project(string value)
    {
        CheckString(value);
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
            || value.Length > 48 || IsDevice(value))
            throw new BridgeException("INVALID_PROJECT");
    }

    public static void Sha(string value, int length = 40)
    {
        if (value.Length != length || value.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new BridgeException("INVALID_SHA");
    }

    public static void Repository(string value)
    {
        CheckString(value);
        if (value.Length > 160 || !Regex.IsMatch(value,
            @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$",
            RegexOptions.CultureInvariant) || value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            || value.Split('/').Any(s => s.Contains("..") || s.EndsWith('.')))
            throw new BridgeException("INVALID_REPOSITORY");
    }

    public static void PublicationBranch(string value)
    {
        CheckString(value);
        if (value.Length > 128 || !Regex.IsMatch(value, @"^handoffs/[a-z][a-z0-9-]{0,47}$",
            RegexOptions.CultureInvariant))
            throw new BridgeException("PUBLICATION_BRANCH_REQUIRED");
    }

    public static void Reference(string value)
    {
        CheckString(value);
        var segments = value.Split('/');
        if (value.Length > 240 || segments.Length > 16 || segments.Length < 2
            || !Regex.IsMatch(value, "^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)
            || segments.Any(s => s.Length == 0 || s.StartsWith('.') || s.EndsWith('.')
                || DeniedSegments.Contains(s) || IsDevice(s))
            || !Extensions.Contains(Path.GetExtension(value)))
            throw new BridgeException("INVALID_REFERENCE");
    }

    private static bool IsDevice(string segment) =>
        Regex.IsMatch(segment.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void Utc(DateTimeOffset value, DateTimeOffset now)
    {
        if (value.Offset != TimeSpan.Zero || value.Year < 2000 || value > now)
            throw new BridgeException("INVALID_TIMESTAMP");
    }
}
