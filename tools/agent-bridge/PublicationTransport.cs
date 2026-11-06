using System.Text;

namespace Hic.AgentBridge;

public sealed class PublicationTransport : IPublicationTransport
{
    private readonly string endpoint;
    private readonly bool offline;
    private readonly PublicationTarget allowed;
    public string? CredentialHelper { get; }
    public string Fingerprint { get; }

    private PublicationTransport(PublicationTarget target, string endpoint, bool offline, string? helper)
    {
        Safety.Repository(target.Repository);
        Safety.PublicationBranch(target.Branch);
        this.allowed = target;
        this.endpoint = endpoint;
        this.offline = offline;
        CredentialHelper = helper;
        Fingerprint = CanonicalJson.Hash(Encoding.UTF8.GetBytes((offline ? "local:" : "github:") + endpoint));
    }

    public static PublicationTransport GitHub(PublicationTarget target, string credentialHelper,
        bool approveNetwork)
    {
        if (!approveNetwork)
            throw new BridgeException("EXPLICIT_GITHUB_NETWORK_APPROVAL_REQUIRED");
        if (credentialHelper is not ("manager" or "manager-core" or "osxkeychain" or "libsecret"))
            throw new BridgeException("CREDENTIAL_HELPER_NOT_ALLOWED");
        return new(target, $"https://github.com/{target.Repository}.git", false, credentialHelper);
    }

    public static PublicationTransport Local(PublicationTarget target, string bareRepository)
    {
        var full = Path.GetFullPath(bareRepository);
        if (!Directory.Exists(full))
            throw new BridgeException("LOCAL_BARE_REMOTE_REQUIRED");
        PublicationWorkspace.NoLinks(full);
        var git = new PublicationGit(full);
        if (git.Text("rev-parse", "--is-bare-repository") != "true"
            || git.Text("config", "--local", "--no-includes", "--get", "hic.bridge.repository") != target.Repository)
            throw new BridgeException("LOCAL_REMOTE_IDENTITY_MISMATCH");
        return new(target, full, true, null);
    }

    public void ValidateTarget(PublicationTarget target)
    {
        Safety.Repository(target.Repository);
        Safety.PublicationBranch(target.Branch);
        if (target != allowed)
            throw new BridgeException("DESTINATION_NOT_APPROVED");
        if (offline)
        {
            PublicationWorkspace.NoLinks(endpoint);
            var git = new PublicationGit(endpoint);
            if (git.Text("config", "--local", "--no-includes", "--get", "hic.bridge.repository") != target.Repository)
                throw new BridgeException("LOCAL_REMOTE_IDENTITY_MISMATCH");
        }
    }

    public string Head(PublicationGit git, PublicationTarget target)
    {
        ValidateTarget(target);
        var fields = git.Text("ls-remote", "--refs", "--exit-code", endpoint, "refs/heads/" + target.Branch).Split('\t');
        if (fields.Length != 2 || fields[1] != "refs/heads/" + target.Branch)
            throw new BridgeException("REMOTE_BRANCH_REQUIRED");
        Safety.Sha(fields[0]);
        return fields[0];
    }

    public void Fetch(PublicationGit git, PublicationTarget target)
    {
        ValidateTarget(target);
        git.Run(["fetch", "--no-tags", "--no-recurse-submodules", "--no-write-fetch-head",
            endpoint, "refs/heads/" + target.Branch + ":refs/heads/publication-base"]);
    }

    public void Push(PublicationGit git, PublicationTarget target, string commit)
    {
        ValidateTarget(target);
        Safety.Sha(commit);
        git.Run(["push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no", endpoint,
            commit + ":refs/heads/" + target.Branch]);
    }
}
