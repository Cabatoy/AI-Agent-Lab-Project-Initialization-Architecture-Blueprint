namespace ControlPlane.Api.Tests;

public class TaskStatusTransitionsTests
{
    [Theory]
    [InlineData("Draft", "Approved", true)]
    [InlineData("Failed", "Retrying", true)]
    [InlineData("Approved", "Executing", true)]
    [InlineData("Executing", "Completed", true)]
    [InlineData("Completed", "Approved", false)]
    [InlineData("Rejected", "Approved", false)]
    [InlineData("Unknown", "Approved", false)]
    public void TryValidateTransition_ReturnsExpected(string from, string to, bool expected)
    {
        var result = TaskStatusTransitions.TryValidateTransition(from, to, out _);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Planned", true)]
    [InlineData("Cancelled", true)]
    [InlineData("NotAStatus", false)]
    public void IsKnownStatus_ReturnsExpected(string status, bool expected)
    {
        var result = TaskStatusTransitions.IsKnownStatus(status);

        Assert.Equal(expected, result);
    }
}
