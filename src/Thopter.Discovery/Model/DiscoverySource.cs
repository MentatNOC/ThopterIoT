namespace Thopter.Discovery.Model;

/// <summary>
/// Which discovery mechanism(s) surfaced a device. A device can be found by more
/// than one, so this is a flags enum and sources are OR'd together as evidence accrues.
/// </summary>
[Flags]
public enum DiscoverySource
{
    None = 0,

    /// <summary>Answered ICMP echo during the ping sweep.</summary>
    Ping = 1 << 0,

    /// <summary>Appeared in the OS neighbor (ARP) table with a MAC.</summary>
    Arp = 1 << 1,

    /// <summary>Responded to ONVIF WS-Discovery (UDP 3702).</summary>
    Onvif = 1 << 2,

    /// <summary>Responded to SSDP M-SEARCH (UDP 1900).</summary>
    Ssdp = 1 << 3,

    /// <summary>Responded to mDNS / DNS-SD (UDP 5353).</summary>
    Mdns = 1 << 4,

    /// <summary>Had at least one open TCP port during the connect scan.</summary>
    PortScan = 1 << 5,

    /// <summary>Answered a NetBIOS name-service node status query (UDP 137) with a machine name.</summary>
    Nbns = 1 << 6,
}
