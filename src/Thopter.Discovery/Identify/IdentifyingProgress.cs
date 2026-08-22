using Thopter.Discovery.Model;

namespace Thopter.Discovery.Identify;

/// <summary>
/// Progress decorator that runs <see cref="DeviceIdentifier.Identify"/> on a device
/// immediately before forwarding each report. Evidence accrues across the pipeline
/// stages, so without this every mid-scan report carries the default Unknown type
/// and the UI only learns "camera" at the final fusion pass, minutes late on a slow
/// sweep. Identify is offline, cheap, and idempotent (it recomputes from the evidence
/// on the device each time), so re-running it per report is safe. Reports are only
/// ever issued sequentially from the engine's continuation thread, never concurrently.
///
/// Banner-vendor enrichment is deliberately left to the fusion pass: running it here
/// would let a banner guess land before the neighbor-table backfill's OUI vendor and
/// then outrank it for good (see the parameter note on Identify).
/// </summary>
internal sealed class IdentifyingProgress : IProgress<DiscoveredDevice>
{
    private readonly IProgress<DiscoveredDevice> _inner;

    private IdentifyingProgress(IProgress<DiscoveredDevice> inner) => _inner = inner;

    /// <summary>Wrap a sink, preserving null (no observer means no reports to enrich).</summary>
    public static IProgress<DiscoveredDevice>? Wrap(IProgress<DiscoveredDevice>? inner)
        => inner is null ? null : new IdentifyingProgress(inner);

    public void Report(DiscoveredDevice value)
    {
        DeviceIdentifier.Identify(value, enrichVendorFromBanner: false);
        _inner.Report(value);
    }
}
