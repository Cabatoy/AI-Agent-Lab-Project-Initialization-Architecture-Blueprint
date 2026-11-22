using ControlPlane.Api.TransactionalMail;

namespace ControlPlane.Api.Tests;

public sealed class TransactionalMailTests
{
    private static readonly DateTimeOffset ResetExpiry =
        new(2026, 12, 1, 12, 30, 0, TimeSpan.Zero);
    private const string TrustedResetOrigin = "https://auth.example.com";

    [Theory]
    [InlineData("tr")]
    [InlineData("en")]
    public void PasswordReset_Renderer_SelectsHtmlAndTextForSupportedLocale(string locale)
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com",
            locale,
            "reset-idempotency-0001",
            ResetExpiry,
            "https://auth.example.com/reset?ticket=future-issued-token",
            "Ada");
        var mail = new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent);

        Assert.Contains(locale == "tr" ? "şifre" : "password", mail.Subject.ToLowerInvariant());
        Assert.Contains(locale == "tr" ? "Merhaba Ada" : "Hello Ada", mail.HtmlBody);
        Assert.Contains(locale == "tr" ? "Merhaba Ada" : "Hello Ada", mail.PlainTextBody);
        Assert.Contains("https://auth.example.com/reset", mail.HtmlBody);
    }

    [Fact]
    public void PasswordChanged_Renderer_ProducesBothForms()
    {
        var intent = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "changed-idempotency-0001",
            ResetExpiry,
            "Ada");
        var mail = new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent);

        Assert.Contains("password changed", mail.Subject);
        Assert.Contains("Hello Ada", mail.HtmlBody);
        Assert.Contains("Hello Ada", mail.PlainTextBody);
    }

    [Fact]
    public void Renderer_EscapesUserGeneratedData()
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com",
            "en",
            "escape-idempotency-0001",
            ResetExpiry,
            "https://auth.example.com/reset",
            "<b>Attacker</b>&\"");
        var mail = new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent);

        Assert.Contains("&lt;b&gt;Attacker&lt;/b&gt;&amp;&quot;", mail.HtmlBody);
        Assert.DoesNotContain("<b>Attacker</b>", mail.HtmlBody);
    }

    [Fact]
    public void Renderer_RejectsMissingOrUnexpectedPlaceholders()
    {
        var missing = MailIntent.CreatePasswordReset(
            "person@example.com",
            "en",
            "missing-idempotency-0001",
            ResetExpiry,
            "https://auth.example.com/reset",
            "Ada");
        missing = missing with
        {
            Placeholders = new Dictionary<string, string>
            {
                ["displayName"] = "Ada"
            }
        };
        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(missing));

        var unexpected = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "unexpected-idempotency-0001",
            ResetExpiry,
            "Ada");
        unexpected = unexpected with
        {
            Placeholders = new Dictionary<string, string>(
                unexpected.Placeholders)
            {
                ["rawHtml"] = "<script>alert(1)</script>"
            }
        };
        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(unexpected));
    }

    [Theory]
    [InlineData("http://auth.example.com/reset")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://auth.example.com/reset#fragment")]
    [InlineData("https://user@auth.example.com/reset")]
    public void Renderer_RejectsUnsafeResetTargets(string resetUrl)
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com",
            "en",
            "unsafe-idempotency-0001",
            ResetExpiry,
            resetUrl,
            "Ada");

        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent));
    }

    [Theory]
    [InlineData("https://attacker.example/reset?ticket=stolen")]
    [InlineData("https://auth.example.com.evil.test/reset")]
    [InlineData("https://evil.test/auth.example.com/reset")]
    [InlineData("https://auth.example.com:444/reset")]
    public void Renderer_RejectsUntrustedHttpsResetTargets(string resetUrl)
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com", "en", "external-idempotency-0001",
            ResetExpiry, resetUrl, "Ada");

        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://auth.example.com")]
    [InlineData("https://evil.test")]
    [InlineData("https://auth.example.com/path")]
    public void Renderer_RejectsResetWithoutValidTrustedOrigin(string? configuredOrigin)
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com", "en", "origin-idempotency-0001",
            ResetExpiry, "https://auth.example.com/reset", "Ada");

        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(configuredOrigin).Render(intent));
    }

    [Fact]
    public async Task CaptureSink_EnforcesTrustedOriginForReset()
    {
        var sink = new CaptureTransactionalMailSink(
            "Development", explicitlyEnabled: true, trustedResetOrigin: TrustedResetOrigin);
        var accepted = MailIntent.CreatePasswordReset(
            "person@example.com", "tr", "sink-trusted-idempotency-0001",
            ResetExpiry, "https://auth.example.com/reset?token=fixture", "Ada");
        var rejected = accepted with
        {
            IdempotencyKey = "sink-reject-idempotency-0001",
            Placeholders = new Dictionary<string, string>(accepted.Placeholders)
            {
                ["resetUrl"] = "https://attacker.example/reset"
            }
        };

        Assert.Equal(TransactionalMailSendStatus.Captured, (await sink.SendAsync(accepted)).Status);
        await Assert.ThrowsAsync<TransactionalMailValidationException>(
            () => sink.SendAsync(rejected));
        Assert.Single(sink.Captures);
    }

    [Fact]
    public void Renderer_RejectsMissingResetUrl()
    {
        var intent = MailIntent.CreatePasswordReset(
            "person@example.com",
            "tr",
            "missing-token-idempotency",
            ResetExpiry,
            "",
            "Ada");

        Assert.Throws<TransactionalMailValidationException>(
            () => new TransactionalMailTemplateRenderer(TrustedResetOrigin).Render(intent));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task CaptureSink_AllowsOnlyExplicitNonProductionCapture(string environmentName)
    {
        var sink = new CaptureTransactionalMailSink(environmentName, explicitlyEnabled: true);
        Assert.Empty(sink.Captures);

        var intent = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "capture-idempotency-0001",
            ResetExpiry,
            "Ada");
        var captured = await sink.SendAsync(intent);

        Assert.Equal(TransactionalMailSendStatus.Captured, captured.Status);
        Assert.Single(sink.Captures);
    }

    [Fact]
    public void CaptureSink_RejectsImplicitEnablementAndProduction()
    {
        Assert.Throws<InvalidOperationException>(
            () => new CaptureTransactionalMailSink("Development", explicitlyEnabled: false));
        Assert.Throws<InvalidOperationException>(
            () => new CaptureTransactionalMailSink("Production", explicitlyEnabled: true));
    }

    [Fact]
    public async Task CaptureSink_BasicIdempotencyReturnsDuplicate()
    {
        var sink = new CaptureTransactionalMailSink("Development", explicitlyEnabled: true);
        var intent = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "duplicate-idempotency-0001",
            ResetExpiry,
            "Ada");

        var first = await sink.SendAsync(intent);
        var second = await sink.SendAsync(intent);

        Assert.Equal(TransactionalMailSendStatus.Captured, first.Status);
        Assert.Equal(TransactionalMailSendStatus.Duplicate, second.Status);
        Assert.Single(sink.Captures);
    }

    [Fact]
    public async Task CaptureSink_RejectsIdempotencyKeyReuseForDifferentIntent()
    {
        var sink = new CaptureTransactionalMailSink("Development", explicitlyEnabled: true);
        var first = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "collision-idempotency-0001",
            ResetExpiry,
            "Ada");
        var second = first with { Recipient = "other@example.com" };

        await sink.SendAsync(first);
        await Assert.ThrowsAsync<TransactionalMailValidationException>(
            () => sink.SendAsync(second));
        Assert.Single(sink.Captures);
    }

    [Fact]
    public async Task CaptureSink_HasNoExternalTransportAndNoSecretConfiguration()
    {
        var sink = new CaptureTransactionalMailSink("Test", explicitlyEnabled: true);
        var before = sink.Captures.Count;

        Assert.Equal(0, before);
        Assert.Throws<InvalidOperationException>(
            () => new CaptureTransactionalMailSink("Production", explicitlyEnabled: true));

        var unconfigured = new UnconfiguredTransactionalMailSender();
        var intent = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "network-idempotency-0001",
            ResetExpiry,
            "Ada");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => unconfigured.SendAsync(intent));
    }

    [Fact]
    public void Contract_IsVersionedAndDoesNotContainRawHtml()
    {
        var intent = MailIntent.CreatePasswordChanged(
            "person@example.com",
            "en",
            "contract-idempotency-0001",
            ResetExpiry,
            "Ada");

        Assert.Equal(1, intent.Version);
        Assert.Equal(MailIntent.ContractVersion, intent.Version);
        Assert.DoesNotContain(intent.Placeholders.Values, value => value.Contains("<html", StringComparison.OrdinalIgnoreCase));
    }
}
