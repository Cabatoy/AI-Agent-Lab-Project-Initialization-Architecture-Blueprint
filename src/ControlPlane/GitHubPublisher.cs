using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record GitHubPublicationPlan(
    Guid PublicationId, Guid ActorId, Guid? TaskId, Guid? RunId, Guid? ConversationId,
    string Kind, GitHubDestination Destination, string? BaseCommit, string? BaseTree,
    string TaskBranch, string File, string Content, string ContentDigest, string? CodePlanDigest,
    string CredentialScopeDigest, DateTime ExpiresAtUtc)
{
    internal string Digest() => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { domain = "hic-github-publication-v1", plan = this },
            GitEditExecutor.JsonOptions)));
    internal string CommitMessage => Kind == "bootstrap"
        ? "HIC approved repository bootstrap\n\nHIC-Publication: " + Digest()
        : "HIC approved task " + TaskId + "\n\nHIC-Publication: " + Digest();
}

internal static class GitHubPublisher
{
    internal const string BootstrapFile = "src/Value.cs";
    internal const string BootstrapContent =
        "namespace HicSandbox;\npublic static class Value { public static string Text => \"HIC sandbox\"; }\n";

    internal static string ContentDigest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    internal static string BlobSha(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexStringLower(SHA1.HashData(
            Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0").Concat(bytes).ToArray()));
    }

    internal static async Task ValidateBaseAsync(GitHubApi api, GitHubPublicationPlan plan, CancellationToken ct)
    {
        GitHubAppBroker.Check(api.Destination == plan.Destination, "github-destination-changed");
        await api.VerifyDestinationAsync(ct);
        if (plan.Kind == "bootstrap")
        {
            var metadata = await api.GetAsync(api.RepoPath, ct);
            GitHubAppBroker.Check(metadata.GetProperty("default_branch").GetString() == plan.Destination.BaseBranch &&
                await api.IsEmptyAsync(ct) &&
                await api.RefAsync(plan.Destination.BaseBranch, ct) is null, "github-bootstrap-not-empty");
        }
        else
        {
            GitHubAppBroker.Check(plan.Kind == "task" && plan.TaskId is not null && plan.RunId is not null &&
                plan.TaskBranch == "hic/task-" + plan.TaskId.Value.ToString("N") &&
                plan.BaseCommit == await api.RefAsync(plan.Destination.BaseBranch, ct), "github-base-changed");
            var commit = await api.GetAsync(api.RepoPath + "/git/commits/" + plan.BaseCommit, ct);
            GitHubAppBroker.Check(GitHubApi.Sha(commit.GetProperty("tree")) == plan.BaseTree,
                "github-base-tree-changed");
        }
        GitHubAppBroker.Check(GitEditExecutor.ValidRepositoryFile(plan.File) &&
            plan.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            GitEditExecutor.ValidModelContent(plan.Content) &&
            plan.ContentDigest == ContentDigest(plan.Content), "github-content-scope-invalid");
    }

    internal static async Task<GitHubPublication> ExecuteAsync(
        GitHubApi api, GitHubPublication row, Func<GitHubPublication, string, Task> save,
        CancellationToken ct)
    {
        GitHubAppBroker.Check(row.State == "pending" && row.ApprovedAtUtc is not null &&
            row.Plan.ActorId == row.ActorId && row.Plan.Digest() == row.Digest &&
            row.Plan.ExpiresAtUtc > DateTime.UtcNow, "github-publication-not-approved");
        async Task Persist(string state)
        {
            row = row with { State = state, ErrorCode = null };
            await save(row, state);
        }
        try
        {
            var plan = row.Plan;
            await ValidateBaseAsync(api, plan, ct);
            if (plan.Kind == "bootstrap")
            {
                await Persist("ref-intent");
                var response = await api.PutAsync(api.RepoPath + "/contents/" + plan.File, new
                {
                    message = plan.CommitMessage, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Content)),
                    branch = plan.Destination.BaseBranch
                }, ct);
                row = row with { CommitSha = GitHubApi.Sha(response.GetProperty("commit")),
                    BlobSha = GitHubApi.Sha(response.GetProperty("content")) };
                GitHubAppBroker.Check(row.BlobSha == BlobSha(plan.Content), "github-bootstrap-content-mismatch");
                await Persist("pushed");
                row = await ReconcileAsync(api, row, save, ct);
                GitHubAppBroker.Check(row.State == "succeeded", "github-bootstrap-unverified");
                return row;
            }

            GitHubAppBroker.Check(await api.RefAsync(plan.TaskBranch, ct) is null, "github-task-branch-exists");
            await Persist("blob-intent");
            var blob = await api.PostAsync(api.RepoPath + "/git/blobs",
                new { content = plan.Content, encoding = "utf-8" }, ct);
            row = row with { BlobSha = GitHubApi.Sha(blob) };
            GitHubAppBroker.Check(row.BlobSha == BlobSha(plan.Content), "github-blob-mismatch");
            await Persist("tree-intent");
            var tree = await api.PostAsync(api.RepoPath + "/git/trees", new
            {
                base_tree = plan.BaseTree,
                tree = new[] { new { path = plan.File, mode = "100644", type = "blob", sha = row.BlobSha } }
            }, ct);
            row = row with { TreeSha = GitHubApi.Sha(tree) };
            await VerifyTreeAsync(api, row, ct);
            await Persist("commit-intent");
            var stamp = row.ApprovedAtUtc!.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            var commit = await api.PostAsync(api.RepoPath + "/git/commits", new
            {
                message = plan.CommitMessage, tree = row.TreeSha, parents = new[] { plan.BaseCommit },
                author = new { name = "HIC", email = "hic@users.noreply.github.com", date = stamp },
                committer = new { name = "HIC", email = "hic@users.noreply.github.com", date = stamp }
            }, ct);
            row = row with { CommitSha = GitHubApi.Sha(commit) };
            VerifyCommit(commit, row);
            await Persist("commit-created");
            await ValidateBaseAsync(api, plan, ct);
            GitHubAppBroker.Check(await api.RefAsync(plan.TaskBranch, ct) is null, "github-task-branch-exists");
            await Persist("ref-intent");
            // Git Data API creates exactly one ref. There is no ref update, force push, Git URL or credential helper.
            await api.PostAsync(api.RepoPath + "/git/refs",
                new { @ref = "refs/heads/" + plan.TaskBranch, sha = row.CommitSha }, ct);
            row = row with { RemoteSha = await api.RefAsync(plan.TaskBranch, ct) };
            GitHubAppBroker.Check(row.RemoteSha == row.CommitSha, "github-remote-sha-mismatch");
            await Persist("pushed");
            var existing = await FindPullAsync(api, row, ct);
            if (existing is not null)
            {
                row = ApplyPull(row, existing.Value);
                await Persist("succeeded");
                return row;
            }
            await ValidateBaseAsync(api, plan, ct);
            await Persist("pr-intent");
            var pull = await api.PostAsync(api.RepoPath + "/pulls", new
            {
                title = "HIC approved task " + plan.TaskId,
                body = "Human-approved HIC code edit. Tests: isolated C# compilation and exact file scope.\n\n" +
                    "Run: " + plan.RunId + "\nPublication digest: " + row.Digest +
                    "\nNo merge or deployment authorized.",
                head = plan.TaskBranch, @base = plan.Destination.BaseBranch, draft = true
            }, ct);
            row = ApplyPull(row, pull);
            GitHubAppBroker.Check(await api.RefAsync(plan.TaskBranch, ct) == row.CommitSha, "github-remote-sha-mismatch");
            await Persist("succeeded");
            return row;
        }
        catch (Exception exception) when (exception is GitHubPublishException or KeyNotFoundException or
            InvalidOperationException or JsonException)
        {
            var code = exception is GitHubPublishException known ? known.Code : "github-response-invalid";
            var beforeWrite = row.State == "pending";
            row = row with { State = beforeWrite ? "blocked" : "unknown", ErrorCode = code };
            await save(row, row.State);
            return row;
        }
    }

