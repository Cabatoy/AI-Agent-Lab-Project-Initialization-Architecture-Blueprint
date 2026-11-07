// PURE, UNWIRED runner-admission contract. This file does not expose an endpoint,
// mint a grant, change a task status, start a process, or authorize the existing Worker.
// Never build VerifiedNativeApproval from client-supplied data; it is trusted evidence
// that a future authenticated Control Plane must provide from durable storage.
internal sealed record ProposedNativeRun(
    Guid TaskId,
    Guid RunId,
    long TaskVersion,
    string Action,
    string Scope,
    string? Arguments);

internal sealed record VerifiedNativeApproval(
    Guid TaskId,
    Guid RunId,
    long TaskVersion,
    string Action,
    string Scope,
    Guid ActorId,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset ExpiresUtc,
    bool ActorAuthenticated,
    bool CryptographicProofVerified,
    bool DurableApprovalRecorded,
    bool ExclusiveRunClaimRecorded);

internal sealed record NativeAdmissionDecision(bool Eligible, string Code);

internal static class NativeRunAdmissionPolicy
{
    // Only a deterministic, read-only operation is structurally eligible in v1.
    // This is NOT an execution grant. Caller must implement its own authenticated
    // authority, real durable single-use claim, audit, expiry and process isolation.
    internal const string ReadOnlyAction = "workspace.inspect";
    internal const string ReadOnlyScope = "workspace:configured:read";
    private static readonly TimeSpan MaxGrantLifetime = TimeSpan.FromMinutes(5);

    internal static NativeAdmissionDecision Evaluate(
        ProposedNativeRun? request,
        VerifiedNativeApproval? approval,
        DateTimeOffset now)
    {
        if (request is null || request.TaskId == Guid.Empty || request.RunId == Guid.Empty ||
            request.TaskVersion < 1 || request.Arguments is not null ||
            !string.Equals(request.Action, ReadOnlyAction, StringComparison.Ordinal) ||
            !string.Equals(request.Scope, ReadOnlyScope, StringComparison.Ordinal))
            return new(false, "invalid-read-only-intent");

        if (approval is null || approval.ActorId == Guid.Empty || !approval.ActorAuthenticated ||
            !approval.CryptographicProofVerified || !approval.DurableApprovalRecorded)
            return new(false, "approval-unverified");

        if (approval.TaskId != request.TaskId || approval.RunId != request.RunId ||
            approval.TaskVersion != request.TaskVersion ||
            !string.Equals(approval.Action, request.Action, StringComparison.Ordinal) ||
            !string.Equals(approval.Scope, request.Scope, StringComparison.Ordinal))
            return new(false, "approval-binding-mismatch");

        if (approval.ApprovedUtc == default || approval.ExpiresUtc <= approval.ApprovedUtc ||
            approval.ExpiresUtc - approval.ApprovedUtc > MaxGrantLifetime ||
            now < approval.ApprovedUtc || now >= approval.ExpiresUtc)
            return new(false, "approval-expired-or-invalid");

        if (!approval.ExclusiveRunClaimRecorded)
            return new(false, "exclusive-run-claim-required");

        // Advisory result ONLY. No operation is executed here. A future runner
        // must validate authoritative task state and issue a durable atomic claim.
        return new(true, "structurally-eligible-read-only");
    }
}
