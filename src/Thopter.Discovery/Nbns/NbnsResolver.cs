using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.Nbns;

/// <summary>Tunables for the NetBIOS name query stage.</summary>
public sealed class NbnsOptions
{
    /// <summary>Max concurrent NBNS queries in flight.</summary>
    public int Concurrency { get; init; } = 64;

    /// <summary>How long to wait for one host's node status reply.</summary>
    public int TimeoutMs { get; init; } = 700;

    /// <summary>Upper bound on the random pre-send delay per host, so the stage stays a gentle LAN citizen.</summary>
    public int MaxJitterMs { get; init; } = 40;
}

/// <summary>
/// Resolve Windows machine names by sending a NetBIOS name-service node status query
/// (UDP 137) directly to each device IP and decoding the advertised name table. This is
/// unauthenticated, on-LAN, and one-shot - every destination is gated on
/// <see cref="NetInfo.IsLanScannable"/>, so nothing ever leaves the local network. This
/// deliberately avoids <c>Dns.GetHostEntryAsync</c>, whose reverse lookups can hit a public
/// resolver and break the egress guarantee. AOT-safe (plain Socket, no reflection).
/// </summary>
internal sealed class NbnsResolver
{
    private const int NbnsPort = 137;

    /// <summary>Query each target and return an IP to machine-name map for those that answered.</summary>
    public async Task<IReadOnlyDictionary<IPAddress, string>> ResolveAsync(
        IReadOnlyList<IPAddress> targets, NbnsOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        var result = new ConcurrentDictionary<IPAddress, string>();
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var tasks = new List<Task>(targets.Count);

        try
        {
            foreach (var ip in targets)
            {
                if (!NetInfo.IsLanScannable(ip)) continue; // egress guard - never query off-LAN
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(ResolveOneAsync(ip, options, result, gate, ct));
            }
        }
        finally
        {
            // Always drain the queries already started before `using` disposes the semaphore -
            // even if WaitAsync threw on cancellation. Otherwise an in-flight task's
            // gate.Release() would run against a disposed SemaphoreSlim and fault unobserved,
            // and its socket would leak until the task drifted to completion.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task ResolveOneAsync(
        IPAddress ip, NbnsOptions options,
        ConcurrentDictionary<IPAddress, string> result, SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            if (options.MaxJitterMs > 0)
                await Task.Delay(Random.Shared.Next(options.MaxJitterMs + 1), ct).ConfigureAwait(false);

            string? name = await QueryAsync(ip, options.TimeoutMs, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(name))
                result[ip] = name!;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* per-host timeout, not a cancel */ }
        catch (OperationCanceledException) { throw; }
        catch { /* transient socket errors are non-fatal for a name query */ }
        finally { gate.Release(); }
    }

    private static async Task<string?> QueryAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        byte[] request = NbnsMessage.BuildNodeStatusRequest();
        var remote = new IPEndPoint(ip, NbnsPort);

        await socket.SendToAsync(request, SocketFlags.None, remote, cts.Token).ConfigureAwait(false);

        var buffer = new byte[4096];
        while (!cts.IsCancellationRequested)
        {
            SocketReceiveFromResult recv;
            try
            {
                recv = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            if (recv.ReceivedBytes <= 0) continue;
            if (!((IPEndPoint)recv.RemoteEndPoint).Address.Equals(ip)) continue; // only the host we asked

            if (NbnsMessage.TryParseNodeStatusResponse(buffer.AsSpan(0, recv.ReceivedBytes), out var name))
                return name;
        }

        return null;
    }
}
