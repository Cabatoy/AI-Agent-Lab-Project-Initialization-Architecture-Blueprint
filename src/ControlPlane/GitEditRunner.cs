using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

internal sealed record GitEditRequest(string RepositoryId, string BaseCommit, string File, string Content);
internal sealed record GitTestRepository(string Id, string Path, string BaseCommit, string[]? AllowedFiles = null)
{
    internal IReadOnlyList<string> Files => AllowedFiles ?? ["message.txt"];
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool PublisherSource { get; init; }
}
internal interface ILocalRunPlan
{
    Guid TaskId { get; }
    Guid ActorId { get; }
    string TaskDigest { get; }
    DateTime ExpiresAtUtc { get; }
    string PlanAction { get; }
    string PlanScope { get; }
    Guid? PlanConversationId { get; }
    string Digest();
}

internal sealed record GitEditPlan(
    int Revision, Guid TaskId, Guid ActorId, string TaskDigest, string RepositoryId,
    string RepositoryPath, string BaseCommit, string File, string Content, DateTime ExpiresAtUtc) : ILocalRunPlan
{
    internal const string Action = "repository.test-edit";
    internal const string Scope = "disposable-worktree:message.txt:write";
    public string PlanAction => Action;
    public string PlanScope => Scope;
    public Guid? PlanConversationId => null;
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { domain = "hic-disposable-git-plan-v1",
            action = Action, scope = Scope, plan = this }, GitEditExecutor.JsonOptions)));
}

internal sealed record GitEditResult(
    string Action, Guid RunId, string? RepositoryId, string? BaseCommit,
    string? WorktreePath, string? Branch, string Diff, string TestCode,
    bool SourceUnchanged, DateTime CheckedAtUtc);

internal sealed class GitEditDeniedException : Exception
{
    internal GitEditDeniedException() : base("Disposable repository scope was rejected.") { }
}

