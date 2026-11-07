using Xunit;

public sealed class NativeRunAdmissionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static ProposedNativeRun ValidRequest() => new(
        Guid.NewGuid(), Guid.NewGuid(), 3,
        NativeRunAdmissionPolicy.ReadOnlyAction, NativeRunAdmissionPolicy.ReadOnlyScope, null);

    private static VerifiedNativeApproval ValidApproval(ProposedNativeRun request) => new(
        request.TaskId, request.RunId, request.TaskVersion, request.Action, request.Scope,
        Guid.NewGuid(), Now.AddSeconds(-20), Now.AddSeconds(100),
        ActorAuthenticated: true, CryptographicProofVerified: true,
        DurableApprovalRecorded: true, ExclusiveRunClaimRecorded: true);

    [Fact]
    public void ValidSyntheticReadOnlyIntent_IsOnlyStructurallyEligible()
    {
        var request = ValidRequest();
        var decision = NativeRunAdmissionPolicy.Evaluate(request, ValidApproval(request), Now);
        Assert.True(decision.Eligible);
        Assert.Equal("structurally-eligible-read-only", decision.Code);
    }

    [Fact]
    public void MissingRequestOrApproval_IsDenied()
    {
        var request = ValidRequest();
        Assert.False(NativeRunAdmissionPolicy.Evaluate(null, ValidApproval(request), Now).Eligible);
        Assert.False(NativeRunAdmissionPolicy.Evaluate(request, null, Now).Eligible);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("run")]
    [InlineData("version")]
    [InlineData("action")]
    [InlineData("scope")]
    [InlineData("args")]
    [InlineData("blankArgs")]
    [InlineData("case")]
    public void MalformedOrDangerousIntent_IsDenied(string modification)
    {
        var request = ValidRequest();
        request = modification switch
        {
            "task" => request with { TaskId = Guid.Empty },
            "run" => request with { RunId = Guid.Empty },
            "version" => request with { TaskVersion = 0 },
            "action" => request with { Action = "shell.run" },
            "scope" => request with { Scope = "workspace:*:write" },
            "args" => request with { Arguments = "; rm -rf ." },
            "blankArgs" => request with { Arguments = "" },
            "case" => request with { Action = "WORKSPACE.INSPECT" },
            _ => throw new ArgumentOutOfRangeException(nameof(modification))
        };
        Assert.False(NativeRunAdmissionPolicy.Evaluate(request, ValidApproval(request), Now).Eligible);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("identity")]
    [InlineData("signature")]
    [InlineData("durability")]
    [InlineData("claim")]
    public void MissingTrustedEvidence_IsDenied(string modification)
    {
        var request = ValidRequest();
        var proof = ValidApproval(request);
        proof = modification switch
        {
            "actor" => proof with { ActorId = Guid.Empty },
            "identity" => proof with { ActorAuthenticated = false },
            "signature" => proof with { CryptographicProofVerified = false },
            "durability" => proof with { DurableApprovalRecorded = false },
            "claim" => proof with { ExclusiveRunClaimRecorded = false },
            _ => throw new ArgumentOutOfRangeException(nameof(modification))
        };
        Assert.False(NativeRunAdmissionPolicy.Evaluate(request, proof, Now).Eligible);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("run")]
    [InlineData("version")]
    [InlineData("action")]
    [InlineData("scope")]
    public void StaleOrMismatchedApproval_IsDenied(string modification)
    {
        var request = ValidRequest();
        var proof = ValidApproval(request);
        proof = modification switch
        {
            "task" => proof with { TaskId = Guid.NewGuid() },
            "run" => proof with { RunId = Guid.NewGuid() },
            "version" => proof with { TaskVersion = request.TaskVersion - 1 },
            "action" => proof with { Action = "system.health" },
            "scope" => proof with { Scope = "other-workspace:read" },
            _ => throw new ArgumentOutOfRangeException(nameof(modification))
        };
        var decision = NativeRunAdmissionPolicy.Evaluate(request, proof, Now);
        Assert.False(decision.Eligible);
        Assert.Equal("approval-binding-mismatch", decision.Code);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("zero")]
    [InlineData("long")]
    [InlineData("reverse")]
    public void InvalidLifetime_IsDenied(string modification)
    {
        var request = ValidRequest();
        var proof = ValidApproval(request);
        proof = modification switch
        {
            "expired" => proof with { ExpiresUtc = Now },
            "future" => proof with { ApprovedUtc = Now.AddSeconds(1), ExpiresUtc = Now.AddMinutes(2) },
            "zero" => proof with { ExpiresUtc = proof.ApprovedUtc },
            "long" => proof with { ExpiresUtc = proof.ApprovedUtc.AddMinutes(6) },
            "reverse" => proof with { ExpiresUtc = proof.ApprovedUtc.AddSeconds(-1) },
            _ => throw new ArgumentOutOfRangeException(nameof(modification))
        };
        Assert.False(NativeRunAdmissionPolicy.Evaluate(request, proof, Now).Eligible);
    }

    [Fact]
    public void ReplayCannotBeClaimedTwiceWithoutNewDurableAuthority()
    {
        var request = ValidRequest();
        var approval = ValidApproval(request);
        Assert.True(NativeRunAdmissionPolicy.Evaluate(request, approval, Now).Eligible);
        // This pure policy does not itself record a replay claim. An actual runner MUST
        // atomically mark the durable claim consumed before it can execute even once.
        Assert.False(NativeRunAdmissionPolicy.Evaluate(
            request, approval with { ExclusiveRunClaimRecorded = false }, Now).Eligible);
    }
}
