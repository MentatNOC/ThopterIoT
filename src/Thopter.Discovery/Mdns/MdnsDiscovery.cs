using System.Net;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.Mdns;

/// <summary>
/// mDNS / DNS-SD discovery probe (RFC 6762/6763, UDP 5353). Sends the DNS-SD service-
/// enumeration meta-query plus a fixed list of camera/device-relevant service-type PTR
/// queries - all marked QU (unicast-response requested) - and folds whatever comes back
/// into one <see cref="ProtocolFinding"/> per responding IP. Unauthenticated, one-shot,
/// no reflection, no external library: the wire format is hand-decoded by <see cref="DnsMessage"/>.
/// </summary>
public sealed class MdnsDiscovery
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    /// <summary>Owner name of the DNS-SD meta-query: "list every service type you advertise" (RFC 6763 §9).</summary>
    private const string MetaQueryName = "_services._dns-sd._udp.local";

    /// <summary>
    /// The meta-query plus a fixed set of camera/IoT-relevant DNS-SD service types. Kept to
    /// one small batch (well under the ~1400-byte practical UDP/mDNS payload ceiling).
    /// </summary>
    private static readonly string[] ServiceQueryNames =
    {
        MetaQueryName,
        "_rtsp._tcp.local",
        "_onvif._tcp.local",
        "_axis-video._tcp.local",
        "_dahua._tcp.local",
        "_http._tcp.local",
        "_https._tcp.local",
        "_ipp._tcp.local",
        "_hap._tcp.local",
    };

    /// <summary>TXT keys worth surfacing as attributes - kept short on purpose, not the whole packet.</summary>
    private static readonly string[] InterestingTxtKeys =
    {
        "md", "fn", "ty", "model", "vendor", "manufacturer", "product", "id", "usb_mdl", "usb_mfg",
    };

    /// <summary>
    /// Probe the LAN for mDNS/DNS-SD responders and return one finding per distinct IP that
    /// answered. Sends from every address in <paramref name="localAddresses"/> and collects
    /// unicast replies for <paramref name="window"/>; never throws on a malformed reply.
    /// </summary>
    public async Task<IReadOnlyList<ProtocolFinding>> DiscoverAsync(
        IReadOnlyList<IPAddress> localAddresses, TimeSpan window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(localAddresses);

        byte[] query = DnsMessage.BuildQuery(ServiceQueryNames.Select(n => (n, DnsType.PTR)));
        var payloads = new[] { query };

        var replies = await UdpProbe.SendAndCollectAsync(
            MulticastGroup, MdnsPort, payloads, window, localAddresses, ct).ConfigureAwait(false);

        var byAddress = new Dictionary<IPAddress, ProtocolFinding>();

        foreach (var reply in replies)
        {
            ct.ThrowIfCancellationRequested();

            if (!DnsMessage.TryParse(reply.Data, out var msg) || msg.Records.Count == 0)
                continue; // malformed / empty datagram - skip it, not the whole probe

            if (!byAddress.TryGetValue(reply.From, out var finding))
            {
                finding = new ProtocolFinding { Address = reply.From, Source = DiscoverySource.Mdns };
                byAddress[reply.From] = finding;
            }

            Merge(finding, msg);
        }

        return byAddress.Values.ToList();
    }

    /// <summary>Fold every record in one reply datagram into that responder's running finding.</summary>
    private static void Merge(ProtocolFinding finding, DnsMessage msg)
    {
        string? instanceName = null;

        foreach (var record in msg.Records)
        {
            switch (record.Type)
            {
                case DnsType.PTR when record.PtrTarget is not null:
                    if (string.Equals(record.Name, MetaQueryName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Meta-query answer: the PTR target itself is a service type
                        // (e.g. "_rtsp._tcp.local"), not an instance.
                        AddService(finding, StripLocalSuffix(record.PtrTarget));
                    }
                    else
                    {
                        // Direct query answer: the record's own name is the service type we
                        // asked about, and the PTR target is a human-readable instance name
                        // (e.g. "Driveway Cam._rtsp._tcp.local").
                        AddService(finding, StripLocalSuffix(record.Name));
                        instanceName ??= StripLocalSuffix(record.PtrTarget);
                    }
                    break;

                case DnsType.SRV when record.Srv is { } srv:
                    // The SRV owner name is "<instance>._service._proto.local" - the service
                    // type is the last two labels before the domain.
                    AddService(finding, ServiceTypeFromOwnerName(record.Name));
                    if (finding.Hostname is null && !string.IsNullOrEmpty(srv.Target))
                        finding.Hostname = StripLocalSuffix(srv.Target);
                    break;

                case DnsType.A when record.Address is not null:
                    if (finding.Hostname is null)
                        finding.Hostname = StripLocalSuffix(record.Name);
                    break;

                case DnsType.TXT when record.Txt is { Count: > 0 }:
                    ApplyTxt(finding, record.Txt);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(instanceName) && !finding.Attributes.ContainsKey("mdns.instance"))
            finding.Attributes["mdns.instance"] = instanceName;
    }

    private static void AddService(ProtocolFinding finding, string? serviceType)
    {
        if (string.IsNullOrEmpty(serviceType)) return;
        if (!finding.Services.Contains(serviceType, StringComparer.OrdinalIgnoreCase))
            finding.Services.Add(serviceType);
    }

    private static void ApplyTxt(ProtocolFinding finding, IReadOnlyList<string> txt)
    {
        foreach (var entry in txt)
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue; // boolean flag string (no '=') or empty key - nothing to attribute
            string value = entry[(eq + 1)..];
            if (value.Length == 0) continue;

            string key = entry[..eq].ToLowerInvariant();
            if (Array.IndexOf(InterestingTxtKeys, key) < 0) continue;

            finding.Attributes["mdns.txt." + key] = value;
            if (key == "md" && finding.Model is null)
                finding.Model = value;
        }
    }

    /// <summary>
    /// Given an SRV/instance owner name "&lt;instance&gt;._service._proto.local", return
    /// "_service._proto". mDNS domains are always the single label "local", so the service
    /// type is reliably the two labels immediately before the last one.
    /// </summary>
    private static string? ServiceTypeFromOwnerName(string ownerName)
    {
        string[] labels = ownerName.Split('.');
        if (labels.Length < 3) return null;

        int protoIdx = labels.Length - 2;
        int serviceIdx = labels.Length - 3;
        if (!labels[serviceIdx].StartsWith('_') || !labels[protoIdx].StartsWith('_')) return null;

        return labels[serviceIdx] + "." + labels[protoIdx];
    }

    /// <summary>Strip a trailing ".local" domain suffix (case-insensitive); used for both hostnames and service types.</summary>
    private static string StripLocalSuffix(string name)
    {
        const string suffix = ".local";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
    }
}
