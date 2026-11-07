using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

var port = NativePortOptions.Parse(Environment.GetEnvironmentVariable("HIC_NATIVE_PORT"));
var token = Environment.GetEnvironmentVariable("HIC_NATIVE_TOKEN")
    ?? throw new InvalidOperationException("HIC_NATIVE_TOKEN is required.");
byte[] tokenBytes;
try { tokenBytes = Convert.FromBase64String(token); }
catch (FormatException) { throw new InvalidOperationException("HIC_NATIVE_TOKEN must be cryptographically random base64."); }
if (tokenBytes.Length < 32)
    throw new InvalidOperationException("HIC_NATIVE_TOKEN must contain at least 32 random bytes.");
CryptographicOperations.ZeroMemory(tokenBytes);

var workspace = Environment.GetEnvironmentVariable("HIC_WORKSPACE_ROOT")
    ?? throw new InvalidOperationException("HIC_WORKSPACE_ROOT is required.");
if (!Path.IsPathFullyQualified(workspace))
    throw new InvalidOperationException("HIC_WORKSPACE_ROOT must be absolute.");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Deliberately no configurable hostname: this service must NEVER bind to LAN/public IPs.
    kestrel.Listen(IPAddress.Loopback, port);
    kestrel.Limits.MaxRequestBodySize = 1024;
});
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is null ||
        !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) ||
        context.Request.Host.Value != $"127.0.0.1:{port}")
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.MapGet("/v1/status", () => Results.Ok(new
{
    status = "ready",
    service = "hic-native-host",
    capabilities = new[] { NativeActionPolicy.Action },
    taskExecutionEnabled = false
}));

app.MapPost("/v1/actions/run", async (HttpContext context) =>
{
    if (!NativeActionPolicy.ValidToken(context.Request.Headers["X-HIC-Native-Token"], token))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    if (context.Request.ContentLength is > 512 ||
        !string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json",
            StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    try
    {
        using var payload = await JsonDocument.ParseAsync(context.Request.Body,
            new JsonDocumentOptions { MaxDepth = 4 }, context.RequestAborted);
        if (!NativeActionPolicy.AllowedPayload(payload.RootElement))
            return Results.BadRequest(new { error = "Action not allowlisted." });
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid action JSON." });
    }

    // There is no process execution, untrusted path, arbitrary argument or model call.
    return Results.Ok(NativeActionPolicy.Inspect(workspace));
});

app.Run();