    internal static async Task<GitHubPublication> ReconcileAsync(
        GitHubApi api, GitHubPublication row, Func<GitHubPublication, string, Task> save, CancellationToken ct)
    {
        GitHubAppBroker.Check(row.ApprovedAtUtc is not null && api.Destination == row.Plan.Destination &&
            row.Plan.Digest() == row.Digest, "publication-reconciliation-not-authorized");
        try
        {
            await api.VerifyDestinationAsync(ct);
            if (row.Plan.Kind == "bootstrap")
            {
                var sha = await api.RefAsync(row.Plan.Destination.BaseBranch, ct);
                GitHubAppBroker.Check(sha is not null && (row.CommitSha is null || row.CommitSha == sha),
                    "github-bootstrap-outcome-unknown");
                var commit = await api.GetAsync(api.RepoPath + "/git/commits/" + sha, ct);
                GitHubAppBroker.Check(commit.GetProperty("parents").GetArrayLength() == 0 &&
                    commit.GetProperty("message").GetString() == row.Plan.CommitMessage,
                    "github-bootstrap-commit-mismatch");
                var tree = await ReadTreeAsync(api, GitHubApi.Sha(commit.GetProperty("tree")), ct);
                GitHubAppBroker.Check(tree.Count == 1 && tree.TryGetValue(row.Plan.File, out var blob) &&
                    blob == BlobSha(row.Plan.Content), "github-bootstrap-tree-mismatch");
                row = row with { CommitSha = sha, RemoteSha = sha, State = "succeeded", ErrorCode = null };
            }

            else
            {
                GitHubAppBroker.Check(row.CommitSha is not null &&
                    await api.RefAsync(row.Plan.TaskBranch, ct) == row.CommitSha, "github-ref-outcome-unknown");
                var commit = await api.GetAsync(api.RepoPath + "/git/commits/" + row.CommitSha, ct);
                VerifyCommit(commit, row);
                await VerifyTreeAsync(api, row, ct);
                row = row with { RemoteSha = row.CommitSha };
                await ValidateBaseAsync(api, row.Plan, ct);
                var pull = await FindPullAsync(api, row, ct);
                row = pull is null
                    ? row with { State = "pushed", ErrorCode = "github-draft-pr-not-found" }
                    : ApplyPull(row, pull.Value) with { State = "succeeded", ErrorCode = null };
            }
            await save(row, "reconciled-" + row.State);
            return row;
        }
        catch (Exception exception) when (exception is GitHubPublishException or KeyNotFoundException or
            InvalidOperationException or JsonException)
        {
            row = row with { State = "unknown", ErrorCode = exception is GitHubPublishException known
                ? known.Code : "github-response-invalid" };
            await save(row, "reconciled-unknown");
            return row;
        }
    }

