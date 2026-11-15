namespace ControlPlane.Api.Tests;

public sealed class RelayHandoffIntentTests
{
    private static readonly Guid TaskId = Guid.Parse("d45e7a26-7f4e-49bc-8c65-99e1bbcf6b2e");
    private static readonly DateTime UpdatedAt = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private static string Digest(
        Guid? taskId = null, string title = "Build", string description = "Test",
        DateTime? updatedAt = null, string repository = "Cabatoy/example",
        string expectedBaseRef = "develop") =>
        RelayHandoffIntent.Digest(taskId ?? TaskId, title, description,
            updatedAt ?? UpdatedAt, repository, expectedBaseRef);

    [Fact]
    public void StableDigest_IsLowercaseSha256_AndMatchesInConstantTime()
    {
        var digest = Digest();
        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.Equal(digest, Digest());
        Assert.True(RelayHandoffIntent.Matches(digest, digest));
        Assert.False(RelayHandoffIntent.Matches(digest.ToUpperInvariant(), digest));
        Assert.False(RelayHandoffIntent.Matches(null, digest));
        Assert.False(RelayHandoffIntent.Matches("bad", digest));
    }

    [Fact]
    public void SnapshotChanges_InvalidateDigest()
    {
        var original = Digest();
        Assert.NotEqual(original, Digest(title: "Build changed"));
        Assert.NotEqual(original, Digest(description: "Test changed"));
        Assert.NotEqual(original, Digest(taskId: Guid.NewGuid()));
        Assert.NotEqual(original, Digest(updatedAt: UpdatedAt.AddTicks(1)));
        Assert.NotEqual(original, Digest(repository: "Cabatoy/other"));
        Assert.NotEqual(original, Digest(expectedBaseRef: "main"));
        Assert.False(RelayHandoffIntent.Matches(original, Digest(title: "Build changed")));
    }

    [Fact]
    public void LengthPrefixing_AndUtf8_KeepFieldsUnambiguous()
    {
        Assert.NotEqual(Digest(title: "ab", description: "c"), Digest(title: "a", description: "bc"));
        Assert.NotEqual(Digest(title: "ü", description: "x"), Digest(title: "u", description: "x"));
    }

    [Fact]
    public void InvalidSnapshot_FailsClosed()
    {
        Assert.Throws<ArgumentException>(() => Digest(taskId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Digest(title: " "));
        Assert.Throws<ArgumentException>(() => Digest(repository: ""));
        Assert.Throws<ArgumentException>(() => Digest(expectedBaseRef: ""));
        Assert.Throws<ArgumentException>(() => Digest(updatedAt: DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => Digest(description: null!));
        Assert.Throws<ArgumentException>(() => Digest(description: ""));
        Assert.Throws<ArgumentException>(() => Digest(title: "Build" + (char)10 + "Injected"));
        Assert.Throws<ArgumentException>(() => Digest(description: "Test" + (char)13 + "Injected"));
        Assert.Throws<ArgumentException>(() => Digest(title: "Build" + (char)9 + "Injected"));
        Assert.Throws<ArgumentException>(() => Digest(description: "Test" + (char)0 + "Injected"));
        Assert.Throws<ArgumentException>(() => Digest(title: new string('x', 257)));
        Assert.Throws<ArgumentException>(() => Digest(description: new string('x', 16385)));
        Assert.Equal(64, Digest(title: new string('x', 256), description: new string('x', 16384)).Length);
    }

    [Theory]
    [InlineData("owner/repo/extra")]
    [InlineData("../repo")]
    [InlineData("owner/..")]
    [InlineData("owner/repo\\nmalicious")]
    [InlineData("owner/.hidden")]
    [InlineData("owner/repo.")]
    [InlineData("owner/repo.lock")]
    [InlineData("owner/repo.LOCK")]
    [InlineData("owner./repo")]
    [InlineData("owner.lock/repo")]
    [InlineData("owner/repo name")]
    public void UnsafeRepository_IsRejected(string repository) =>
        Assert.Throws<ArgumentException>(() => Digest(repository: repository));

    [Theory]
    [InlineData("../main")]
    [InlineData("feature//topic")]
    [InlineData("feature/..")]
    [InlineData("topic.lock")]
    [InlineData("-danger")]
    [InlineData("feature/topic\\nmalicious")]
    [InlineData("feature/")]
    [InlineData("feature/topic.")]
    [InlineData("feature/topic.lock/child")]
    [InlineData("feature/topic.LOCK/child")]
    [InlineData("feature/topic./child")]
    public void UnsafeBaseRef_IsRejected(string expectedBaseRef) =>
        Assert.Throws<ArgumentException>(() => Digest(expectedBaseRef: expectedBaseRef));

    [Fact]
    public void TrailingNewlineCannotBypassAbsoluteEndAnchors()
    {
        Assert.Throws<ArgumentException>(() => Digest(repository: "owner/repo\n"));
        Assert.Throws<ArgumentException>(() => Digest(expectedBaseRef: "feature/topic\n"));
        Assert.False(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 32) + "\n"));
        Assert.False(RelayHandoffIntent.Matches(new string('a', 64) + "\n", Digest()));
        Assert.False(RelayHandoffIntent.Matches(Digest() + "\n", Digest()));
    }

    [Fact]
    public void IdempotencyKey_RejectsOversizedInputBeforeRegexEvaluation()
    {
        Assert.False(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 129)));
        Assert.False(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 1_000_000)));
        Assert.True(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 128)));
        Assert.True(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 32)));
        Assert.False(RelayHandoffIntent.IsValidIdempotencyKey(new string('a', 31)));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEF", true)]
    [InlineData("01234567890123456789012345678901", true)]
    [InlineData("short", false)]
    [InlineData("contains whitespace and a long value", false)]
    [InlineData("0123456789012345678901234567890!", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IdempotencyKey_UsesStrictOpaqueAlphabet(string? key, bool expected) =>
        Assert.Equal(expected, RelayHandoffIntent.IsValidIdempotencyKey(key));
}
