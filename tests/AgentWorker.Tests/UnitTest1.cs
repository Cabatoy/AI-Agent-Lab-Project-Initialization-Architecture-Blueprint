using System.Net;

namespace AgentWorker.Tests;

public class WorkerReliabilityTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData((HttpStatusCode)429, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void IsTransientStatusCode_ReturnsExpected(HttpStatusCode statusCode, bool expected)
    {
        var result = Worker.IsTransientStatusCode(statusCode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsTransientException_ReturnsTrue_ForTransientExceptions()
    {
        Assert.True(Worker.IsTransientException(new HttpRequestException("network")));
        Assert.True(Worker.IsTransientException(new TimeoutException("timeout")));
        Assert.True(Worker.IsTransientException(new TaskCanceledException("canceled")));
    }

    [Fact]
    public void IsTransientException_ReturnsFalse_ForNonTransientExceptions()
    {
        var result = Worker.IsTransientException(new InvalidOperationException("non-transient"));

        Assert.False(result);
    }

    [Fact]
    public void ModelResult_Fail_DefaultsToNonTransient()
    {
        var result = ModelResult.Fail("error");

        Assert.False(result.Success);
        Assert.False(result.IsTransient);
        Assert.Equal("error", result.ErrorMessage);
    }

    [Fact]
    public void ModelResult_Fail_CanBeMarkedTransient()
    {
        var result = ModelResult.Fail("transient-error", true);

        Assert.False(result.Success);
        Assert.True(result.IsTransient);
    }
}
