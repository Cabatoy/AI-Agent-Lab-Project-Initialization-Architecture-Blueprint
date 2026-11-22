using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.TransactionalMail;

public sealed record MailIntent
{
    public const int ContractVersion = 1;

    public required int Version { get; init; }
    public required string TemplateKey { get; init; }
    public required string Recipient { get; init; }
    public required string Locale { get; init; }
    public required string IdempotencyKey { get; init; }
    public required DateTimeOffset SemanticExpiresAtUtc { get; init; }
    public required IReadOnlyDictionary<string, string> Placeholders { get; init; }

    public static MailIntent CreatePasswordReset(
        string recipient,
        string locale,
        string idempotencyKey,
        DateTimeOffset resetExpiresAtUtc,
        string resetUrl,
        string displayName)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["displayName"] = displayName,
            ["resetUrl"] = resetUrl,
            ["resetExpiresAtUtc"] = resetExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture)
        };
        return new MailIntent
        {
            Version = ContractVersion,
            TemplateKey = TransactionalMailTemplateKeys.PasswordReset,
            Recipient = recipient,
            Locale = locale,
            IdempotencyKey = idempotencyKey,
            SemanticExpiresAtUtc = resetExpiresAtUtc,
            Placeholders = new ReadOnlyDictionary<string, string>(placeholders)
        };
    }

    public static MailIntent CreatePasswordChanged(
        string recipient,
        string locale,
        string idempotencyKey,
        DateTimeOffset changedAtUtc,
        string displayName)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["displayName"] = displayName,
            ["changedAtUtc"] = changedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        };
        return new MailIntent
        {
            Version = ContractVersion,
            TemplateKey = TransactionalMailTemplateKeys.PasswordChanged,
            Recipient = recipient,
            Locale = locale,
            IdempotencyKey = idempotencyKey,
            SemanticExpiresAtUtc = changedAtUtc,
            Placeholders = new ReadOnlyDictionary<string, string>(placeholders)
        };
    }

    public string Fingerprint()
    {
        var canonical = new StringBuilder()
            .Append(Version).Append('\n')
            .Append(TemplateKey).Append('\n')
            .Append(Recipient).Append('\n')
            .Append(Locale).Append('\n')
            .Append(SemanticExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var pair in Placeholders.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            canonical.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

public static class TransactionalMailTemplateKeys
{
    public const string PasswordReset = "hic.password-reset";
    public const string PasswordChanged = "hic.password-changed";
}

public sealed record CapturedMail(
    string Subject,
    string HtmlBody,
    string PlainTextBody,
    MailIntent Intent);

public enum TransactionalMailSendStatus
{
    Captured,
    Duplicate
}

public sealed record TransactionalMailSendResult(
    TransactionalMailSendStatus Status,
    string IdempotencyKey);

public interface ITransactionalMailSender
{
    Task<TransactionalMailSendResult> SendAsync(
        MailIntent intent,
        CancellationToken cancellationToken = default);
}

public sealed class TransactionalMailValidationException : ArgumentException
{
    public TransactionalMailValidationException(string message) : base(message) { }
}

public sealed class TransactionalMailTemplateRenderer
{
    private readonly Uri? _trustedResetOrigin;

    public TransactionalMailTemplateRenderer(string? trustedResetOrigin = null)
    {
        // Without a configured HTTPS origin, reset mail must fail closed.
        if (Uri.TryCreate(trustedResetOrigin, UriKind.Absolute, out var origin) &&
            origin.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(origin.UserInfo) &&
            origin.AbsolutePath == "/" &&
            string.IsNullOrEmpty(origin.Query) &&
            string.IsNullOrEmpty(origin.Fragment))
            _trustedResetOrigin = origin;
    }

    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant);
    private static readonly Regex IdempotencyPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._:-]{15,127}$", RegexOptions.CultureInvariant);

    public CapturedMail Render(MailIntent intent)
    {
        ValidateIntent(intent);

        var locale = intent.Locale.ToLowerInvariant();
        var displayName = intent.Placeholders["displayName"];

        if (intent.TemplateKey == TransactionalMailTemplateKeys.PasswordReset)
        {
            var resetUrl = intent.Placeholders["resetUrl"];
            var resetExpires = ParseRequiredDate(intent.Placeholders["resetExpiresAtUtc"], "resetExpiresAtUtc");
            var subject = locale == "tr"
                ? "HIC şifre sıfırlama"
                : "HIC password reset";
            var safeName = EscapePlainText(displayName);
            var safeUrl = resetUrl;

            var html = locale == "tr"
                ? $"""
                   <html><body style="font-family:Arial,sans-serif;color:#172033">
                   <h1>Şifrenizi sıfırlayın</h1>
                   <p>Merhaba {EscapeHtml(safeName)},</p>
                   <p>HIC hesabınız için şifre sıfırlama talebini aldık.</p>
                   <p><a href="{EscapeHtml(safeUrl)}">Şifremi sıfırla</a></p>
                   <p>Bu bağlantı {EscapeHtml(resetExpires.ToString("u", CultureInfo.InvariantCulture))} tarihine kadar geçerlidir.</p>
                   </body></html>
                   """
                : $"""
                   <html><body style="font-family:Arial,sans-serif;color:#172033">
                   <h1>Reset your password</h1>
                   <p>Hello {EscapeHtml(safeName)},</p>
                   <p>We received a password reset request for your HIC account.</p>
                   <p><a href="{EscapeHtml(safeUrl)}">Reset my password</a></p>
                   <p>This link expires at {EscapeHtml(resetExpires.ToString("u", CultureInfo.InvariantCulture))}.</p>
                   </body></html>
                   """;
            var text = locale == "tr"
                ? $"HIC şifre sıfırlama\n\nMerhaba {safeName},\n\nHIC hesabınız için şifre sıfırlama talebini aldık.\n\nŞifre sıfırlama bağlantısı: {safeUrl}\nBağlantı son kullanma: {resetExpires:u}"
                : $"HIC password reset\n\nHello {safeName},\n\nWe received a password reset request for your HIC account.\n\nReset link: {safeUrl}\nLink expires at: {resetExpires:u}";

            return new CapturedMail(subject, html, text, intent);
        }

        if (intent.TemplateKey == TransactionalMailTemplateKeys.PasswordChanged)
        {
            var changedAt = ParseRequiredDate(intent.Placeholders["changedAtUtc"], "changedAtUtc");
            var subject = locale == "tr" ? "HIC şifresi değiştirildi" : "HIC password changed";
            var safeName = EscapePlainText(displayName);
            var html = locale == "tr"
                ? $"""
                   <html><body style="font-family:Arial,sans-serif;color:#172033">
                   <h1>Şifreniz değiştirildi</h1>
                   <p>Merhaba {EscapeHtml(safeName)},</p>
                   <p>HIC hesabınızın şifresi {EscapeHtml(changedAt.ToString("u", CultureInfo.InvariantCulture))} tarihinde değiştirildi.</p>
                   </body></html>
                   """
                : $"""
                   <html><body style="font-family:Arial,sans-serif;color:#172033">
                   <h1>Password changed</h1>
                   <p>Hello {EscapeHtml(safeName)},</p>
                   <p>The password for your HIC account was changed at {EscapeHtml(changedAt.ToString("u", CultureInfo.InvariantCulture))}.</p>
                   </body></html>
                   """;
            var text = locale == "tr"
                ? $"HIC şifresi değiştirildi\n\nMerhaba {safeName},\n\nHIC hesabınızın şifresi {changedAt:u} tarihinde değiştirildi."
                : $"HIC password changed\n\nHello {safeName},\n\nThe password for your HIC account was changed at {changedAt:u}.";

            return new CapturedMail(subject, html, text, intent);
        }

        throw new TransactionalMailValidationException($"Unknown template key.");
    }

    private static void ValidateIntent(MailIntent intent)
    {
        if (intent.Version != MailIntent.ContractVersion)
            throw new TransactionalMailValidationException("Unsupported mail contract version.");
        if (!EmailPattern.IsMatch(intent.Recipient) || intent.Recipient.Length > 254)
            throw new TransactionalMailValidationException("Recipient is not a valid email address.");
        if (intent.Locale is not ("tr" or "en"))
            throw new TransactionalMailValidationException("Only TR/EN locales are supported.");
        if (!IdempotencyPattern.IsMatch(intent.IdempotencyKey))
            throw new TransactionalMailValidationException("Idempotency key is invalid.");
        if (intent.Placeholders is null)
            throw new TransactionalMailValidationException("Placeholders are required.");

        if (intent.TemplateKey == TransactionalMailTemplateKeys.PasswordReset)
            ValidateReset(intent);
        else if (intent.TemplateKey == TransactionalMailTemplateKeys.PasswordChanged)
            ValidateChanged(intent);
        else
            throw new TransactionalMailValidationException("Unknown template key.");

        if (intent.SemanticExpiresAtUtc == default)
            throw new TransactionalMailValidationException("Semantic expiry is required.");
    }

    private static void ValidateReset(MailIntent intent)
    {
        AssertExactKeys(intent.Placeholders, "displayName", "resetUrl", "resetExpiresAtUtc");
        AssertSafeText(intent.Placeholders["displayName"], "displayName", 100);
        var resetUrl = intent.Placeholders["resetUrl"];
        if (resetUrl.Length > 2048 ||
            !Uri.TryCreate(resetUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            _trustedResetOrigin is null ||
            !string.Equals(uri.GetLeftPart(UriPartial.Authority),
                _trustedResetOrigin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            throw new TransactionalMailValidationException("Reset URL must use the configured trusted HTTPS origin.");
        ParseRequiredDate(intent.Placeholders["resetExpiresAtUtc"], "resetExpiresAtUtc");
    }

    private static void ValidateChanged(MailIntent intent)
    {
        AssertExactKeys(intent.Placeholders, "displayName", "changedAtUtc");
        AssertSafeText(intent.Placeholders["displayName"], "displayName", 100);
        ParseRequiredDate(intent.Placeholders["changedAtUtc"], "changedAtUtc");
    }

    private static void AssertExactKeys(IReadOnlyDictionary<string, string> placeholders, params string[] required)
    {
        if (placeholders.Count != required.Length ||
            required.Any(key => !placeholders.ContainsKey(key)))
            throw new TransactionalMailValidationException("Missing or unexpected template placeholders.");
    }

    private static void AssertSafeText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Contains('\r') || value.Contains('\n'))
            throw new TransactionalMailValidationException($"Placeholder {name} is missing or invalid.");
    }

    private static DateTimeOffset ParseRequiredDate(string value, string name)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var result))
            throw new TransactionalMailValidationException($"Placeholder {name} is missing or invalid.");
        return result;
    }

    private static string EscapeHtml(string value) => WebUtility.HtmlEncode(value);

    private static string EscapePlainText(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

public sealed class CaptureTransactionalMailSink : ITransactionalMailSender
{
    private readonly ConcurrentDictionary<string, CapturedMail> _captures = new(StringComparer.Ordinal);
    private readonly TransactionalMailTemplateRenderer _renderer;

    public CaptureTransactionalMailSink(
        string environmentName, bool explicitlyEnabled, string? trustedResetOrigin = null)
    {
        if (explicitlyEnabled != true)
            throw new InvalidOperationException("Development capture sink requires explicit enablement.");
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Development capture sink is forbidden in Production.");
        _renderer = new TransactionalMailTemplateRenderer(trustedResetOrigin);
    }

    public IReadOnlyCollection<CapturedMail> Captures =>
        _captures.Values.ToArray();

    public Task<TransactionalMailSendResult> SendAsync(
        MailIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var captured = _renderer.Render(intent);
        var fingerprint = intent.Fingerprint();

        if (_captures.TryGetValue(intent.IdempotencyKey, out var existing))
        {
            if (existing.Intent.Fingerprint() != fingerprint)
                throw new TransactionalMailValidationException(
                    "The idempotency key is already associated with a different mail intent.");
            return Task.FromResult(new TransactionalMailSendResult(
                TransactionalMailSendStatus.Duplicate, intent.IdempotencyKey));
        }

        if (_captures.TryAdd(intent.IdempotencyKey, captured))
            return Task.FromResult(new TransactionalMailSendResult(
                TransactionalMailSendStatus.Captured, intent.IdempotencyKey));

        return Task.FromResult(new TransactionalMailSendResult(
            TransactionalMailSendStatus.Duplicate, intent.IdempotencyKey));
    }

    public void Clear() => _captures.Clear();
}

public sealed class UnconfiguredTransactionalMailSender : ITransactionalMailSender
{
    public Task<TransactionalMailSendResult> SendAsync(
        MailIntent intent,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "No transactional mail transport is configured. The offline capture sink is not a production transport.");
}
