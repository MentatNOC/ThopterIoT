using System.Net;
using System.Text;
using System.Xml.Linq;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.Onvif;

/// <summary>
/// ONVIF WS-Discovery probe: a SOAP 1.2 Probe multicast to 239.255.255.250:3702, then
/// parse the ProbeMatch replies for XAddrs / Types / Scopes. Scopes are the prize -
/// vendor, model, name, and device type, all unauthenticated. We never call the device
/// back (no anonymous GetDeviceInformation) - WS-Discovery scopes only.
/// </summary>
public sealed class WsDiscovery
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int Port = 3702;

    public async Task<IReadOnlyList<ProtocolFinding>> DiscoverAsync(
        IReadOnlyList<IPAddress> localAddresses, TimeSpan window, CancellationToken ct)
    {
        // Two probes: cameras (NetworkVideoTransmitter) and a match-all for NVRs/other devices.
        var payloads = new List<byte[]>
        {
            BuildProbe("dn:NetworkVideoTransmitter"),
            BuildProbe(null),
        };

        var replies = await UdpProbe.SendAndCollectAsync(Group, Port, payloads, window, localAddresses, ct)
            .ConfigureAwait(false);

        var byIp = new Dictionary<IPAddress, ProtocolFinding>();
        foreach (var reply in replies)
        {
            ProtocolFinding finding = GetOrCreate(byIp, reply.From);
            try { ParseProbeMatches(reply.Data, finding); }
            catch { /* malformed SOAP - keep whatever we already extracted */ }
        }

        // Only surface genuine ONVIF responders. WSD printers/scanners also answer on 3702;
        // they get dropped here and are still found via mDNS/ARP/port scan.
        return byIp.Values.Where(f => f.Attributes.ContainsKey("onvif.present")).ToList();
    }

    private static ProtocolFinding GetOrCreate(Dictionary<IPAddress, ProtocolFinding> map, IPAddress ip)
    {
        if (!map.TryGetValue(ip, out var f))
        {
            f = new ProtocolFinding { Address = ip, Source = DiscoverySource.Onvif };
            map[ip] = f;
        }
        return f;
    }

    private static byte[] BuildProbe(string? types)
    {
        string messageId = "urn:uuid:" + Guid.NewGuid().ToString();
        string typesElement = types is null ? "" : $"<d:Types>{types}</d:Types>";

        string soap =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\"" +
            " xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\"" +
            " xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\"" +
            " xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
            "<e:Header>" +
            $"<w:MessageID>{messageId}</w:MessageID>" +
            "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
            "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
            "</e:Header>" +
            "<e:Body><d:Probe>" + typesElement + "</d:Probe></e:Body>" +
            "</e:Envelope>";

        return Encoding.UTF8.GetBytes(soap);
    }

    internal static void ParseProbeMatches(byte[] data, ProtocolFinding finding)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(data));
        bool isOnvif = false;

        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            string? xaddrs = Local(match, "XAddrs");
            string? scopes = Local(match, "Scopes");
            string? types = Local(match, "Types");

            // WS-Discovery (UDP 3702) is shared by ONVIF cameras and Microsoft WSD
            // printers/scanners. Treat it as ONVIF only on an ONVIF video type or an
            // onvif.org scope - never on a bare WSD/print response.
            if (!string.IsNullOrWhiteSpace(types) && ContainsOnvifVideoType(types!))
                isOnvif = true;

            if (!string.IsNullOrWhiteSpace(scopes))
            {
                foreach (var raw in scopes!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string scope = Uri.UnescapeDataString(raw);
                    if (!scope.Contains("onvif.org", StringComparison.OrdinalIgnoreCase)) continue;
                    isOnvif = true;
                    finding.Scopes.Add(scope);
                    ApplyScope(scope, finding);
                }
            }

            if (isOnvif)
            {
                if (!string.IsNullOrWhiteSpace(xaddrs))
                    finding.Attributes["onvif.xaddr"] = xaddrs!.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? xaddrs!;
                if (!string.IsNullOrWhiteSpace(types))
                    finding.Attributes["onvif.types"] = types!.Trim();
            }
        }

        if (isOnvif)
            finding.Attributes["onvif.present"] = "true";
    }

    private static bool ContainsOnvifVideoType(string types) =>
        types.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase) ||
        types.Contains("NetworkVideoDisplay", StringComparison.OrdinalIgnoreCase) ||
        types.Contains("NetworkVideoStorage", StringComparison.OrdinalIgnoreCase);

    private static void ApplyScope(string scope, ProtocolFinding finding)
    {
        // onvif://www.onvif.org/<key>/<value>
        int marker = scope.IndexOf("onvif.org/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return;
        string tail = scope[(marker + "onvif.org/".Length)..];
        int slash = tail.IndexOf('/');
        if (slash < 0) return;

        string key = tail[..slash].ToLowerInvariant();
        string value = tail[(slash + 1)..].Trim();
        if (value.Length == 0) return;

        switch (key)
        {
            case "name":
                finding.Hostname ??= value;
                finding.Attributes["onvif.name"] = value;
                break;
            case "hardware":
                finding.Model ??= value;
                finding.Attributes["onvif.hardware"] = value;
                break;
            case "manufacturer":
                finding.Vendor ??= value;
                break;
            case "location":
                finding.Attributes["onvif.location"] = value;
                break;
            case "type":
                finding.Attributes["onvif.type"] = value;
                break;
        }
    }

    private static string? Local(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
