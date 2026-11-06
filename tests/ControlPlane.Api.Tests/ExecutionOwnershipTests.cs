using System.Text.Json;

namespace ControlPlane.Api.Tests;

public class ExecutionOwnershipTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyClaimIdentity_IsRejectedBeforeDatabaseAccess(bool emptyExecution)
    {
        var result = await ExecutionOwnershipStore.ClaimAsync("invalid-connection-string", Guid.NewGuid(),
            new(emptyExecution ? Guid.Empty : Guid.NewGuid(), emptyExecution ? Guid.NewGuid() : Guid.Empty));
        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.False(result.NewlyClaimed);
        Assert.Null(result.Execution);
        Assert.NotEmpty(result.Error!);
    }

    [Theory]
    [InlineData("Approved", 0)]
    [InlineData("Retrying", 0)]
    [InlineData("Executing", 3)]
    [InlineData("Completed", 3)]
    [InlineData("Draft", 3)]
    [InlineData(null, 1)]
    public void ClaimPolicy_OnlyApprovesEligibleStates(string? status, int outcome)
    {
        Assert.Equal((TransitionOutcome)outcome, ExecutionOwnershipStore.ValidateClaim(status).Outcome);
    }

    [Fact]
    public void CurrentOwner_IsAccepted()
    {
        var execution = Execution();
        Assert.Equal(TransitionOutcome.Accepted, ExecutionOwnershipStore.ValidateOwner(2, execution,
            new(execution.Id, execution.WorkerId, 2)).Outcome);
    }

    [Theory]
    [InlineData("InProgress", true)]
    [InlineData("Review", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Blocked", false)]
    public void ExecutionLifecycle_KeepsOwnerThroughReview(string status, bool active)
    {
        Assert.Equal(active, ExecutionOwnershipStore.IsActiveStatus(status));
        var execution = Execution() with { Status = status };
        Assert.Equal(active ? TransitionOutcome.Accepted : TransitionOutcome.Conflict,
            ExecutionOwnershipStore.ValidateOwner(2, execution,
                new(execution.Id, execution.WorkerId, 2)).Outcome);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("worker")]
    [InlineData("generation")]
    [InlineData("zero-generation")]
    [InlineData("task-generation")]
    [InlineData("completed")]
    [InlineData("missing")]
    [InlineData("missing-proof")]
    public void StaleOrInvalidOwner_IsRejected(string change)
    {
        var execution = Execution();
        ExecutionIdentity? identity = new(execution.Id, execution.WorkerId, 2);
        identity = change switch
        {
            "id" => identity with { ExecutionId = Guid.NewGuid() },
            "worker" => identity with { WorkerId = Guid.NewGuid() },
            "generation" => identity with { Generation = 1 },
            "zero-generation" => identity with { Generation = 0 },
            "missing-proof" => null,
            _ => identity
        };
        var actual = change == "completed" ? execution with { CompletedAtUtc = DateTime.UtcNow } : execution;
        Assert.Equal(TransitionOutcome.Conflict, ExecutionOwnershipStore.ValidateOwner(
            change == "task-generation" ? 3 : 2, change == "missing" ? null : actual, identity).Outcome);
    }

    [Fact]
    public void ReplayRequiresSameExecutionTaskAndWorker()
    {
        var execution = Execution();
        var request = new ClaimExecutionRequest(execution.Id, execution.WorkerId);
        Assert.True(ExecutionOwnershipStore.IsReplay(execution, execution.TaskId, request));
        Assert.False(ExecutionOwnershipStore.IsReplay(execution, Guid.NewGuid(), request));
        Assert.False(ExecutionOwnershipStore.IsReplay(execution, execution.TaskId, request with { WorkerId = Guid.NewGuid() }));
        Assert.False(ExecutionOwnershipStore.IsReplay(execution, execution.TaskId, request with { ExecutionId = Guid.NewGuid() }));
    }

    [Fact]
    public void ClaimAndStatusContracts_RoundTrip()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var execution = Execution();
        var claim = new ClaimExecutionRequest(execution.Id, execution.WorkerId);
        Assert.Equal(claim, JsonSerializer.Deserialize<ClaimExecutionRequest>(JsonSerializer.Serialize(claim, options), options));
        var status = new UpdateTaskStatusRequest("Completed", 1, "OK", null, "Executing",
            new(execution.Id, execution.WorkerId, 2));
        Assert.Equal(status, JsonSerializer.Deserialize<UpdateTaskStatusRequest>(JsonSerializer.Serialize(status, options), options));
    }

    [Fact]
    public void VersionedMigration_IsEmbeddedWithoutAutomaticApplication()
    {
        Assert.Equal(2, ExecutionSchema.Version);
        Assert.Contains("ix_task_executions_one_active", ExecutionSchema.MigrationSql);
        Assert.Contains("execution_generation", ExecutionSchema.MigrationSql);
    }

    private static TaskExecution Execution() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, "Executing", DateTime.UtcNow, null);
}
