using System.Text;
using System.Text.Json;

internal static class GitHubPublisherEndpoints
{
    private sealed record ApprovalRequest(string IntentDigestSha256);

    internal static void Map(WebApplication app, string connectionString, LocalOperatorSessions? sessions)
    {
        var broker = new GitHubAppBroker(app.Configuration);
        var store = new GitHubPublicationStore(connectionString);
        var runs = new LocalHealthRunStore(connectionString);
        var group = app.MapGroup("/local-runner/github");
        group.AddEndpointFilter(async (context, next) =>
        {
            var actor = sessions?.Authenticate(context.HttpContext.Request.Headers.Authorization);
            if (actor is null) return Results.Unauthorized();
            context.HttpContext.Items["githubActor"] = actor.Value;
            try { return await next(context); }
            catch (Exception exception) when (exception is GitHubPublishException or GitEditDeniedException or
                IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                var code = exception is GitHubPublishException known ? known.Code : "github-validation-denied";
                app.Logger.LogWarning("GitHub publisher request rejected: {Code}; raw details withheld.", code);
                return Results.Conflict(new { error = code });
            }
        });
        static Guid Actor(HttpContext context) => (Guid)context.Items["githubActor"]!;
        async Task RequireReady(CancellationToken ct) =>
            GitHubAppBroker.Check(await store.ReadyAsync(ct), "github-migration-007-required");

        group.MapGet("/status", () => Results.Ok(broker.Status));
        group.MapGet("/repository", async (CancellationToken ct) =>
        {
            using var api = await broker.OpenAsync(ct);
            return Results.Ok(new { destination = api.Destination,
                baseCommit = await api.RefAsync(api.Destination.BaseBranch, ct),
                empty = await api.IsEmptyAsync(ct) });
        });
        group.MapPost("/bootstrap-preview", async (HttpContext context, CancellationToken ct) =>
        {
            RequireEmpty(context.Request);
            await RequireReady(ct);
            using var api = await broker.OpenAsync(ct);
            var plan = new GitHubPublicationPlan(Guid.NewGuid(), Actor(context), null, null, null,
                "bootstrap", api.Destination, null, null, api.Destination.BaseBranch,
                GitHubPublisher.BootstrapFile, GitHubPublisher.BootstrapContent,
                GitHubPublisher.ContentDigest(GitHubPublisher.BootstrapContent), null, broker.ScopeDigest(),
                DateTime.UtcNow.AddMinutes(5));
            await GitHubPublisher.ValidateBaseAsync(api, plan, ct);
            await store.CreateAsync(plan, ct);
            return Results.Ok(await store.ViewAsync((await store.ReadAsync(plan.PublicationId, Actor(context), ct))!, ct));
        });
        group.MapPost("/runs/{runId:guid}/preview", async (Guid runId, HttpContext context, CancellationToken ct) =>
        {
            RequireEmpty(context.Request);
            await RequireReady(ct);
            var code = await EligibleCodeAsync(app.Configuration, runs, runId, Actor(context), ct);
            using var api = await broker.OpenAsync(ct);
            var commit = await api.GetAsync(api.RepoPath + "/git/commits/" + code.BaseCommit, ct);
            var baseTree = GitHubApi.Sha(commit.GetProperty("tree"));
            var plan = new GitHubPublicationPlan(Guid.NewGuid(), Actor(context), code.TaskId, runId,
                code.ConversationId, "task", api.Destination, code.BaseCommit,
                baseTree, "hic/task-" + code.TaskId.ToString("N"),
                code.File, code.Content, GitHubPublisher.ContentDigest(code.Content), code.Digest(), broker.ScopeDigest(),
                DateTime.UtcNow.AddMinutes(5));
            await GitHubPublisher.ValidateBaseAsync(api, plan, ct);
            var tree = await GitHubPublisher.ReadTreeAsync(api, baseTree, ct);
            GitHubAppBroker.Check(tree.ContainsKey(plan.File), "github-file-not-in-base");
            await store.CreateAsync(plan, ct);
            return Results.Ok(await store.ViewAsync((await store.ReadAsync(plan.PublicationId, Actor(context), ct))!, ct));
        });
        group.MapGet("/publications/{id:guid}", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            await RequireReady(ct);
            var row = await store.ReadAsync(id, Actor(context), ct);
            return row is null ? Results.NotFound() : Results.Ok(await store.ViewAsync(row, ct));
        });
        group.MapGet("/bootstraps", async (HttpContext context, CancellationToken ct) =>
        {
            await RequireReady(ct);
            return Results.Ok(await store.BootstrapsAsync(Actor(context), ct));
        });
        group.MapPost("/publications/{id:guid}/approve", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            await RequireReady(ct);
            var approval = await ReadApprovalAsync(context.Request, ct);
            var row = await store.ReadAsync(id, Actor(context), ct);
            if (row is null) return Results.NotFound();
            GitHubAppBroker.Check(approval is not null && row.State == "preview" &&
                LocalHealthRunIntent.Matches(approval.IntentDigestSha256, row.Plan.Digest()) &&
                row.Plan.ExpiresAtUtc > DateTime.UtcNow, "github-approval-invalid-or-expired");
            GitHubAppBroker.Check(broker.ScopeDigest() == row.Plan.CredentialScopeDigest, "github-app-scope-changed");
            if (row.RunId is { } runId)
            {
                var code = await EligibleCodeAsync(app.Configuration, runs, runId, Actor(context), ct);
                GitHubAppBroker.Check(code.Digest() == row.Plan.CodePlanDigest, "github-code-plan-changed");
            }
            using var api = await broker.OpenAsync(ct);
            await GitHubPublisher.ValidateBaseAsync(api, row.Plan, ct);
            GitHubAppBroker.Check(await store.ApproveAsync(id, Actor(context), approval!.IntentDigestSha256, ct),
                "github-approval-already-decided");
            return Results.Ok(await store.ViewAsync((await store.ReadAsync(id, Actor(context), ct))!, ct));
        });
        group.MapPost("/publications/{id:guid}/renew", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            await RequireReady(ct);
            var request = await ReadApprovalAsync(context.Request, ct);
            var row = await store.ReadAsync(id, Actor(context), ct);
            if (row is null) return Results.NotFound();
            GitHubAppBroker.Check(request is not null && row.State == "preview" &&
                LocalHealthRunIntent.Matches(request.IntentDigestSha256, row.Digest),
                "github-preview-renewal-invalid");
            GitHubAppBroker.Check(broker.ScopeDigest() == row.Plan.CredentialScopeDigest, "github-app-scope-changed");
            if (row.RunId is { } runId)
            {
                var code = await EligibleCodeAsync(app.Configuration, runs, runId, Actor(context), ct);
                GitHubAppBroker.Check(code.Digest() == row.Plan.CodePlanDigest, "github-code-plan-changed");
            }
            using var api = await broker.OpenAsync(ct);
            await GitHubPublisher.ValidateBaseAsync(api, row.Plan, ct);
            GitHubAppBroker.Check(await store.RenewAsync(row.Plan, ct), "github-preview-not-expired-or-already-decided");
            return Results.Ok(await store.ViewAsync((await store.ReadAsync(id, Actor(context), ct))!, ct));
        });
        group.MapPost("/publications/{id:guid}/reject", async (Guid id, HttpContext context, CancellationToken ct) =>
        {
            await RequireReady(ct);
            var rejection = await ReadApprovalAsync(context.Request, ct);
            var row = await store.ReadAsync(id, Actor(context), ct);
            if (row is null) return Results.NotFound();
            GitHubAppBroker.Check(rejection is not null &&
                LocalHealthRunIntent.Matches(rejection.IntentDigestSha256, row.Digest),
                "github-rejection-invalid");
            GitHubAppBroker.Check(await store.RejectAsync(id, Actor(context), row.Digest, ct),
                "github-publication-already-decided");
            return Results.Ok(await store.ViewAsync((await store.ReadAsync(id, Actor(context), ct))!, ct));
        });
        foreach (var action in new[] { "execute", "reconcile", "complete-draft" })
        {
            group.MapPost("/publications/{id:guid}/" + action, async (
                Guid id, HttpContext context, CancellationToken ct) =>
            {
                RequireEmpty(context.Request);
                await RequireReady(ct);
                await using var publicationLock = await store.LockAsync(id, ct);
                GitHubAppBroker.Check(publicationLock is not null, "github-publication-busy");
                try
                {
                    var row = await store.ReadAsync(id, Actor(context), ct);
                    if (row is null) return Results.NotFound();
                    GitHubAppBroker.Check(row.ApprovedAtUtc is not null, "github-publication-not-approved");
                    GitHubAppBroker.Check(broker.ScopeDigest() == row.Plan.CredentialScopeDigest, "github-app-scope-changed");
                    if (action != "reconcile" && row.RunId is { } runId)
                    {
                        var code = await EligibleCodeAsync(app.Configuration, runs, runId, Actor(context), ct);
                        GitHubAppBroker.Check(code.Digest() == row.Plan.CodePlanDigest, "github-code-plan-changed");
                    }
                    using var api = await broker.OpenAsync(ct);
                    Task Save(GitHubPublication value, string code) => store.SaveAsync(value, code, CancellationToken.None);
                    // Request cancellation must not cancel recording an external-write outcome.
                    row = action switch
                    {
                        "execute" => await GitHubPublisher.ExecuteAsync(api, row, Save, CancellationToken.None),
                        "complete-draft" => await GitHubPublisher.CompleteDraftAsync(api, row, Save, CancellationToken.None),
                        _ => await GitHubPublisher.ReconcileAsync(api, row, Save, CancellationToken.None)
                    };
                    return Results.Ok(await store.ViewAsync(row, CancellationToken.None));
                }
                finally { await GitHubPublicationStore.UnlockAsync(publicationLock!, id); }
            });
        }
    }

    internal static async Task<ModelCodeEditPlan> EligibleCodeAsync(
        IConfiguration configuration, LocalHealthRunStore runs, Guid runId, Guid actor, CancellationToken ct)
    {
        var snapshot = await runs.GetAsync(runId, actor, ct);
        var plan = snapshot is null ? null : await runs.GetModelCodePlanAsync(runId, ct);
        GitHubAppBroker.Check(snapshot is not null && snapshot.Run.Status == "completed" &&
            snapshot.Run.Action == ModelCodeEditPlan.Action && plan is not null && plan.ActorId == actor &&
            snapshot.Run.TaskId == plan.TaskId && plan.Digest() == snapshot.Run.IntentDigestSha256 &&
            snapshot.Run.ResultJson is not null && await runs.ModelRunStillCurrentAsync(runId, plan, ct),
            "github-code-run-not-eligible");
        var result = JsonSerializer.Deserialize<ModelCodeEditResult>(snapshot.Run.ResultJson, GitEditExecutor.JsonOptions);
        GitHubAppBroker.Check(result is not null && result.RunId == runId && result.SourceUnchanged &&
            result.TestCode == "passed:isolated-csharp-compile-and-scope" && result.File == plan.File &&
            result.BaseCommit == plan.BaseCommit && result.WorktreePath is not null,
            "github-code-tests-not-passed");
        GitHubAppBroker.Check(configuration["HIC_GITHUB_SOURCE_REPOSITORY"] == plan.RepositoryId,
            "github-source-repository-not-authorized");
        var repo = GitEditExecutor.Configuration(configuration).SingleOrDefault(item => item.Id == plan.RepositoryId);
        GitHubAppBroker.Check(repo is not null && repo.Path == plan.RepositoryPath &&
            repo.BaseCommit == plan.BaseCommit && repo.Files.Contains(plan.File), "github-source-scope-changed");
        await GitEditExecutor.InspectAsync(repo, ct);
        var root = configuration["HIC_GIT_RUN_ROOT"];
        GitHubAppBroker.Check(root is not null && result.WorktreePath == Path.Combine(root, runId.ToString("N")),
            "github-worktree-scope-invalid");
        var file = Path.Combine(result.WorktreePath, plan.File.Replace('/', Path.DirectorySeparatorChar));
        GitEditExecutor.CheckPath(file);
        GitHubAppBroker.Check(new FileInfo(file).Length <= 32768 &&
            await File.ReadAllTextAsync(file, ct) == plan.Content, "github-tested-content-changed");
        return plan;
    }

    private static void RequireEmpty(HttpRequest request) =>
        GitHubAppBroker.Check(request.ContentLength is null or 0 && request.Headers.TransferEncoding.Count == 0,
            "github-unexpected-request-body");

    private static async Task<ApprovalRequest?> ReadApprovalAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or < 1 or > 256 || request.Headers.TransferEncoding.Count > 0 ||
            request.ContentType?.Split(';')[0] != "application/json") return null;
        try { return await JsonSerializer.DeserializeAsync<ApprovalRequest>(request.Body, GitEditExecutor.JsonOptions, ct); }
        catch (JsonException) { return null; }
    }
}