    internal static async Task<GitHubPublication> CompleteDraftAsync(
        GitHubApi api, GitHubPublication row, Func<GitHubPublication, string, Task> save, CancellationToken ct)
    {
        GitHubAppBroker.Check(row.State == "pushed" && row.Plan.Kind == "task" &&
            row.ApprovedAtUtc is not null && row.Plan.ExpiresAtUtc > DateTime.UtcNow,
            "github-draft-resume-not-approved");
        row = await ReconcileAsync(api, row, save, ct);
        if (row.State != "pushed") return row;
        try
        {
            await ValidateBaseAsync(api, row.Plan, ct);
            row = row with { State = "pr-intent", ErrorCode = null };
            await save(row, "pr-intent");
            var pull = await api.PostAsync(api.RepoPath + "/pulls", new
            {
                title = "HIC approved task " + row.Plan.TaskId,
                body = "Human-approved HIC edit. Run: " + row.RunId + "\nPublication digest: " + row.Digest,
                head = row.Plan.TaskBranch, @base = row.Plan.Destination.BaseBranch, draft = true
            }, ct);
            row = ApplyPull(row, pull) with { State = "succeeded", ErrorCode = null };
            await save(row, "succeeded");
            return row;
        }
        catch (Exception exception) when (exception is GitHubPublishException or KeyNotFoundException or
            InvalidOperationException or JsonException)
        {
            row = row with { State = "unknown", ErrorCode = exception is GitHubPublishException known
                ? known.Code : "github-response-invalid" };
            await save(row, "unknown");
            return row;
        }
    }

