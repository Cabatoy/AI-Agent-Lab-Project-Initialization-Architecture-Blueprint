using Hic.ExecutionProviders;

namespace Hic.ExecutionProviders.Tests;

public sealed class OfflineProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
    private const string Challenge = "synthetic-challenge";

    private sealed class Trust : ISyntheticTrustSource
    {
        public SyntheticAuthority? Authority { get; set; }
        public Dictionary<ProviderIdentity, SyntheticProof> Proofs { get; } = new();
        public Dictionary<ProviderIdentity, SyntheticConsent> Consents { get; } = new();
        public SyntheticAuthority? GetAuthority(ExecutionManifest manifest) => Authority;
        public SyntheticProof? GetProof(ProviderIdentity identity, string challenge) =>
            Proofs.GetValueOrDefault(identity);
        public SyntheticConsent? GetConsent(ProviderIdentity identity) => Consents.GetValueOrDefault(identity);

        public void SetAuthority(ExecutionManifest m) => Authority = new(
            m.TenantId, m.ActorId, m.TaskId, m.RunId, m.AttemptId, m.TaskRevision, m.ApprovalId,
            m.ApprovalEpoch, m.ClaimGeneration, m.Digest(), Now.AddMinutes(10), false);

        public void Add(ExecutionManifest m, ProviderCapabilities c)
        {
            Proofs[c.Identity] = new(SyntheticAdmission.ProofVersion, c.Identity, m.Digest(),
                SyntheticAdmission.CapabilitiesDigest(c), Challenge, Now.AddMinutes(-1),
                Now.AddMinutes(5), true, true);
            Consents[c.Identity] = new(m.TenantId, m.Digest(), c.Identity.InstallationId,
                c.Identity.InstanceId, c.Environment, c.Region, c.PrivacyClass, m.MaxCharge, m.Currency);
        }
    }

    private static ExecutionManifest Manifest() => new(
        ExecutionManifest.CurrentVersion, "tenant-a", "actor-a",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        1, Guid.Parse("44444444-4444-4444-4444-444444444444"), 1, 1,
        "installation", "owner/repo", new string('a', 40), new string('b', 64),
        "policy-1", "x64", "synthetic", 500, 256, 64, 30, 1024,
        "deny-all", "synthetic", "region-a", 0m, "USD", "key-1", Now.AddMinutes(10));

    private static ProviderCapabilities Capabilities(string id = "local") => new(
        SyntheticAdmission.CapabilityVersion, new("tenant-a", id, id + "-instance"),
        id, "region-a", "synthetic", "x64", "synthetic", 1000, 512, 128, 60, 2048,
        "deny-all", 0m, "USD", true, true);

    private static (ExecutionManifest M, Trust T, ProviderCapabilities C, IExecutionProvider P) Setup()
    {
        var m = Manifest();
        var t = new Trust();
        var c = Capabilities();
        t.SetAuthority(m);
        t.Add(m, c);
        return (m, t, c, new LocalOfflineFakeProvider(c, t, () => Now));
    }

    private static void Denied(ProviderError expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<ProviderException>(action).Code);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothProvidersSimulateOnlyAndAreIdempotent(bool remote)
    {
        var (m, t, c, _) = Setup();
        IExecutionProvider p = remote ? new RemoteOfflineFakeProvider(c, t, () => Now) :
            new LocalOfflineFakeProvider(c, t, () => Now);
        var h = p.Prepare(m, Challenge);
        Assert.Equal(h, p.Prepare(m, Challenge));
        Assert.Equal(FakeStatus.Started, p.Start(m, h, Challenge).Status);
        Assert.Equal(FakeStatus.Started, p.Start(m, h, Challenge).Status);
        Assert.Equal("OFFLINE_TESTED; OS_UNVERIFIED", p.CollectEvidence(m, h).EvidenceLevel);
        Assert.Equal(FakeStatus.Cancelled, p.Cancel(m, h).Status);
        Assert.Equal(FakeStatus.Cancelled, p.Cancel(m, h).Status);
        Assert.Equal(FakeStatus.Cleaned, p.Cleanup(m, h).Status);
        Assert.Equal(FakeStatus.Cleaned, p.Cleanup(m, h).Status);
        Denied(ProviderError.Cancelled, () => p.Start(m, h, Challenge));
        Denied(ProviderError.Cancelled, () => p.Prepare(m, Challenge));
    }

    [Fact]
    public void ManualNeverFallsBackAndAutoRequiresSpecificConsent()
    {
        var (m, t, c, p) = Setup();
        var remote = Capabilities("remote");
        t.Add(m, remote);
        IExecutionProvider q = new RemoteOfflineFakeProvider(remote, t, () => Now);
        t.Proofs.Remove(c.Identity);
        Denied(ProviderError.AttestationUnverified, () => SyntheticProviderSelector.Select(
            m, SelectionMode.Manual, c.Identity, [p, q], Challenge, t, Now));
        t.Consents.Remove(remote.Identity);
        Denied(ProviderError.ConsentRequired, () => SyntheticProviderSelector.Select(
            m, SelectionMode.Auto, null, [p, q], Challenge, t, Now));
        t.Add(m, remote);
        Assert.Same(q, SyntheticProviderSelector.Select(m, SelectionMode.Auto, null, [p, q], Challenge, t, Now));
        Denied(ProviderError.ConsentRequired, () => SyntheticProviderSelector.Select(
            m, SelectionMode.Manual, null, [p, q], Challenge, t, Now));
    }

    [Fact]
    public void CrossRegionEnvironmentAndProviderNeedExactConsent()
    {
        var (m, t, c, _) = Setup();
        var other = c with { Identity = c.Identity with { InstanceId = "other" }, Environment = "remote" };
        IExecutionProvider p = new RemoteOfflineFakeProvider(other, t, () => Now);
        t.Proofs[other.Identity] = t.Proofs[c.Identity] with
        {
            Identity = other.Identity, CapabilitiesDigest = SyntheticAdmission.CapabilitiesDigest(other)
        };
        Denied(ProviderError.ConsentRequired, () => p.Preflight(m, Challenge));
        t.Add(m, other);
        var crossRegion = other with { Region = "region-b" };
        t.Add(m, crossRegion);
        p = new RemoteOfflineFakeProvider(crossRegion, t, () => Now);
        Denied(ProviderError.RegionNotAllowed, () => p.Preflight(m, Challenge));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("digest")]
    [InlineData("capabilities")]
    [InlineData("nonce")]
    [InlineData("version")]
    [InlineData("expired")]
    [InlineData("future")]
    public void BadProofNeverAuthorizes(string mutation)
    {
        var (m, t, c, p) = Setup();
        var proof = t.Proofs[c.Identity];
        switch (mutation)
        {
            case "missing": t.Proofs.Remove(c.Identity); break;
            case "forged": t.Proofs[c.Identity] = proof with { TrustedFixture = false }; break;
            case "digest": t.Proofs[c.Identity] = proof with { ManifestDigest = new string('0', 64) }; break;
            case "capabilities": t.Proofs[c.Identity] = proof with { CapabilitiesDigest = new string('0', 64) }; break;
            case "nonce": t.Proofs[c.Identity] = proof with { Challenge = "old" }; break;
            case "version": t.Proofs[c.Identity] = proof with { Version = "hic.execution-attestation/1" }; break;
            case "expired": t.Proofs[c.Identity] = proof with { ExpiresAt = Now }; break;
            case "future": t.Proofs[c.Identity] = proof with { ObservedAt = Now.AddSeconds(1) }; break;
        }
        Denied(mutation is "expired" or "future" ? ProviderError.AttestationStale :
            ProviderError.AttestationUnverified, () => p.Preflight(m, Challenge));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("source")]
    [InlineData("revision")]
    [InlineData("actor")]
    [InlineData("claim")]
    [InlineData("tenant")]
    [InlineData("expired")]
    public void ChangedManifestCannotAssertAuthority(string mutation)
    {
        var (m, _, _, p) = Setup();
        m = mutation switch
        {
            "base" => m with { BaseSha = new string('c', 40) },
            "source" => m with { SourceDigest = new string('d', 64) },
            "revision" => m with { TaskRevision = 2 },
            "actor" => m with { ActorId = "other" },
            "claim" => m with { ClaimGeneration = 2 },
            "tenant" => m with { TenantId = "tenant-b" },
            _ => m with { ExpiresAt = Now }
        };
        Denied(ProviderError.AuthorityUnverified, () => p.Prepare(m, Challenge));
    }

    [Fact]
    public void RevocationFencesStartButAllowsScopedCancellation()
    {
        var (m, t, _, p) = Setup();
        var h = p.Prepare(m, Challenge);
        t.Authority = t.Authority! with { Revoked = true };
        Denied(ProviderError.ApprovalRevoked, () => p.Start(m, h, Challenge));
        Assert.Equal(FakeStatus.Cancelled, p.Cancel(m, h).Status);
        Assert.Equal(FakeStatus.Cleaned, p.Cleanup(m, h).Status);
    }

    [Theory]
    [InlineData("quota", ProviderError.QuotaUnavailable)]
    [InlineData("cost", ProviderError.CostNotAllowed)]
    [InlineData("unknown", ProviderError.CostNotAllowed)]
    [InlineData("arch", ProviderError.CapabilityMismatch)]
    [InlineData("cpu", ProviderError.CapabilityMismatch)]
    [InlineData("memory", ProviderError.CapabilityMismatch)]
    [InlineData("pids", ProviderError.CapabilityMismatch)]
    [InlineData("network", ProviderError.CapabilityMismatch)]
    [InlineData("version", ProviderError.ProtocolMismatch)]
    public void IneligibleCapabilitiesFailClosed(string mutation, ProviderError error)
    {
        var (m, t, c, _) = Setup();
        c = mutation switch
        {
            "quota" => c with { QuotaAvailable = false },
            "cost" => c with { EstimatedCharge = 1m },
            "unknown" => c with { EstimatedCharge = null },
            "arch" => c with { Architecture = "arm64" },
            "cpu" => c with { CpuMillicores = 499 },
            "memory" => c with { MemoryMiB = 255 },
            "pids" => c with { PidsMax = 63 },
            "network" => c with { NetworkPolicy = "allow-all" },
            _ => c with { Version = "hic.execution-capabilities/1" }
        };
        t.Add(m, c);
        if (mutation == "cost") t.Consents[c.Identity] = t.Consents[c.Identity] with { MaxCharge = 1m };
        var p = new LocalOfflineFakeProvider(c, t, () => Now);
        Denied(error, () => p.Preflight(m, Challenge));
    }

    [Fact]
    public void HandleDoesNotGrantAuthorityAndCleanupRequiresOwnership()
    {
        var (m, t, _, p) = Setup();
        var h = p.Prepare(m, Challenge);
        Denied(ProviderError.CleanupUnverified, () => p.Cleanup(m, h));
        Denied(ProviderError.Conflict, () => p.Cancel(m, h with { ResourceId = "fake" }));
        t.Authority = null;
        Denied(ProviderError.AuthorityUnverified, () => p.Cancel(m, h));
    }

    [Fact]
    public void DuplicateKeyOrChangedManifestConflicts()
    {
        var (m, t, c, p) = Setup();
        p.Prepare(m, Challenge);
        var changed = m with { SourceDigest = new string('c', 64) };
        t.SetAuthority(changed);
        t.Add(changed, c);
        Denied(ProviderError.Conflict, () => p.Prepare(changed, Challenge));
        var other = m with { AttemptId = Guid.Parse("55555555-5555-5555-5555-555555555555") };
        t.SetAuthority(other);
        t.Add(other, c);
        Denied(ProviderError.Conflict, () => p.Prepare(other, Challenge));
    }

    [Fact]
    public void CrossTenantHandleCannotAccessOtherTenant()
    {
        var (m, t, c, p) = Setup();
        var h = p.Prepare(m, Challenge);
        var other = m with { TenantId = "tenant-b" };
        t.SetAuthority(other);
        Denied(ProviderError.NotFound, () => p.Cancel(other, h));
        Denied(ProviderError.AuthorityUnverified, () => p.Preflight(other, Challenge));
    }

    [Fact]
    public async Task ConcurrentCancellationFencesAllSubsequentStarts()
    {
        var (m, _, _, p) = Setup();
        var h = p.Prepare(m, Challenge);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) p.Cancel(m, h);
            else
            {
                try { p.Start(m, h, Challenge); }
                catch (ProviderException e) when (e.Code == ProviderError.Cancelled) { }
            }
        })));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            Denied(ProviderError.Cancelled, () => p.Start(m, h, Challenge)))));
        Assert.Equal(FakeStatus.Cancelled, p.Inspect(m, h).Status);
    }

    [Fact]
    public async Task ConcurrentPrepareAndCancelCannotRecreateCancelledHandle()
    {
        var (m, _, _, p) = Setup();
        var h = p.Prepare(m, Challenge);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) p.Cancel(m, h);
            else
            {
                try { Assert.Equal(h, p.Prepare(m, Challenge)); }
                catch (ProviderException e) when (e.Code == ProviderError.Cancelled) { }
            }
        })));
        Denied(ProviderError.Cancelled, () => p.Prepare(m, Challenge));
    }

    [Fact]
    public void DuplicateProviderIdentityIsRejectedBeforeSelection()
    {
        var (m, t, c, p) = Setup();
        IExecutionProvider duplicate = new RemoteOfflineFakeProvider(c, t, () => Now);
        Denied(ProviderError.Conflict, () => SyntheticProviderSelector.Select(
            m, SelectionMode.Auto, null, [p, duplicate], Challenge, t, Now));
    }

    [Fact]
    public void StaleAuthorityAndProofAreRecheckedAtStart()
    {
        var (m, t, c, p) = Setup();
        var h = p.Prepare(m, Challenge);
        t.Authority = t.Authority! with { ClaimGeneration = 2 };
        Denied(ProviderError.AuthorityUnverified, () => p.Start(m, h, Challenge));
        t.SetAuthority(m);
        t.Proofs[c.Identity] = t.Proofs[c.Identity] with { ExpiresAt = Now };
        Denied(ProviderError.AttestationStale, () => p.Start(m, h, Challenge));
    }

    [Fact]
    public void InvalidManifestAndVersionFailClosed()
    {
        var (m, _, _, p) = Setup();
        Denied(ProviderError.ProtocolMismatch, () => p.Preflight(m with { Version = "hic.execution-request/1" }, Challenge));
        Denied(ProviderError.InvalidManifest, () => p.Preflight(m with { BaseSha = "HEAD" }, Challenge));
        Denied(ProviderError.InvalidManifest, () => p.Preflight(m with { NetworkPolicy = "allow-all" }, Challenge));
    }
}
