using System.Text;
using Microsoft.AspNetCore.Http;

namespace ControlPlane.Api.Tests;

public sealed class RelayHandoffRequestTests
{
    private static async Task<bool> Accepts(string json, string? contentType = "application/json",
        long? declaredLength = null)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = contentType;
        context.Request.ContentLength = declaredLength ?? bytes.Length;
        return await RelayHandoffEndpoints.ParseHandoffRequestAsync(
            context.Request, CancellationToken.None) is not null;
    }

    private static string ValidJson => "{\"idempotencyKey\":\"" + new string('k', 32) +
        "\",\"intentDigestSha256\":\"" + new string('a', 64) +
        "\",\"repository\":\"Cabatoy/example\",\"expectedBaseRef\":\"develop\"}";

    [Fact]
    public async Task ValidRequest_IsAccepted() =>
        Assert.True(await Accepts(ValidJson));

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"idempotencyKey\":null}")]
    public async Task InvalidJson_IsRejected(string json) =>
        Assert.False(await Accepts(json));

    [Fact]
    public async Task DuplicateField_IsRejected() =>
        Assert.False(await Accepts(ValidJson.Replace(
            "\"repository\":\"Cabatoy/example\"",
            "\"repository\":\"Cabatoy/example\",\"repository\":\"Other/example\"")));

    [Fact]
    public async Task UnknownField_IsRejected() =>
        Assert.False(await Accepts(ValidJson.Replace(
            "\"expectedBaseRef\":\"develop\"",
            "\"expectedBaseRef\":\"develop\",\"execute\":true")));

    [Fact]
    public async Task NewlineInIdempotencyKey_IsRejected() =>
        Assert.False(await Accepts(ValidJson.Replace(new string('k', 32),
            new string('k', 32) + "\\n")));

    [Fact]
    public async Task MissingOrWrongContentType_IsRejected()
    {
        Assert.False(await Accepts(ValidJson, null));
        Assert.False(await Accepts(ValidJson, "text/plain"));
    }


    [Fact]
    public async Task DeclaredLengthSmallerThanActualBody_IsRejected() =>
        Assert.False(await Accepts(ValidJson + new string(' ', 1100),
            declaredLength: Encoding.UTF8.GetByteCount(ValidJson)));

    [Fact]
    public async Task DeclaredLengthLargerThanActualBody_IsRejected() =>
        Assert.False(await Accepts(ValidJson,
            declaredLength: Encoding.UTF8.GetByteCount(ValidJson) + 1));

    [Fact]
    public async Task OversizedOrUnboundedBody_IsRejected()
    {
        Assert.False(await Accepts(ValidJson, declaredLength: 1025));
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(ValidJson));
        context.Request.ContentLength = null;
        Assert.Null(await RelayHandoffEndpoints.ParseHandoffRequestAsync(
            context.Request, CancellationToken.None));
    }
}
