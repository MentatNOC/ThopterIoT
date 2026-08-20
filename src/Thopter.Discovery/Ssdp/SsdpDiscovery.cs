using System.Net;
using System.Text;
using System.Xml.Linq;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.Ssdp;

/// <summary>
/// SSDP discovery: an HTTP-over-UDP M-SEARCH to 239.255.255.250:1900, dedupe replies by
/// source IP, and (optionally) GET the device's own advertised LOCATION description for
/// friendlyName / manufacturer / modelName. The LOCATION fetch goes only to LAN addresses
/// (HttpLite enforces that). We read only what the device advertises - never authenticate.
/// </summary>
public sealed class SsdpDiscovery
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.255.250");
    private const int Port = 1900;

    public bool FetchLocation { get; init; } = true;
    public int LocationMaxBytes { get; init; } = 1 << 20; // 1 MB cap
    public int LocationConcurrency { get; init; } = 16;

    public async Task<IReadOnlyList<ProtocolFinding>> DiscoverAsync(
        IReadOnlyList<IPAddress> localAddresses, TimeSpan window, CancellationToken ct)
    {
        var payloads = new List<byte[]>
        {
            BuildMSearch("ssdp:all"),
            BuildMSearch("upnp:rootdevice"),
        };

        var replies = await UdpProbe.SendAndCollectAsync(Group, Port, payloads, window, localAddresses, ct)
            .ConfigureAwait(false);

        var byIp = new Dictionary<IPAddress, ProtocolFinding>();
        var locations = new Dictionary<IPAddress, string>();

        foreach (var reply in replies)
        {
            var headers = ParseHeaders(reply.Data);
            if (headers.Count == 0) continue;

            var finding = GetOrCreate(byIp, reply.From);

            if (headers.TryGetValue("ST", out var st)) finding.Attributes.TryAdd("ssdp.st", st);
            if (headers.TryGetValue("USN", out var usn)) finding.Attributes.TryAdd("ssdp.usn", usn);
            if (headers.TryGetValue("SERVER", out var server)) finding.Attributes.TryAdd("ssdp.server", server);
            if (headers.TryGetValue("LOCATION", out var loc) && !locations.ContainsKey(reply.From))
                locations[reply.From] = loc;
        }

        if (FetchLocation && locations.Count > 0)
            await EnrichFromLocationsAsync(byIp, locations, ct).ConfigureAwait(false);

        return byIp.Values.ToList();
    }

    private async Task EnrichFromLocationsAsync(
        Dictionary<IPAddress, ProtocolFinding> byIp, Dictionary<IPAddress, string> locations, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(LocationConcurrency);
        var tasks = new List<Task>(locations.Count);

        foreach (var (ip, url) in locations)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (byIp.TryGetValue(ip, out var finding))
                        await FetchDescriptionAsync(finding, ip, url, ct).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task FetchDescriptionAsync(ProtocolFinding finding, IPAddress source, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        // Prefer the URL's own IP, else the responder IP. HttpLite refuses non-LAN targets.
        IPAddress host = IPAddress.TryParse(uri.Host, out var parsed) ? parsed : source;
        bool tls = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

        var resp = await HttpLite.RequestAsync(
            host, uri.Port, "GET", uri.PathAndQuery, tls, LocationMaxBytes,
            TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

        if (resp is null || string.IsNullOrEmpty(resp.Body)) return;

        try
        {
            var doc = XDocument.Parse(resp.Body);
            string? friendly = Local(doc, "friendlyName");
            string? manufacturer = Local(doc, "manufacturer");
            string? modelName = Local(doc, "modelName");
            string? modelNumber = Local(doc, "modelNumber");

            lock (finding)
            {
                if (friendly is not null) { finding.Hostname ??= friendly; finding.Attributes["upnp.friendlyName"] = friendly; }
                if (manufacturer is not null) { finding.Vendor ??= manufacturer; finding.Attributes["upnp.manufacturer"] = manufacturer; }
                if (modelName is not null) { finding.Model ??= modelName; finding.Attributes["upnp.modelName"] = modelName; }
                if (modelNumber is not null) finding.Attributes["upnp.modelNumber"] = modelNumber;
            }
        }
        catch { /* not valid UPnP XML - keep the header-level evidence */ }
    }

    private static ProtocolFinding GetOrCreate(Dictionary<IPAddress, ProtocolFinding> map, IPAddress ip)
    {
        if (!map.TryGetValue(ip, out var f))
        {
            f = new ProtocolFinding { Address = ip, Source = DiscoverySource.Ssdp };
            map[ip] = f;
        }
        return f;
    }

    private static byte[] BuildMSearch(string searchTarget)
    {
        string msg =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {searchTarget}\r\n" +
            "USER-AGENT: ThopterIoT/0.1 UPnP/1.1\r\n" +
            "\r\n";
        return Encoding.ASCII.GetBytes(msg);
    }

    private static Dictionary<string, string> ParseHeaders(byte[] data)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = Encoding.Latin1.GetString(data);
        var lines = text.Split("\r\n");
        // lines[0] is the status/request line (HTTP/1.1 200 OK or NOTIFY) - skip it.
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string key = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0) headers[key] = value;
        }
        return headers;
    }

    private static string? Local(XDocument doc, string localName)
    {
        string? v = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
