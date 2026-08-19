using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Thopter.Discovery.Net;

/// <summary>
/// Snapshot of one active IPv4 interface and its subnet.
/// </summary>
public sealed class NetworkInterfaceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required NetworkInterfaceType InterfaceType { get; init; }
    public required IPAddress HostAddress { get; init; }
    public required int PrefixLength { get; init; }
    public PhysicalAddress? HostMac { get; init; }
    public IPAddress? Gateway { get; init; }

    public IPAddress NetworkAddress => NetInfo.NetworkAddressOf(HostAddress, PrefixLength);

    /// <summary>Number of usable host addresses in the subnet (excludes network + broadcast for prefix &lt;= 30).</summary>
    public long UsableHostCount
    {
        get
        {
            long total = 1L << (32 - PrefixLength);
            return PrefixLength >= 31 ? total : Math.Max(0, total - 2);
        }
    }

    public override string ToString() => $"{Name} ({HostAddress}/{PrefixLength})";
}

/// <summary>
/// Enumerates local interfaces and computes subnet host ranges. Pure BCL, AOT-safe.
/// </summary>
public static class NetInfo
{
    /// <summary>
    /// Active, non-loopback, non-tunnel interfaces that have a usable IPv4 unicast address.
    /// </summary>
    public static IReadOnlyList<NetworkInterfaceInfo> GetActiveIPv4Interfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            PhysicalAddress? mac = nic.GetPhysicalAddress();
            if (mac is not null && mac.GetAddressBytes().Length == 0) mac = null;

            IPAddress? gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                     && !a.Equals(IPAddress.Any));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                int prefix = ua.PrefixLength;
                if (prefix is <= 0 or > 32) prefix = 24; // APIPA/legacy fallback

                // Skip link-local 169.254/16 auto-config addresses.
                byte[] b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;

                result.Add(new NetworkInterfaceInfo
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    InterfaceType = nic.NetworkInterfaceType,
                    HostAddress = ua.Address,
                    PrefixLength = prefix,
                    HostMac = mac,
                    Gateway = gateway,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Pick the most likely "primary" LAN interface: one with a default gateway and a
    /// private-range address, preferring wired/wireless over virtual adapters.
    /// </summary>
    public static NetworkInterfaceInfo? GetPrimaryInterface()
    {
        var candidates = GetActiveIPv4Interfaces();

        return candidates
            .OrderByDescending(i => i.Gateway is not null)          // has a default route
            .ThenByDescending(i => IsPrivate(i.HostAddress))        // RFC1918
            .ThenByDescending(i => PreferenceScore(i.InterfaceType)) // real NIC over virtual
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Enumerate usable host addresses in a subnet, capped at <paramref name="maxHosts"/>
    /// so a mistakenly-wide prefix (e.g. /8) can't spawn millions of pings.
    /// </summary>
    public static IEnumerable<IPAddress> EnumerateHosts(IPAddress address, int prefixLength, int maxHosts = 8192)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) yield break;
        if (prefixLength is < 0 or > 32) yield break;

        uint ip = ToUInt(address);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        uint network = ip & mask;
        uint broadcast = network | ~mask;

        uint first, last;
        if (prefixLength >= 31) { first = network; last = broadcast; }
        else { first = network + 1; last = broadcast - 1; }

        int count = 0;
        for (uint h = first; ; h++)
        {
            if (count++ >= maxHosts) yield break;
            yield return FromUInt(h);
            if (h == last) yield break; // guard uint wrap when last == uint.MaxValue
        }
    }

    public static IPAddress NetworkAddressOf(IPAddress address, int prefixLength)
    {
        uint ip = ToUInt(address);
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return FromUInt(ip & mask);
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] switch
        {
            10 => true,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            _ => false,
        };
    }

    internal static uint ToUInt(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    internal static IPAddress FromUInt(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return new IPAddress(b);
    }

    private static int PreferenceScore(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => 3,
        NetworkInterfaceType.GigabitEthernet => 3,
        NetworkInterfaceType.Wireless80211 => 2,
        _ => 0,
    };
}