internal static class GitEditExecutor
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    internal const string Marker = "HIC disposable test repository v1\n";
    private static readonly HashSet<string> SafeConfigKeys = new(StringComparer.Ordinal)
    {
        "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates",
        "core.symlinks", "core.ignorecase", "core.autocrlf", "user.name", "user.email"
    };

    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
    {
        if (!condition) throw new GitEditDeniedException();
    }

    internal static bool ValidContent(string? content) =>
        content is { Length: > 0 and <= 512 } && content.EndsWith('\n') &&
        content.Replace("\r\n", "\n", StringComparison.Ordinal).All(c => c is '\n' or >= ' ' and <= '~');

    internal static bool ValidModelContent(string? content) =>
        content is { Length: > 0 and <= 8192 } && content.EndsWith('\n') &&
        !content.Contains('\0') && content.All(c => c is '\n' or '\r' or '\t' || !char.IsControl(c));

    internal static bool ValidRepositoryFile(string? file) =>
        file is { Length: > 0 and <= 120 } && !file.StartsWith('/') &&
        !file.Contains('\\') && !file.Contains(':') &&
        file.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..")) &&
        !file.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(file, ".git", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(file, ".hic-disposable", StringComparison.Ordinal);

    internal static void CheckPath(string path)
    {
        Require(System.IO.Path.IsPathFullyQualified(path));
        Require(!path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith("//", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
            Require(new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType != DriveType.Network);
        for (var current = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(current);
             current = System.IO.Path.GetDirectoryName(current))
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0);
    }

    internal static GitTestRepository[] Configuration(IConfiguration configuration)
    {
        var json = configuration["HIC_DISPOSABLE_GIT_REPOSITORIES"];
        var publisherJson = configuration["HIC_GITHUB_LOCAL_REPOSITORIES"];
        if (string.IsNullOrWhiteSpace(json) && string.IsNullOrWhiteSpace(publisherJson)) return [];
        static GitTestRepository[] Read(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return [];
            var parsed = JsonSerializer.Deserialize<GitTestRepository[]>(value, JsonOptions);
            Require(parsed is { Length: > 0 and <= 4 } && parsed.All(repo => repo is not null));
            return parsed;
        }
        var repositories = Read(json).Concat(Read(publisherJson)
            .Select(repo => repo with { PublisherSource = true })).ToArray();
        Require(repositories is { Length: > 0 and <= 4 });
        Require(repositories!.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == repositories.Length);
        foreach (var repo in repositories)
        {
            Require(Regex.IsMatch(repo.Id, "^[a-z0-9-]{1,40}$") &&
                Regex.IsMatch(repo.BaseCommit, "^[0-9a-f]{40}$") &&
                System.IO.Path.IsPathFullyQualified(repo.Path));
            Require(repo.Path == System.IO.Path.GetFullPath(repo.Path));
            Require(repo.Files.Count is > 0 and <= 8 &&
                repo.Files.Distinct(StringComparer.Ordinal).Count() == repo.Files.Count &&
                repo.Files.All(ValidRepositoryFile));
        }
        return repositories;
    }

    internal static async Task<string> GitAsync(string directory, CancellationToken cancellationToken, params string[] args)
    {
        var disabledHooks = System.IO.Path.Combine(directory, ".git", "hic-disabled-hooks");
        var disabledGlobal = System.IO.Path.Combine(directory, ".hic-nonexistent-global");
        Require(!Directory.Exists(disabledHooks) && !File.Exists(disabledHooks) &&
            !Directory.Exists(disabledGlobal) && !File.Exists(disabledGlobal));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("HIC_GITHUB_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = disabledGlobal;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var arg in new[] { "--no-pager", "-c", "core.hooksPath=" + disabledHooks,
            "-c", "core.fsmonitor=false", "-c", "core.autocrlf=false", "-c", "core.attributesFile=",
            "-c", "protocol.allow=never", "-c", "diff.external=", "-c", "core.quotePath=true" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new GitEditDeniedException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        async Task<string> ReadBounded(StreamReader reader)
        {
            var buffer = new char[1024];
            var output = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                Require(output.Length + count <= 8192);
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        try
        {
            var stdout = ReadBounded(process.StandardOutput);
            var stderr = ReadBounded(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            Require(process.ExitCode == 0);
            return await stdout;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static async Task InspectAsync(GitTestRepository repo, CancellationToken cancellationToken)
    {
        CheckPath(repo.Path);
        Require(Directory.Exists(System.IO.Path.Combine(repo.Path, ".git")));
        CheckPath(System.IO.Path.Combine(repo.Path, ".git"));
        CheckPath(System.IO.Path.Combine(repo.Path, ".git", "config"));
        var pending = new Stack<string>();
        pending.Push(System.IO.Path.Combine(repo.Path, ".git"));
        var entries = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                Require(++entries <= 512);
                CheckPath(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        foreach (var directory in new[] { "objects", "refs", "info" })
        {
            var path = System.IO.Path.Combine(repo.Path, ".git", directory);
            if (Directory.Exists(path)) CheckPath(path);
        }
        // Reject executable configuration before Git is invoked.
        Require(new FileInfo(System.IO.Path.Combine(repo.Path, ".git", "config")).Length <= 2048);
        var config = File.ReadAllText(System.IO.Path.Combine(repo.Path, ".git", "config"));
        Require(config.Length <= 2048 && !Regex.IsMatch(config, @"(?im)^\s*\[(?!core\]|user\])"));
        Require(!Regex.IsMatch(config, @"(?im)^\s*(?:include|fsmonitor|hooksPath|attributesFile|worktree|alternates|extensions)\s*="));
        var keys = await GitAsync(repo.Path, cancellationToken, "config", "--local", "--name-only", "--list");
        Require(keys.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(k => SafeConfigKeys.Contains(k.Trim())));
        Require(!File.Exists(System.IO.Path.Combine(repo.Path, ".git", "objects", "info", "alternates")) &&
            !File.Exists(System.IO.Path.Combine(repo.Path, ".git", "info", "attributes")) &&
            !File.Exists(System.IO.Path.Combine(repo.Path, ".git", "info", "grafts")) &&
            !File.Exists(System.IO.Path.Combine(repo.Path, ".git", "shallow")));
        Require((await GitAsync(repo.Path, cancellationToken, "rev-parse", "--show-toplevel")).Trim()
            .Replace('/', System.IO.Path.DirectorySeparatorChar) == repo.Path);
        Require((await GitAsync(repo.Path, cancellationToken, "rev-parse", "HEAD")).Trim() == repo.BaseCommit);
        var tree = (await GitAsync(repo.Path, cancellationToken, "ls-tree", "-r", "--full-tree", repo.BaseCommit))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Require(tree.Length <= 200 && (repo.PublisherSource || tree.Length == repo.Files.Count + 1) &&
            tree.All(entry => entry.StartsWith("100644 blob ", StringComparison.Ordinal)));
        var paths = tree.Select(entry => entry[(entry.IndexOf('\t') + 1)..]).ToHashSet(StringComparer.Ordinal);
        Require(repo.PublisherSource ? repo.Files.All(paths.Contains) :
            paths.SetEquals(repo.Files.Append(".hic-disposable")));
        Require((await GitAsync(repo.Path, cancellationToken, "status", "--porcelain", "--untracked-files=all")).Length == 0);
        foreach (var file in repo.PublisherSource ? repo.Files : repo.Files.Append(".hic-disposable"))
        {
            var path = System.IO.Path.Combine(repo.Path, file.Replace('/', System.IO.Path.DirectorySeparatorChar));
            CheckPath(path);
            Require(new FileInfo(path).Length <= 64 * 1024);
            var bytes = File.ReadAllBytes(path);
            Require(bytes.Length <= 64 * 1024 && !bytes.Contains((byte)0));
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        Require(repo.PublisherSource || File.ReadAllText(System.IO.Path.Combine(repo.Path, ".hic-disposable"))
            .Replace("\r\n", "\n", StringComparison.Ordinal) == Marker);
        foreach (var file in repo.Files)
        {
            var path = System.IO.Path.Combine(repo.Path, file.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Require(ValidModelContent(File.ReadAllText(path)));
        }
    }

    internal static async Task<GitEditResult> ExecuteAsync(
        IConfiguration configuration, Guid runId, GitEditPlan? plan, ILogger logger, CancellationToken cancellationToken)
    {
        string? worktree = null;
        var branch = "hic-run-" + runId.ToString("N");
        try
        {
            Require(plan is not null && plan.Revision == 1 && plan.File == "message.txt" && ValidContent(plan.Content));
            var repo = Configuration(configuration).SingleOrDefault(r => r.Id == plan!.RepositoryId);
            Require(repo is not null && repo.Path == plan!.RepositoryPath && repo.BaseCommit == plan.BaseCommit);
            await InspectAsync(repo!, cancellationToken);
            var root = configuration["HIC_GIT_RUN_ROOT"];
            Require(root is not null && System.IO.Path.IsPathFullyQualified(root));
            CheckPath(root!);
            Require(!System.IO.Path.GetFullPath(root!).StartsWith(repo!.Path + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFullPath(root!) != repo.Path);
            worktree = System.IO.Path.Combine(root!, runId.ToString("N"));
            Require(!Directory.Exists(worktree) && !File.Exists(worktree));
            var original = File.ReadAllBytes(System.IO.Path.Combine(repo.Path, "message.txt"));
            await GitAsync(repo.Path, cancellationToken, "worktree", "add", "-b", branch, "--", worktree, repo.BaseCommit);
            CheckPath(worktree);
            var destination = System.IO.Path.Combine(worktree, plan!.File);
            CheckPath(destination);
            // Only one known plain file is opened; no client-supplied command is executed.
            File.WriteAllText(destination, plan.Content, new UTF8Encoding(false));
            Require((await GitAsync(worktree, cancellationToken, "diff", "--name-only", "--no-ext-diff", "--no-textconv")).Trim() == "message.txt");
            var diff = await GitAsync(worktree, cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--", "message.txt");
            Require(Encoding.UTF8.GetByteCount(diff) <= 4096 && File.ReadAllText(destination) == plan.Content);
            await InspectAsync(repo, cancellationToken);
            Require(original.SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(repo.Path, "message.txt"))));
            return new(GitEditPlan.Action, runId, repo.Id, repo.BaseCommit, worktree, branch, diff,
                "passed:exact-content-and-scope", true, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is GitEditDeniedException or IOException or UnauthorizedAccessException
            or JsonException or Win32Exception or OperationCanceledException)
        {
            logger.LogWarning("Isolated Git run denied or interrupted; raw details withheld. Run {RunId}", runId);
            return new(GitEditPlan.Action, runId, plan?.RepositoryId, plan?.BaseCommit, worktree,
                worktree is null ? null : branch, "", "denied:scope-or-infrastructure", false, DateTime.UtcNow);
        }
    }
}

internal static class GitEditEndpoints
{
    internal static void Map(WebApplication app, string connectionString, LocalOperatorSessions? sessions)
    {
        var store = new LocalHealthRunStore(connectionString);
        var decisions = new GitPlanDecisionStore(connectionString);
        Task<bool> Ready(CancellationToken cancellationToken) => decisions.ReadyAsync(cancellationToken);
        app.MapGet("/local-runner/tasks/{taskId:guid}/git-decisions", async (
            Guid taskId, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return Results.Unauthorized();
            if (!await Ready(cancellationToken)) return Results.StatusCode(503);
            var history = await decisions.HistoryAsync(taskId, actor.Value, sessions!,
                context.Request.Headers.Authorization, cancellationToken);
            return history is null ? Results.Unauthorized() : Results.Ok(history);
        });
        app.MapGet("/local-runner/repositories", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return Results.Unauthorized();
            if (!await Ready(cancellationToken)) return Results.StatusCode(503);
            var repositories = GitEditExecutor.Configuration(app.Configuration);
            foreach (var repo in repositories) await GitEditExecutor.InspectAsync(repo, cancellationToken);
            return Results.Ok(repositories.Select(r => new { r.Id, r.Path, r.BaseCommit, files = r.Files, available = true }));
        });
        app.MapPost("/local-runner/tasks/{taskId:guid}/git-preview", async (
            Guid taskId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return Results.Unauthorized();
            if (!await Ready(cancellationToken)) return Results.StatusCode(503);
            var request = await ReadAsync<GitEditRequest>(context.Request, cancellationToken);
            if (request is null || request.File != "message.txt" || !GitEditExecutor.ValidContent(request.Content))
                return Results.BadRequest(new { error = "Only bounded message.txt text replacement is permitted." });
            var repo = GitEditExecutor.Configuration(app.Configuration).SingleOrDefault(r => r.Id == request.RepositoryId);
            if (repo is null || repo.BaseCommit != request.BaseCommit)
                return Results.Conflict(new { error = "Repository or immutable base commit is not authorized." });
            await GitEditExecutor.InspectAsync(repo, cancellationToken);
            var intent = await store.GetIntentAsync(taskId, cancellationToken);
            if (intent is null) return Results.Conflict(new { error = "Select an available Draft task." });
            var expiry = DateTime.UtcNow.AddMinutes(5);
            expiry = new DateTime(expiry.Ticks / 10 * 10, DateTimeKind.Utc);
            var plan = new GitEditPlan(1, taskId, actor.Value, intent.IntentDigestSha256, repo.Id, repo.Path,
                repo.BaseCommit, request.File, request.Content, expiry);
            if (!await decisions.IssueAsync(plan, sessions!, context.Request.Headers.Authorization, cancellationToken))
                return sessions?.Authenticate(context.Request.Headers.Authorization) is null
                    ? Results.Unauthorized() : Results.Conflict(new { error = "Task changed during preview." });
            return Results.Ok(new { plan, intentDigestSha256 = plan.Digest() });
        });
        async Task<IResult> Decide(Guid taskId, HttpContext context, bool approve, CancellationToken cancellationToken)
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = sessions?.Authenticate(context.Request.Headers.Authorization);
            if (actor is null) return Results.Unauthorized();
            if (!await Ready(cancellationToken)) return Results.StatusCode(503);
            var approval = await ReadAsync<GitApproval>(context.Request, cancellationToken);
            if (approval?.Plan is not { } plan || !GitPlanDecisionStore.Valid(plan, approval.IntentDigestSha256))
                return Results.BadRequest(new { error = "Review the exact bounded plan before deciding." });
            async Task<bool> AuthorizeRepository()
            {
                try
                {
                    var repo = GitEditExecutor.Configuration(app.Configuration).SingleOrDefault(r => r.Id == plan.RepositoryId);
                    if (repo is null || repo.Path != plan.RepositoryPath || repo.BaseCommit != plan.BaseCommit) return false;
                    await GitEditExecutor.InspectAsync(repo, cancellationToken);
                    return true;
                }
                catch (Exception exception) when (exception is GitEditDeniedException or IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
            var outcome = await decisions.DecideAsync(taskId, actor.Value, plan, approval.IntentDigestSha256,
                approve, sessions!, context.Request.Headers.Authorization, cancellationToken, AuthorizeRepository);
            if (outcome.Code == "unauthorized") return Results.Unauthorized();
            if (outcome.Decision is null)
                return Results.Conflict(new { error = "Plan expired, changed, not issued or already decided.", code = outcome.Code });
            if (!approve) return Results.Ok(new
            {
                outcome.Decision.DecisionId, outcome.Decision.TaskId, outcome.Decision.ActorId,
                action = GitEditPlan.Action, scope = GitEditPlan.Scope,
                outcome.Decision.IntentDigestSha256, decision = "rejected",
                outcome.Decision.DecidedAtUtc, runId = (Guid?)null
            });
            var run = outcome.Run!;
            return Results.Accepted($"/local-runner/runs/{run.RunId}", new
                {
                    run.RunId, run.TaskId, run.Action, run.Scope, run.IntentDigestSha256, run.Status
                });
        }
        app.MapPost("/local-runner/tasks/{taskId:guid}/git-approve",
            (Guid taskId, HttpContext context, CancellationToken ct) => Decide(taskId, context, true, ct));
        app.MapPost("/local-runner/tasks/{taskId:guid}/git-reject",
            (Guid taskId, HttpContext context, CancellationToken ct) => Decide(taskId, context, false, ct));
    }

    internal sealed record GitApproval(GitEditPlan Plan, string IntentDigestSha256);
    private static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
    {
        if (request.ContentLength is null or < 1 or > 4096 || request.Headers.TransferEncoding.Count > 0 ||
            request.ContentType?.Split(';')[0] != "application/json") return null;
        try { return await JsonSerializer.DeserializeAsync<T>(request.Body, GitEditExecutor.JsonOptions, cancellationToken); }
        catch (JsonException) { return null; }
    }
}
