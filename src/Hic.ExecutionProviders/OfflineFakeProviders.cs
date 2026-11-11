using System.Security.Cryptography;
using System.Text;

namespace Hic.ExecutionProviders;

// State represents only simulated provider resources; it is not a task ledger.
public abstract class OfflineFakeProvider : IExecutionProvider
{
    private readonly object gate = new();
    private readonly Dictionary<(string Tenant, Guid Attempt), Entry> entries = new();
    private readonly Dictionary<(string Tenant, string Key), Guid> keys = new();
    private readonly ProviderCapabilities capabilities;
    private readonly ISyntheticTrustSource trust;
    private readonly Func<DateTimeOffset> clock;

    protected OfflineFakeProvider(ProviderCapabilities capabilities, ISyntheticTrustSource trust,
        Func<DateTimeOffset> clock)
    {
        this.capabilities = capabilities;
        this.trust = trust;
        this.clock = clock;
    }

    public ProviderCapabilities DescribeCapabilities() => capabilities;

    public void Preflight(ExecutionManifest manifest, string challenge) =>
        SyntheticAdmission.Check(manifest, capabilities, challenge, trust, clock());

    public FakeHandle Prepare(ExecutionManifest manifest, string challenge)
    {
        lock (gate)
        {
            Preflight(manifest, challenge);
            var attempt = (manifest.TenantId, manifest.AttemptId);
            var key = (manifest.TenantId, manifest.IdempotencyKey);
            if (keys.TryGetValue(key, out var existingAttempt) && existingAttempt != manifest.AttemptId)
                throw new ProviderException(ProviderError.Conflict);
            if (entries.TryGetValue(attempt, out var existing))
            {
                if (existing.Digest != manifest.Digest())
                    throw new ProviderException(ProviderError.Conflict);
                if (existing.Status is FakeStatus.Cancelled or FakeStatus.Cleaned)
                    throw new ProviderException(ProviderError.Cancelled);
                return existing.Handle;
            }
            var raw = $"{capabilities.Identity.TenantId}\n{capabilities.Identity.InstallationId}\n" +
                $"{capabilities.Identity.InstanceId}\n{manifest.AttemptId:D}\n{manifest.Digest()}";
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var handle = new FakeHandle(capabilities.Identity, manifest.AttemptId, id);
            entries.Add(attempt, new Entry(manifest.Digest(), handle));
            keys.Add(key, manifest.AttemptId);
            return handle;
        }
    }

    public FakeObservation Start(ExecutionManifest manifest, FakeHandle handle, string challenge)
    {
        lock (gate)
        {
            Preflight(manifest, challenge);
            var entry = Find(manifest, handle);
            if (entry.Status is FakeStatus.Cancelled or FakeStatus.Cleaned)
                throw new ProviderException(ProviderError.Cancelled);
            entry.Status = FakeStatus.Started;
            return Observe(entry);
        }
    }

    public FakeObservation Inspect(ExecutionManifest manifest, FakeHandle handle) =>
        Read(manifest, handle);

    public FakeObservation CollectEvidence(ExecutionManifest manifest, FakeHandle handle) =>
        Read(manifest, handle);

    public FakeObservation Cancel(ExecutionManifest manifest, FakeHandle handle)
    {
        lock (gate)
        {
            SyntheticAdmission.CheckAuthority(manifest, trust, clock(), allowRevoked: true);
            var entry = Find(manifest, handle);
            if (entry.Status != FakeStatus.Cleaned) entry.Status = FakeStatus.Cancelled;
            return Observe(entry);
        }
    }

    public FakeObservation Cleanup(ExecutionManifest manifest, FakeHandle handle)
    {
        lock (gate)
        {
            SyntheticAdmission.CheckAuthority(manifest, trust, clock(), allowRevoked: true);
            var entry = Find(manifest, handle);
            if (entry.Status is not FakeStatus.Cancelled and not FakeStatus.Cleaned)
                throw new ProviderException(ProviderError.CleanupUnverified);
            entry.Status = FakeStatus.Cleaned;
            return Observe(entry);
        }
    }

    private FakeObservation Read(ExecutionManifest manifest, FakeHandle handle)
    {
        lock (gate)
        {
            SyntheticAdmission.CheckAuthority(manifest, trust, clock());
            return Observe(Find(manifest, handle));
        }
    }

    private Entry Find(ExecutionManifest manifest, FakeHandle handle)
    {
        if (handle.Provider != capabilities.Identity || handle.AttemptId != manifest.AttemptId ||
            !entries.TryGetValue((manifest.TenantId, manifest.AttemptId), out var entry))
            throw new ProviderException(ProviderError.NotFound);
        if (entry.Handle != handle || entry.Digest != manifest.Digest())
            throw new ProviderException(ProviderError.Conflict);
        return entry;
    }

    private static FakeObservation Observe(Entry entry) => new(entry.Status, "OFFLINE_TESTED; OS_UNVERIFIED");

    private sealed class Entry(string digest, FakeHandle handle)
    {
        public string Digest { get; } = digest;
        public FakeHandle Handle { get; } = handle;
        public FakeStatus Status { get; set; } = FakeStatus.Prepared;
    }
}

public sealed class LocalOfflineFakeProvider(ProviderCapabilities capabilities, ISyntheticTrustSource trust,
    Func<DateTimeOffset> clock) : OfflineFakeProvider(capabilities, trust, clock);

public sealed class RemoteOfflineFakeProvider(ProviderCapabilities capabilities, ISyntheticTrustSource trust,
    Func<DateTimeOffset> clock) : OfflineFakeProvider(capabilities, trust, clock);
