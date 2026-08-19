using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Thopter.Discovery.Net;

internal sealed record HttpLiteResponse(
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string? TlsSubjectCn);

/// <summary>
/// Minimal, dependency-free HTTP/1.1 client over a raw socket, used only to read a
/// device's own unauthenticated, advertised pages (SSDP LOCATION XML, HTTP Server
/// header, TLS cert CN). It has NO System.Net.Http reference and, critically, it
/// <b>refuses any destination that is not on the local network</b> — that is the
/// egress guard from the open-core wall, enforced in code.
/// </summary>
internal static class HttpLite
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    public static async Task<HttpLiteResponse?> RequestAsync(
        IPAddress host, int port, string method, string path, bool tls,
        int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        // Egress guard: only ever talk to LAN addresses. A public destination is refused.
        if (!NetInfo.IsLanScannable(host)) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(host, port), token).ConfigureAwait(false); }
        catch { return null; }

        Stream stream = new NetworkStream(socket, ownsSocket: false);
        string? cn = null;
        try
        {
            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                var opts = new SslClientAuthenticationOptions
                {
                    TargetHost = host.ToString(),
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                };
                try
                {
                    await ssl.AuthenticateAsClientAsync(opts, token).ConfigureAwait(false);
                    if (ssl.RemoteCertificate is not null)
                        cn = ExtractCn(ssl.RemoteCertificate.Subject);
                }
                catch { ssl.Dispose(); return null; }
                stream = ssl;
            }

            var req = new StringBuilder()
                .Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n")
                .Append("Host: ").Append(host).Append("\r\n")
                .Append("User-Agent: ThopterIoT/0.1 (discovery)\r\n")
                .Append("Accept: */*\r\n")
                .Append("Connection: close\r\n\r\n")
                .ToString();

            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(req), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            catch { return cn is null ? null : new HttpLiteResponse(0, EmptyHeaders, "", cn); }

            using var ms = new MemoryStream();
            var buf = new byte[8192];
            int total = 0;
            while (total < maxBytes)
            {
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, maxBytes - total)), token).ConfigureAwait(false); }
                catch { break; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                total += n;
            }

            return ParseResponse(ms.ToArray(), cn);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static HttpLiteResponse ParseResponse(byte[] raw, string? cn)
    {
        // Latin-1 keeps every byte 1:1 for header parsing; bodies are XML/ASCII in practice.
        string text = Encoding.Latin1.GetString(raw);
        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? text : text[..split];
        string body = split < 0 ? "" : text[(split + 4)..];

        var lines = head.Split("\r\n");
        int status = 0;
        if (lines.Length > 0)
        {
            var parts = lines[0].Split(' ', 3);
            if (parts.Length >= 2) int.TryParse(parts[1], out status);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string key = lines[i][..colon].Trim();
            string val = lines[i][(colon + 1)..].Trim();
            if (key.Length > 0) headers[key] = val;
        }

        return new HttpLiteResponse(status, headers, body, cn);
    }

    private static string? ExtractCn(string distinguishedName)
    {
        // Pull CN=... out of a subject DN like "CN=cam01, O=Acme, C=US".
        int i = distinguishedName.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += 3;
        int end = distinguishedName.IndexOf(',', i);
        string cn = (end < 0 ? distinguishedName[i..] : distinguishedName[i..end]).Trim();
        return cn.Length == 0 ? null : cn;
    }
}
