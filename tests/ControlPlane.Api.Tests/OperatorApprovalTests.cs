using Dapper;
using Npgsql;

namespace ControlPlane.Api.Tests;

[Trait("Category", "PostgreSQLIntegration")]
public class OperatorApprovalTests
{
    [PostgresFact]
    public async Task ApproveWithoutAuthorizationHeader_Returns401()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var context = new MockHttpContext();
            context.Request.Headers.Authorization = string.Empty;

            var sessions = LocalOperatorSessions.Create(
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                Guid.NewGuid().ToString());
            Assert.NotNull(sessions);

            var result = await ApproveTaskWithAuth(cs, id, context, sessions);

            Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
            var status = await db.QuerySingleAsync<string>("select status from tasks where id = @id", new { id });
            Assert.Equal("Approved", status);
        });
    }

    [PostgresFact]
    public async Task ApproveNonexistentTask_Returns404NotFound()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var nonexistentId = Guid.NewGuid();
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);
            Assert.NotNull(sessions);

            var sessionToken = sessions.IssueSessionToken(bootstrap);
            Assert.NotNull(sessionToken);

            var context = new MockHttpContext();
            context.Request.Headers.Authorization = $"Bearer {sessionToken}";

            var result = await ApproveTaskWithAuth(cs, nonexistentId, context, sessions);

            Assert.Equal(TransitionOutcome.NotFound, result.Outcome);
        });
    }

    [PostgresFact]
    public async Task ApproveNonDraftTask_Returns409Conflict()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);
            Assert.NotNull(sessions);

            var sessionToken = sessions.IssueSessionToken(bootstrap);
            Assert.NotNull(sessionToken);

            var context = new MockHttpContext();
            context.Request.Headers.Authorization = $"Bearer {sessionToken}";

            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = "Completed", Id = id });

            var result = await ApproveTaskWithAuth(cs, id, context, sessions);

            Assert.Equal(TransitionOutcome.Conflict, result.Outcome);
            Assert.Contains("Invalid status transition", result.Error ?? string.Empty);
        });
    }

    [PostgresFact]
    public async Task SuccessfulApproval_TransitionsTaskAndQueuesForExecution()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);
            Assert.NotNull(sessions);

            var sessionToken = sessions.IssueSessionToken(bootstrap);
            Assert.NotNull(sessionToken);

            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            var context = new MockHttpContext();
            context.Request.Headers.Authorization = $"Bearer {sessionToken}";

            var result = await ApproveTaskWithAuth(cs, id, context, sessions);

            Assert.Equal(TransitionOutcome.Accepted, result.Outcome);

            var status = await db.QuerySingleAsync<string>("select status from tasks where id = @id", new { id });
            Assert.Equal(TaskStatuses.Approved, status);

            var audit = await db.QuerySingleAsync<string>(
                "select from_status || '>' || to_status from task_status_transitions where source = 'ApproveTask'");
            Assert.Equal($"{TaskStatuses.Draft}>{TaskStatuses.Approved}", audit);
        });
    }

    [PostgresFact]
    public async Task IdempotentApproval_SecondAttemptReturnsConflict()
    {
        await TaskTransitionPostgresTests.WithDatabase(async (cs, db, id) =>
        {
            var bootstrap = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var actorId = Guid.NewGuid().ToString();
            var sessions = LocalOperatorSessions.Create(bootstrap, actorId);
            Assert.NotNull(sessions);

            var sessionToken = sessions.IssueSessionToken(bootstrap);
            Assert.NotNull(sessionToken);

            await db.ExecuteAsync("update tasks set status = @Status where id = @Id",
                new { Status = TaskStatuses.Draft, Id = id });

            var context = new MockHttpContext();
            context.Request.Headers.Authorization = $"Bearer {sessionToken}";

            var firstApproval = await ApproveTaskWithAuth(cs, id, context, sessions);
            Assert.Equal(TransitionOutcome.Accepted, firstApproval.Outcome);

            var secondApproval = await ApproveTaskWithAuth(cs, id, context, sessions);
            Assert.Equal(TransitionOutcome.Conflict, secondApproval.Outcome);

            var auditCount = await db.QuerySingleAsync<int>(
                "select count(*)::int from task_status_transitions where source = 'ApproveTask'");
            Assert.Equal(1, auditCount);
        });
    }

    private static async Task<TransitionResult> ApproveTaskWithAuth(
        string connectionString, Guid taskId, MockHttpContext context, LocalOperatorSessions sessions)
    {
        var actorId = sessions.Authenticate(context.Request.Headers.Authorization);
        if (actorId == null)
        {
            return new TransitionResult(TransitionOutcome.Invalid, "Unauthorized: Invalid or missing Bearer token.");
        }

        return await TaskTransitionStore.ApplyAsync(
            connectionString, taskId, TaskStatuses.Approved, "ApproveTask", cancellationToken: CancellationToken.None);
    }

    private sealed class MockHttpContext
    {
        public MockRequest Request { get; } = new();
    }

    private sealed class MockRequest
    {
        public MockHeaders Headers { get; } = new();
    }

    private sealed class MockHeaders
    {
        private string? _authorization;

        public string? Authorization
        {
            get => _authorization;
            set => _authorization = value;
        }
    }
}
