using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Thopter.Discovery.Arp;
using Thopter.Discovery.Identify;
using Thopter.Discovery.Mdns;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;
using Thopter.Discovery.Onvif;
using Thopter.Discovery.Oui;
using Thopter.Discovery.PortScan;
using Thopter.Discovery.Ssdp;

namespace Thopter.Discovery;

/// <summary>
/// Orchestrates a one-shot local-network scan. Deliberately one-shot — no daemon, no
/// re-scan loop (continuous observation is monitoring, which stays server-side).
///
/// Pipeline:
///   Stage A — driver-free ARP sweep (IP + MAC + OUI vendor).
///   Stage B — concurrent multicast probes (ONVIF WS-Discovery, SSDP, mDNS).
///   Stage C — TCP connect scan + light banners on every live/responding host.
///   Fusion  — offline, rule-based device type + model.
/// Everything is unauthenticated, standard, and LAN-only.
/// </summary>
public sealed class DiscoveryEngine
{
    private readonly OuiDatabase _oui;

    public DiscoveryEngine(OuiDatabase? oui = null)
    {
        _oui = oui ?? OuiDatabase.LoadEmbedded();
    }

    public int OuiRecordCount => _oui.Count;

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        DiscoveryOptions options,
        IProgress<DiscoveredDevice>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var registry = new Dictionary<string, DiscoveredDevice>(StringComparer.Ordinal);
        var ipIndex = new Dictionary<IPAddress, DiscoveredDevice>();

        var targets = options.ResolveTargets();
        if (targets.Count == 0)
            return Array.Empty<DiscoveredDevice>();

        // --- Stage A: driver-free IP↔MAC sweep + OUI vendor ---
        var ipToMac = await new ArpSweep()
            .SweepAsync(targets, options.Arp, pinged: null, ct)
            .ConfigureAwait(false);

        foreach (var (ip, mac) in ipToMac)
        {
            ct.ThrowIfCancellationRequested();
            var device = MergeArp(registry, ipIndex, ip, mac);
            progress?.Report(device);
        }

        // --- Stage B: concurrent multicast protocol probes ---
        var localAddresses = ResolveLocalAddresses(options);
        var window = TimeSpan.FromMilliseconds(options.ProtocolWindowMs);

        var probeTasks = new List<Task<IReadOnlyList<ProtocolFinding>>>();
        if (options.EnableOnvif) probeTasks.Add(new WsDiscovery().DiscoverAsync(localAddresses, window, ct));
        if (options.EnableSsdp) probeTasks.Add(new SsdpDiscovery().DiscoverAsync(localAddresses, window, ct));
        if (options.EnableMdns) probeTasks.Add(new MdnsDiscovery().DiscoverAsync(localAddresses, window, ct));

        if (probeTasks.Count > 0)
        {
            var broadcast = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            foreach (var findings in broadcast)
                foreach (var finding in findings)
                    ApplyFinding(MergeFinding(registry, ipIndex, finding), finding, progress);
        }

        // --- Stage C: TCP connect scan + banners ---
        if (options.EnablePortScan)
        {
            var scanTargets = (options.PortScanAllTargets ? targets : ipIndex.Keys.ToList())
                .Where(NetInfo.IsLanScannable)
                .Distinct()
                .ToList();

            if (scanTargets.Count > 0)
            {
                var portFindings = await new TcpScanner()
                    .ScanAsync(scanTargets, options.PortScan, ct)
                    .ConfigureAwait(false);

                foreach (var finding in portFindings)
                    ApplyFinding(MergeFinding(registry, ipIndex, finding), finding, progress);

                // The TCP connects just populated the OS neighbor table for on-segment hosts
                // that were silent to ICMP (many cameras block ping). Recover their MAC + OUI.
                BackfillMacsFromNeighborTable(registry, progress);
            }
        }

        // --- Fusion: offline device type + model ---
        foreach (var device in registry.Values)
        {
            ct.ThrowIfCancellationRequested();
            DeviceIdentifier.Identify(device);
            progress?.Report(device);
        }

