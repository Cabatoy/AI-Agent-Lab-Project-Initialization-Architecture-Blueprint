using System.Text;

namespace Hic.AgentBridge;

public sealed class PublicationWorkspace(string directory)
{
    public string Root { get; } = Path.GetFullPath(directory);
    public string ManifestPath => Path.Combine(Root, "manifest.json");
    public string JournalPath => Path.Combine(Root, "journal.json");

    public static void NoLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("PUBLICATION_LINK_NOT_ALLOWED");
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new BridgeException("PUBLICATION_LINK_NOT_ALLOWED");
    }

    public void Create(string sourceRoot, PublicationGit git)
    {
        NoLinks(Root);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Root.Equals(source, comparison) || Root.StartsWith(source + Path.DirectorySeparatorChar, comparison)
            || source.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, comparison)
            || Directory.Exists(Root) || File.Exists(Root))
            throw new BridgeException("FRESH_SEPARATE_PUBLICATION_WORKSPACE_REQUIRED");
        Directory.CreateDirectory(Root);
        git.Run(["init", "--quiet", "--initial-branch=publication-staging"]);
        File.WriteAllText(Path.Combine(Root, ".git", "hic-publication"), "hic.publication/1.0", Encoding.ASCII);
    }

    public void Check(PublicationGit git)
    {
        NoLinks(Root);
        NoLinks(Path.Combine(Root, ".git"));
        var marker = Path.Combine(Root, ".git", "hic-publication");
        NoLinks(marker);
        if (!Directory.Exists(Path.Combine(Root, ".git")) || !File.Exists(marker)
            || File.ReadAllText(marker) != "hic.publication/1.0")
            throw new BridgeException("PUBLICATION_WORKSPACE_MARKER_REQUIRED");
        NoLinks(Path.Combine(Root, ".git", "config"));
        NoLinks(Path.Combine(Root, ".git", "index"));
        NoLinks(Path.Combine(Root, ".git", "objects"));
        var configKeys = git.Text("config", "--local", "--no-includes", "--name-only", "--list")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (configKeys.Any(k => k is not ("core.repositoryformatversion" or "core.filemode"
            or "core.bare" or "core.logallrefupdates" or "core.ignorecase" or "core.symlinks")))
            throw new BridgeException("UNEXPECTED_PUBLICATION_GIT_CONFIG");
        if (File.Exists(Path.Combine(Root, ".git", "objects", "info", "alternates"))
            || File.Exists(Path.Combine(Root, ".git", "info", "grafts")))
            throw new BridgeException("UNEXPECTED_PUBLICATION_GIT_OBJECT_SOURCE");
        NoLinks(ManifestPath);
        NoLinks(JournalPath);
    }

    public void Save<T>(string path, T value, bool createNew = false)
    {
        NoLinks(path);
        var bytes = CanonicalJson.Serialize(value);
        if (createNew)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(bytes);
            file.Flush(true);
            return;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public T Read<T>(string path)
    {
        NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > CanonicalJson.MaxBytes)
            throw new BridgeException("PUBLICATION_STATE_SIZE");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return CanonicalJson.Parse<T>(bytes);
    }
}
