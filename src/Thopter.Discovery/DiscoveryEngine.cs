using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Thopter.Discovery.Arp;
using Thopter.Discovery.Model;
using Thopter.Discovery.Oui;

namespace Thopter.Discovery;

/// <summary>
/// Orchestrates a one-shot local-network scan. This is the only public entry point:
/// <see cref="ScanAsync"/>. It is deliberately one-shot — no daemon, no re-scan loop
/// (continuous observation is monitoring, which stays server-side).
///
/// MVP (step 1): driver-free ARP sweep + IEEE OUI vendor enrichment.
/// The protocol layer (ONVIF/SSDP/mDNS/port scan) plugs into the same registry in step 2.
/// </summary>
public sealed class DiscoveryEngine
{
    private readonly OuiDatabase _oui;

    public DiscoveryEngine(OuiDatabase? oui = null)
    {
        _oui = oui ?? OuiDatabase.LoadEmbedded();
    }

    /// <summary>Number of OUI records loaded (diagnostics).</summary>
    public int OuiRecordCount => _oui.Count;

    /// <summary>
    /// Run a scan. Devices are reported via <paramref name="progress"/> as they are merged
    /// (live UI streaming) and also returned as a final ordered snapshot.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        DiscoveryOptions options,
        IProgress<DiscoveredDevice>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var registry = new ConcurrentDictionary<string, DiscoveredDevice>(StringComparer.Ordinal);

        var targets = options.ResolveTargets();
        if (targets.Count == 0)
            return Array.Empty<DiscoveredDevice>();

        // --- Stage A: driver-free IP↔MAC sweep ---
        var sweep = new ArpSweep();
        var ipToMac = await sweep.SweepAsync(targets, options.Arp, pinged: null, ct).ConfigureAwait(false);

        foreach (var (ip, mac) in ipToMac)
        {
            ct.ThrowIfCancellationRequested();
            var device = MergeArp(registry, ip, mac);
            progress?.Report(device);
        }

        // (Step 2 stages ONVIF/SSDP/mDNS/port scan merge into `registry` here.)

        return registry.Values
            .OrderBy(d => d.PrimaryAddress, IPAddressComparer.Instance)
            .ToList();
    }

    /// <summary>Merge one ARP result into the registry, enriching vendor from the OUI table.</summary>
    private DiscoveredDevice MergeArp(
        ConcurrentDictionary<string, DiscoveredDevice> registry, IPAddress ip, PhysicalAddress mac)
    {
        string key = "mac:" + Convert.ToHexString(mac.GetAddressBytes());
        var device = registry.GetOrAdd(key, k => new DiscoveredDevice { Key = k });

        lock (device)
        {
            device.Mac = mac;
            device.AddAddress(ip);
            device.Sources |= DiscoverySource.Arp | DiscoverySource.Ping;

            if (device.Vendor is null && !device.IsLocallyAdministered)
            {
                var lookup = _oui.Lookup(mac);
                device.Vendor = lookup.Vendor;
                device.IsLocallyAdministered = lookup.IsLocallyAdministered;
                if (lookup.Note is not null) device.Note = lookup.Note;
            }
        }

        return device;
    }

    private sealed class IPAddressComparer : IComparer<IPAddress>
    {
        public static readonly IPAddressComparer Instance = new();

        public int Compare(IPAddress? x, IPAddress? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            byte[] a = x.GetAddressBytes(), b = y.GetAddressBytes();
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }
    }
}