    internal static async Task<Dictionary<string, string>> ReadTreeAsync(GitHubApi api, string treeSha, CancellationToken ct)
    {
        var tree = await api.GetAsync(api.RepoPath + "/git/trees/" + treeSha + "?recursive=1", ct);
        GitHubAppBroker.Check(!tree.GetProperty("truncated").GetBoolean(), "github-tree-truncated");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in tree.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() == "tree") continue;
            GitHubAppBroker.Check(entry.GetProperty("mode").GetString() == "100644" &&
                entry.GetProperty("type").GetString() == "blob", "github-tree-unsafe-mode");
            var path = entry.GetProperty("path").GetString()!;
            GitHubAppBroker.Check(GitEditExecutor.ValidRepositoryFile(path) || path == ".hic-disposable",
                "github-tree-unsafe-path");
            GitHubAppBroker.Check(result.TryAdd(path, GitHubApi.Sha(entry)) && result.Count <= 200,
                "github-tree-scope-too-large");
        }
        return result;
    }

    private static async Task VerifyTreeAsync(GitHubApi api, GitHubPublication row, CancellationToken ct)
    {
        var original = await ReadTreeAsync(api, row.Plan.BaseTree!, ct);
        GitHubAppBroker.Check(original.ContainsKey(row.Plan.File), "github-file-not-in-base");
        var changed = await ReadTreeAsync(api, row.TreeSha!, ct);
        original[row.Plan.File] = BlobSha(row.Plan.Content);
        GitHubAppBroker.Check(original.Count == changed.Count &&
            original.All(pair => changed.TryGetValue(pair.Key, out var sha) && sha == pair.Value),
            "github-tree-scope-mismatch");
    }

    private static void VerifyCommit(JsonElement commit, GitHubPublication row) =>
        GitHubAppBroker.Check(GitHubApi.Sha(commit) == row.CommitSha &&
            GitHubApi.Sha(commit.GetProperty("tree")) == row.TreeSha &&
            commit.GetProperty("parents").GetArrayLength() == 1 &&
            GitHubApi.Sha(commit.GetProperty("parents")[0]) == row.Plan.BaseCommit &&
            commit.GetProperty("message").GetString() == row.Plan.CommitMessage, "github-commit-mismatch");

    private static async Task<JsonElement?> FindPullAsync(GitHubApi api, GitHubPublication row, CancellationToken ct)
    {
        var pulls = await api.GetAsync(api.RepoPath + "/pulls?state=all&head=" +
            Uri.EscapeDataString(row.Plan.Destination.Owner + ":" + row.Plan.TaskBranch) +
            "&base=" + Uri.EscapeDataString(row.Plan.Destination.BaseBranch) + "&per_page=100", ct);
        GitHubAppBroker.Check(pulls.GetArrayLength() <= 1, "github-duplicate-pr-conflict");
        return pulls.GetArrayLength() == 0 ? null : pulls[0];
    }

    private static GitHubPublication ApplyPull(GitHubPublication row, JsonElement pull)
    {
        var head = pull.GetProperty("head");
        var @base = pull.GetProperty("base");
        var number = pull.GetProperty("number").GetInt32();
        var url = row.Plan.Destination.Url + "/pull/" + number;
        GitHubAppBroker.Check(number > 0 && pull.GetProperty("html_url").GetString() == url &&
            pull.GetProperty("draft").GetBoolean() && pull.GetProperty("state").GetString() == "open" &&
            head.GetProperty("ref").GetString() == row.Plan.TaskBranch &&
            GitHubApi.Sha(head) == row.CommitSha &&
            head.GetProperty("repo").GetProperty("id").GetInt64() == row.RepositoryId &&
            @base.GetProperty("ref").GetString() == row.Plan.Destination.BaseBranch &&
            GitHubApi.Sha(@base) == row.Plan.BaseCommit &&
            @base.GetProperty("repo").GetProperty("id").GetInt64() == row.RepositoryId,
            "github-pr-scope-mismatch");
        return row with { PrNumber = number, PrUrl = url };
    }
}
