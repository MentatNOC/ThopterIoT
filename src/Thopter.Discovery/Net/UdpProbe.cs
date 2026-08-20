using System.Net;
using System.Net.Sockets;

namespace Thopter.Discovery.Net;

internal readonly record struct UdpReply(IPAddress From, byte[] Data);

/// <summary>
/// Shared multicast probe primitive for ONVIF WS-Discovery, SSDP and mDNS: from each
/// local interface, send one or more datagrams to a multicast group and collect the
/// unicast replies for a fixed window. AOT-safe (plain Socket, no reflection).
/// </summary>
internal static class UdpProbe
{
    public static async Task<IReadOnlyList<UdpReply>> SendAndCollectAsync(
        IPAddress group,
        int port,
        IReadOnlyList<byte[]> payloads,
        TimeSpan window,
        IReadOnlyList<IPAddress> localAddresses,
        CancellationToken ct)
    {
        var replies = new List<UdpReply>();
        var gate = new object();
        var groupEp = new IPEndPoint(group, port);

        var tasks = new List<Task>(localAddresses.Count);
        foreach (var local in localAddresses)
            tasks.Add(RunInterfaceAsync(local, groupEp, payloads, window, replies, gate, ct));

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return replies;
    }

    private static async Task RunInterfaceAsync(
        IPAddress local, IPEndPoint groupEp, IReadOnlyList<byte[]> payloads, TimeSpan window,
        List<UdpReply> replies, object gate, CancellationToken ct)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(local, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        }
        catch (SocketException)
        {
            return; // interface can't do multicast - skip it
        }

        using (socket)
        {
            try
            {
                foreach (var payload in payloads)
                    await socket.SendToAsync(payload, SocketFlags.None, groupEp, ct).ConfigureAwait(false);
            }
            catch (SocketException) { return; }
            catch (OperationCanceledException) { return; }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(window);
            var buffer = new byte[65_536];

            while (!cts.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                if (result.ReceivedBytes <= 0) continue;
                var data = new byte[result.ReceivedBytes];
                Array.Copy(buffer, data, result.ReceivedBytes);
                var from = ((IPEndPoint)result.RemoteEndPoint).Address;
                lock (gate) replies.Add(new UdpReply(from, data));
            }
        }
    }
}
