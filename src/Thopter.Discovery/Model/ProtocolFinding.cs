using System.Net;

namespace Thopter.Discovery.Model;

/// <summary>
/// One piece of evidence about a device at a given IP, produced by a protocol probe
/// (ONVIF / SSDP / mDNS / port scan). The engine merges these into the MAC-keyed
/// registry (by matching IP), then <c>DeviceIdentifier</c> fuses them into a verdict.
///
/// Everything here is unauthenticated, standard, one-shot data. No media, no auth, no
/// continuous observation - that is the open-core wall.
/// </summary>
public sealed class ProtocolFinding
{
    public required IPAddress Address { get; init; }
    public required DiscoverySource Source { get; init; }

    /// <summary>Vendor as advertised by the protocol (SSDP manufacturer, ONVIF name, etc.) - not the OUI vendor.</summary>
    public string? Vendor { get; set; }

    /// <summary>Model/hardware string from an unauthenticated field (ONVIF hardware scope, SSDP modelName, mDNS md=).</summary>
    public string? Model { get; set; }

    public string? Hostname { get; set; }

    /// <summary>ONVIF scopes (onvif://www.onvif.org/...), decoded.</summary>
    public List<string> Scopes { get; } = new();

    /// <summary>mDNS/DNS-SD service types seen for this host (e.g. _rtsp._tcp, _axis-video._tcp).</summary>
    public List<string> Services { get; } = new();

    /// <summary>Open TCP ports + light banners.</summary>
    public List<OpenPort> Ports { get; } = new();

    /// <summary>Free-form evidence bag (http.server, tls.cn, rtsp.server, ssdp.st, mdns.txt.md, ...).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Note { get; set; }
}
