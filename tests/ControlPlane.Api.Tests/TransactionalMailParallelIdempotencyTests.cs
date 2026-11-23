using ControlPlane.Api.TransactionalMail;

namespace ControlPlane.Api.Tests;

public sealed class TransactionalMailParallelIdempotencyTests
{
    [Fact]
    public async Task CaptureSink_ParallelRetriesCaptureExactlyOnce()
    {
        var sink = new CaptureTransactionalMailSink("Test", explicitlyEnabled: true);
        var intent = MailIntent.CreatePasswordChanged(
            "first@example.test", "en", "parallel-retry-20261124-0001",
            new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero), "First");

        var attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => sink.SendAsync(intent)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Status == TransactionalMailSendStatus.Captured);
        Assert.Equal(63, results.Count(result => result.Status == TransactionalMailSendStatus.Duplicate));
        Assert.Single(sink.Captures);
        Assert.Equal("first@example.test", Assert.Single(sink.Captures).Intent.Recipient);
    }

    [Fact]
    public async Task CaptureSink_ParallelIndependentRecipientsRemainIsolated()
    {
        var sink = new CaptureTransactionalMailSink("Test", explicitlyEnabled: true);
        var timestamp = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var intents = Enumerable.Range(0, 16).Select(index =>
            MailIntent.CreatePasswordChanged(
                $"account{index}@example.test", "tr",
                $"independent-mail-20261124-{index:D4}", timestamp, $"Account {index}"))
            .ToArray();

        var results = await Task.WhenAll(intents.Select(intent =>
            Task.Run(() => sink.SendAsync(intent))));

        Assert.All(results, result => Assert.Equal(TransactionalMailSendStatus.Captured, result.Status));
        Assert.Equal(intents.Length, sink.Captures.Count);
        Assert.Equal(intents.Length, sink.Captures.Select(mail => mail.Intent.Recipient).Distinct().Count());
        Assert.All(sink.Captures, mail => Assert.DoesNotContain("<script", mail.HtmlBody, StringComparison.OrdinalIgnoreCase));
    }
}
