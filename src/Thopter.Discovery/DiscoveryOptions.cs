using System.Net;
using Thopter.Discovery.Arp;
using Thopter.Discovery.Net;

namespace Thopter.Discovery;

/// <summary>
/// Inputs for a scan. Leave <see cref="Interface"/> and <see cref="Cidrs"/> null/empty
/// to auto-select the primary LAN interface and sweep its own subnet.
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>Explicit interface to scan. When null, the engine auto-selects the primary NIC.</summary>
    public NetworkInterfaceInfo? Interface { get; init; }

    /// <summary>Explicit CIDR targets (e.g. "192.168.1.0/24"). Overrides <see cref="Interface"/> when non-empty.</summary>
    public IReadOnlyList<string> Cidrs { get; init; } = Array.Empty<string>();

    /// <summary>Safety cap on hosts enumerated per subnet.</summary>
    public int MaxHostsPerSubnet { get; init; } = 8192;

    public ArpSweepOptions Arp { get; init; } = new();

    // --- Protocol-layer toggles (wired up in step 2; present now so the API is stable) ---
    public bool EnableOnvif { get; init; } = true;
    public bool EnableSsdp { get; init; } = true;
    public bool EnableMdns { get; init; } = true;
    public bool EnablePortScan { get; init; } = true;

    /// <summary>
    /// Resolve the concrete list of host IPs to sweep from the options.
    /// </summary>
    public IReadOnlyList<IPAddress> ResolveTargets()
    {
        var targets = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        if (Cidrs.Count > 0)
        {
            foreach (var cidr in Cidrs)
            {
                if (!TryParseCidr(cidr, out var addr, out var prefix)) continue;
                foreach (var ip in NetInfo.EnumerateHosts(addr, prefix, MaxHostsPerSubnet))
                    if (seen.Add(ip)) targets.Add(ip);
            }
            return targets;
        }

        var nic = Interface ?? NetInfo.GetPrimaryInterface();
        if (nic is not null)
        {
            foreach (var ip in NetInfo.EnumerateHosts(nic.HostAddress, nic.PrefixLength, MaxHostsPerSubnet))
                if (seen.Add(ip)) targets.Add(ip);
        }

        return targets;
    }

    private static bool TryParseCidr(string cidr, out IPAddress address, out int prefix)
    {
        address = IPAddress.None;
        prefix = 0;
        if (string.IsNullOrWhiteSpace(cidr)) return false;

        int slash = cidr.IndexOf('/');
        string ipPart = slash < 0 ? cidr : cidr[..slash];
        if (!IPAddress.TryParse(ipPart.Trim(), out var parsed)) return false;
        address = parsed;

        if (slash < 0) { prefix = 32; return true; }
        return int.TryParse(cidr.AsSpan(slash + 1), out prefix) && prefix is >= 0 and <= 32;
    }
}
