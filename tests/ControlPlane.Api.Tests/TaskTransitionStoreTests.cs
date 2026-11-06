namespace ControlPlane.Api.Tests;

public class TaskTransitionStoreTests
{
    [Theory]
    [InlineData("Approved", "Executing", "Approved", 0)]
    [InlineData("Draft", "Completed", "Draft", 2)]
    [InlineData("Executing", "Executing", "Approved", 3)]
    [InlineData("Approved", "Approved", null, 3)]
    [InlineData("Cancelled", "Executing", "Approved", 3)]
    [InlineData(null, "Executing", "Approved", 1)]
    public void Validate_UsesCurrentStateWithoutChangingStateMachine(
        string? current, string target, string? expected, int outcome)
    {
        Assert.Equal((TransitionOutcome)outcome, TaskTransitionStore.Validate(current, target, expected).Outcome);
    }
}
