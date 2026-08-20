using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.PortScan;

public sealed class TcpScanOptions
{
    /// <summary>Default camera/NVR-oriented TCP port shortlist. Longer = slower and noisier.</summary>
    public static readonly IReadOnlyList<int> DefaultCameraPorts = new[]
    {
        80, 443, 554, 8000, 8080, 8443, 88, 8899, 9000, 5000, 37777, 34567,
    };

    public IReadOnlyList<int> Ports { get; init; } = DefaultCameraPorts;
    public int Concurrency { get; init; } = 256;
    public int ConnectTimeoutMs { get; init; } = 800;
    public int BannerTimeoutMs { get; init; } = 1500;
    public bool GrabBanners { get; init; } = true;
}

/// <summary>
/// TCP connect scan (no SYN/raw sockets, no admin) plus light, unauthenticated banners:
/// HTTP <c>Server</c> and <c>WWW-Authenticate</c> realm, TLS certificate CN, and RTSP
/// <c>OPTIONS</c>. Never logs in, never fetches media. All destinations are LAN (guarded).
/// </summary>
public sealed class TcpScanner
{
    public Task<IReadOnlyList<ProtocolFinding>> ScanAsync(
        IReadOnlyList<IPAddress> targets, TcpScanOptions options, CancellationToken ct)
        => ScanAsync(targets, options, completedFraction: null, ct);

    /// <summary>
    /// Scan with live progress: <paramref name="completedFraction"/> reports the fraction of
    /// (host, port) probes finished so far, 0.0 to 1.0. May be called from pool threads.
    /// </summary>
    public async Task<IReadOnlyList<ProtocolFinding>> ScanAsync(
        IReadOnlyList<IPAddress> targets, TcpScanOptions options,
        IProgress<double>? completedFraction, CancellationToken ct)
    {
        var byIp = new ConcurrentDictionary<IPAddress, ProtocolFinding>();
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var tasks = new List<Task>();

        var lanTargets = targets.Where(NetInfo.IsLanScannable).ToList(); // egress guard
        long totalUnits = (long)lanTargets.Count * options.Ports.Count;
        long doneUnits = 0;

        foreach (var ip in lanTargets)
        {
            foreach (var port in options.Ports)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(RunOneAsync(ip, port));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return byIp.Values.ToList();

        async Task RunOneAsync(IPAddress ip, int port)
        {
            try
            {
                await ScanOneAsync(ip, port, options, byIp, gate, ct).ConfigureAwait(false);
            }
            finally
            {
                if (completedFraction is not null && totalUnits > 0)
                    completedFraction.Report(Interlocked.Increment(ref doneUnits) / (double)totalUnits);
            }
        }
    }

    private static async Task ScanOneAsync(
        IPAddress ip, int port, TcpScanOptions options,
        ConcurrentDictionary<IPAddress, ProtocolFinding> byIp, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            if (!await IsOpenAsync(ip, port, options.ConnectTimeoutMs, ct).ConfigureAwait(false))
                return;

            var finding = byIp.GetOrAdd(ip, a => new ProtocolFinding { Address = a, Source = DiscoverySource.PortScan });
            string service = ServiceLabel(port);
            string? banner = options.GrabBanners
                ? await GrabBannerAsync(ip, port, service, options.BannerTimeoutMs, finding, ct).ConfigureAwait(false)
                : null;

            lock (finding)
            {
                finding.Ports.Add(new OpenPort { Port = port, Service = service, Banner = banner });
            }
        }
        catch (OperationCanceledException) { }
        catch { /* transient socket errors are non-fatal */ }
        finally { gate.Release(); }
    }

    private static async Task<bool> IsOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            return socket.Connected;
        }
        catch { return false; }
    }

    private static async Task<string?> GrabBannerAsync(
        IPAddress ip, int port, string service, int timeoutMs, ProtocolFinding finding, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        switch (service)
        {
            case "http":
            case "https":
            {
                bool tls = service == "https";
                var resp = await HttpLite.RequestAsync(ip, port, "GET", "/", tls, 16 * 1024, timeout, ct).ConfigureAwait(false);
                if (resp is null) return null;

                lock (finding)
                {
                    if (resp.Headers.TryGetValue("Server", out var server)) finding.Attributes.TryAdd($"http.server.{port}", server);
                    if (resp.Headers.TryGetValue("WWW-Authenticate", out var auth))
                    {
                        finding.Attributes.TryAdd($"http.realm.{port}", ExtractRealm(auth) ?? auth);
                    }
                    if (resp.TlsSubjectCn is not null) finding.Attributes.TryAdd($"tls.cn.{port}", resp.TlsSubjectCn);
                }

                string? s = resp.Headers.TryGetValue("Server", out var sv) ? sv : null;
                return resp.TlsSubjectCn is not null ? $"{s} (CN={resp.TlsSubjectCn})".Trim() : s;
            }

            case "rtsp":
            {
                string? banner = await RtspOptionsAsync(ip, port, timeout, ct).ConfigureAwait(false);
                if (banner is not null) lock (finding) finding.Attributes.TryAdd($"rtsp.server.{port}", banner);
                return banner;
            }

            default:
                return null; // proprietary ports (37777/34567): record open, don't poke further
        }
    }

    private static async Task<string?> RtspOptionsAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
            string req = $"OPTIONS rtsp://{ip}:{port}/ RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: ThopterIoT/0.1\r\n\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(req), SocketFlags.None, cts.Token).ConfigureAwait(false);

            var buf = new byte[4096];
            int n = await socket.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false);
            if (n <= 0) return null;

            string text = Encoding.Latin1.GetString(buf, 0, n);
            string? server = HeaderValue(text, "Server");
            string? publicMethods = HeaderValue(text, "Public");
            if (server is null && publicMethods is null) return text.Split("\r\n")[0].Trim(); // status line at least
            return server ?? $"RTSP OPTIONS: {publicMethods}";
        }
        catch { return null; }
    }

    private static string ServiceLabel(int port) => port switch
    {
        80 or 8080 or 8000 or 88 or 8899 or 9000 or 5000 => "http",
        443 or 8443 => "https",
        554 => "rtsp",
        37777 => "dahua",
        34567 => "dvrip",
        _ => "tcp",
    };

    private static string? HeaderValue(string httpText, string name)
    {
        foreach (var line in httpText.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static string? ExtractRealm(string wwwAuthenticate)
    {
        int i = wwwAuthenticate.IndexOf("realm=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += 6;
        if (i < wwwAuthenticate.Length && wwwAuthenticate[i] == '"')
        {
            int end = wwwAuthenticate.IndexOf('"', i + 1);
            if (end > i) return wwwAuthenticate[(i + 1)..end];
        }
        return null;
    }
}
