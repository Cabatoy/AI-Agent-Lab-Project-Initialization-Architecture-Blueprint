using ControlPlane.Api.TransactionalMail;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

internal sealed record PasswordForgotRequest(string? Identifier);
internal sealed record PasswordCompleteRequest(string? Token, string? NewPassword);

internal static class PasswordRecoveryEndpoints
{
    internal static void Map(WebApplication app, string connectionString, string? trustedAuthOrigin,
        string? trustedResetOrigin, PasswordResetStore? resetStore)
    {
        var linkOrigin = HicAuthRequestGuard.NormalizeTrustedOrigin(trustedResetOrigin, isDevelopment: false);

        app.MapPost("/auth/password/forgot", async (HttpContext context, PasswordForgotRequest request,
            IAntiforgery antiforgery, ITransactionalMailSender sender, CancellationToken cancellationToken) =>
        {
            if (trustedAuthOrigin is null || linkOrigin is null || resetStore is null ||
                sender is not CaptureTransactionalMailSink)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!HicAuthRequestGuard.HasTrustedOrigin(context.Request, trustedAuthOrigin))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!await HicAuthRequestGuard.HasValidCsrfAsync(context, antiforgery))
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(request.Identifier) || request.Identifier.Length > 320)
                return Results.BadRequest();

            var user = await UserIdentityStore.FindByIdentifierAsync(connectionString, request.Identifier, cancellationToken);
            if (user is not null)
            {
                var expiresAt = DateTimeOffset.UtcNow.Add(PasswordResetStore.Lifetime);
                var token = await resetStore.IssueAsync(user.Id, cancellationToken);
                if (token is not null)
                {
                    var link = $"{linkOrigin}/?reset_token={Uri.EscapeDataString(token)}";
                    var intent = MailIntent.CreatePasswordReset(user.NormalizedEmail, "en",
                        Guid.NewGuid().ToString("N"), expiresAt, link, "HIC user");
                    try
                    {
                        await sender.SendAsync(intent, cancellationToken);
                    }
                    catch (TransactionalMailValidationException)
                    {
                        app.Logger.LogError("Password recovery mail was rejected; details withheld.");
                        await resetStore.CancelAsync(user.Id, token, CancellationToken.None);
                    }
                }
            }
            return Results.Json(new { accepted = true }, statusCode: StatusCodes.Status202Accepted);
        })
            .AllowAnonymous()
            .RequireRateLimiting("hic-password-recovery")
            .WithMetadata(new RequestSizeLimitAttribute(4096));

        app.MapPost("/auth/password/reset", async (HttpContext context, PasswordCompleteRequest request,
            IAntiforgery antiforgery, ITransactionalMailSender sender, CancellationToken cancellationToken) =>
        {
            if (trustedAuthOrigin is null || linkOrigin is null || resetStore is null ||
                sender is not CaptureTransactionalMailSink)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!HicAuthRequestGuard.HasTrustedOrigin(context.Request, trustedAuthOrigin))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!await HicAuthRequestGuard.HasValidCsrfAsync(context, antiforgery))
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            if (request.NewPassword is null || request.NewPassword.Length is < 12 or > 1024)
                return Results.BadRequest();
            return await resetStore.ResetPasswordByTokenAsync(request.Token, request.NewPassword, cancellationToken)
                ? Results.NoContent()
                : Results.BadRequest();
        })
            .AllowAnonymous()
            .RequireRateLimiting("hic-password-recovery")
            .WithMetadata(new RequestSizeLimitAttribute(4096));
    }
}
