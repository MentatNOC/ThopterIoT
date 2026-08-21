using System.Net;
using Thopter.Discovery.Arp;
using Thopter.Discovery.Nbns;
using Thopter.Discovery.Net;
using Thopter.Discovery.PortScan;

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

    /// <summary>How long each multicast protocol probe listens for replies.</summary>
    public int ProtocolWindowMs { get; init; } = 2500;

    public TcpScanOptions PortScan { get; init; } = new();

    public NbnsOptions Nbns { get; init; } = new();

    /// <summary>
    /// Port-scan every enumerated target, not just hosts that answered ICMP/multicast.
    /// Needed to catch ping-silent cameras and to scan routed subnets at all. Default on.
    /// </summary>
    public bool PortScanAllTargets { get; init; } = true;

    /// <summary>
    /// Report only devices whose address is inside the scanned target range. On a host with
    /// several NICs the OS neighbor table holds neighbors from every segment, so without this
    /// a scan of one subnet also lists the others' hosts. Default on; turn off to sweep every
    /// interface at once ("scan all networks").
    /// </summary>
    public bool RestrictToTargetSubnets { get; init; } = true;

    // --- Protocol-layer toggles ---
    public bool EnableOnvif { get; init; } = true;
    public bool EnableSsdp { get; init; } = true;
    public bool EnableMdns { get; init; } = true;
    public bool EnablePortScan { get; init; } = true;

    /// <summary>Resolve Windows machine names via a NetBIOS node status query for hosts still unnamed after protocol discovery.</summary>
    public bool EnableNbns { get; init; } = true;

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

    /// <summary>
    /// The subnet ranges this scan targets, as (network, mask) integer pairs. Scope is tested
    /// by mask so it stays correct on subnets wider than the per-subnet host-enumeration cap
    /// (<see cref="MaxHostsPerSubnet"/>) - the enumerated host list stops at that cap, so it
    /// must not double as the "is this address in my subnet" test.
    /// </summary>
    public IReadOnlyList<(uint Network, uint Mask)> ResolveTargetSubnets()
    {
        var subnets = new List<(uint, uint)>();

        if (Cidrs.Count > 0)
        {
            foreach (var cidr in Cidrs)
            {
                if (!TryParseCidr(cidr, out var addr, out var prefix)) continue;
                uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                subnets.Add((NetInfo.ToUInt(addr) & mask, mask));
            }
            return subnets;
        }

        var nic = Interface ?? NetInfo.GetPrimaryInterface();
        if (nic is not null)
        {
            uint mask = nic.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - nic.PrefixLength);
            subnets.Add((NetInfo.ToUInt(nic.HostAddress) & mask, mask));
        }

        return subnets;
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