        return registry.Values
            .OrderBy(d => d.PrimaryAddress, IPAddressComparer.Instance)
            .ToList();
    }

    private DiscoveredDevice MergeArp(
        Dictionary<string, DiscoveredDevice> registry,
        Dictionary<IPAddress, DiscoveredDevice> ipIndex,
        IPAddress ip, PhysicalAddress mac)
    {
        string key = "mac:" + Convert.ToHexString(mac.GetAddressBytes());
        if (!registry.TryGetValue(key, out var device))
        {
            device = new DiscoveredDevice { Key = key };
            registry[key] = device;
        }

        device.Mac = mac;
        device.AddAddress(ip);
        ipIndex[ip] = device;
        device.Sources |= DiscoverySource.Arp | DiscoverySource.Ping;

        if (device.Vendor is null && !device.IsLocallyAdministered)
        {
            var lookup = _oui.Lookup(mac);
            device.Vendor = lookup.Vendor;
            device.IsLocallyAdministered = lookup.IsLocallyAdministered;
            if (lookup.Note is not null) device.Note ??= lookup.Note;
        }

        return device;
    }

    /// <summary>
    /// After the port scan, re-read the neighbor table and attach MAC + OUI vendor to any
    /// device we still lack one for — TCP contact resolves ARP even for ping-silent hosts.
    /// Only helps for on-segment hosts (a routed host has no local neighbor entry).
    /// </summary>
    private void BackfillMacsFromNeighborTable(
        Dictionary<string, DiscoveredDevice> registry, IProgress<DiscoveredDevice>? progress)
    {
        var neighbors = ArpSweep.ReadNeighborTable();
        if (neighbors.Count == 0) return;

        foreach (var device in registry.Values)
        {
            if (device.Mac is not null) continue;
            foreach (var ip in device.Addresses)
            {
                if (!neighbors.TryGetValue(ip, out var mac)) continue;

                device.Mac = mac;
                device.Sources |= DiscoverySource.Arp;
                var lookup = _oui.Lookup(mac);
                if (device.Vendor is null && !lookup.IsLocallyAdministered) device.Vendor = lookup.Vendor;
                device.IsLocallyAdministered = lookup.IsLocallyAdministered;
                if (lookup.Note is not null) device.Note ??= lookup.Note;
                progress?.Report(device);
                break;
            }
        }
    }

    /// <summary>Locate the device for a protocol finding's IP, or create an IP-keyed device.</summary>
    private static DiscoveredDevice MergeFinding(
        Dictionary<string, DiscoveredDevice> registry,
        Dictionary<IPAddress, DiscoveredDevice> ipIndex,
        ProtocolFinding finding)
    {
        if (ipIndex.TryGetValue(finding.Address, out var device))
            return device;

        string key = "ip:" + finding.Address;
        if (!registry.TryGetValue(key, out device))
        {
            device = new DiscoveredDevice { Key = key };
            device.AddAddress(finding.Address);
            registry[key] = device;
        }
        ipIndex[finding.Address] = device;
        return device;
    }

    private static void ApplyFinding(DiscoveredDevice device, ProtocolFinding finding, IProgress<DiscoveredDevice>? progress)
    {
        device.Sources |= finding.Source;
        if (device.Vendor is null && finding.Vendor is not null) device.Vendor = finding.Vendor;
        if (finding.Model is not null) device.Model ??= finding.Model;
        if (finding.Hostname is not null) device.Hostname ??= finding.Hostname;

        foreach (var scope in finding.Scopes) device.AddOnvifScope(scope);
        foreach (var service in finding.Services) device.AddMdnsService(service);
        foreach (var port in finding.Ports) device.AddOpenPort(port);
        foreach (var kv in finding.Attributes) device.SetAttribute(kv.Key, kv.Value);
        if (finding.Note is not null) device.Note ??= finding.Note;

        progress?.Report(device);
    }

    private static IReadOnlyList<IPAddress> ResolveLocalAddresses(DiscoveryOptions options)
    {
        if (options.Interface is not null)
            return new[] { options.Interface.HostAddress };

        var addresses = NetInfo.GetActiveIPv4Interfaces()
            .Select(i => i.HostAddress)
            .Where(NetInfo.IsLanScannable)
            .Distinct()
            .ToList();

        return addresses.Count > 0 ? addresses : new List<IPAddress> { IPAddress.Any };
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
