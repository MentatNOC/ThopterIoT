using System.Net;
using System.Net.NetworkInformation;

namespace Thopter.Discovery.Model;

/// <summary>
/// One device found on the local network, accumulated across discovery sources.
/// The registry keys devices by MAC when known (<see cref="Key"/>); silent hosts
/// with no resolvable MAC are keyed by IP.
/// </summary>
public sealed class DiscoveredDevice
{
    private readonly List<IPAddress> _addresses = new();
    private readonly SortedSet<int> _ports = new();

    /// <summary>Stable registry key: <c>"mac:AABBCCDDEEFF"</c> when a MAC is known, else <c>"ip:&lt;addr&gt;"</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Layer-2 hardware address, when resolved. This is the primary identity when present.</summary>
    public PhysicalAddress? Mac { get; set; }

    /// <summary>IEEE OUI vendor for <see cref="Mac"/>, or null (unknown, or locally-administered/randomized).</summary>
    public string? Vendor { get; set; }

    /// <summary>True when the MAC's locally-administered bit is set (randomized / virtual — not a real OUI).</summary>
    public bool IsLocallyAdministered { get; set; }

    public DeviceType Type { get; set; } = DeviceType.Unknown;

    /// <summary>Best offline model string (ONVIF scope, SSDP modelName, mDNS TXT, etc.). Null until the protocol layer runs.</summary>
    public string? Model { get; set; }

    public string? Hostname { get; set; }

    public DiscoverySource Sources { get; set; } = DiscoverySource.None;

    /// <summary>Free-form notes (e.g. "randomized MAC"). Never contains credentials or media.</summary>
    public string? Note { get; set; }

    public IReadOnlyList<IPAddress> Addresses => _addresses;

    public IReadOnlyCollection<OpenPort> OpenPorts => _openPorts;
    private readonly List<OpenPort> _openPorts = new();

    /// <summary>First (lowest) IPv4 address, for display/sort. <see cref="IPAddress.None"/> if none.</summary>
    public IPAddress PrimaryAddress =>
        _addresses.Count == 0 ? IPAddress.None : _addresses[0];

    public void AddAddress(IPAddress address)
    {
        if (!_addresses.Contains(address))
        {
            _addresses.Add(address);
            _addresses.Sort(CompareAddresses);
        }
    }

    public void AddOpenPort(OpenPort port)
    {
        if (_ports.Add(port.Port))
            _openPorts.Add(port);
    }

    private static int CompareAddresses(IPAddress a, IPAddress b)
    {
        byte[] ab = a.GetAddressBytes(), bb = b.GetAddressBytes();
        if (ab.Length != bb.Length) return ab.Length.CompareTo(bb.Length);
        for (int i = 0; i < ab.Length; i++)
            if (ab[i] != bb[i]) return ab[i].CompareTo(bb[i]);
        return 0;
    }

    /// <summary>Format a MAC as <c>AA:BB:CC:DD:EE:FF</c>.</summary>
    public static string FormatMac(PhysicalAddress mac)
    {
        byte[] b = mac.GetAddressBytes();
        return string.Join(":", b.Select(x => x.ToString("X2")));
    }

    public string? MacString => Mac is null ? null : FormatMac(Mac);
}
