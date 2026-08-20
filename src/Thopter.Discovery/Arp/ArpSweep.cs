using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using Thopter.Discovery.Net;

namespace Thopter.Discovery.Arp;

/// <summary>
/// Tunables for the driver-free IP↔MAC sweep.
/// </summary>
public sealed class ArpSweepOptions
{
    /// <summary>Max concurrent pings while seeding the OS ARP cache.</summary>
    public int PingConcurrency { get; init; } = 256;

    public int PingTimeoutMs { get; init; } = 500;

    /// <summary>Settle time after the ping wave so the neighbor table is populated before we read it.</summary>
    public int PostPingSettleMs { get; init; } = 300;

    /// <summary>If true, run SendARP for targets that pinged but didn't land in the neighbor table.</summary>
    public bool SendArpBackfill { get; init; } = true;
}

/// <summary>
/// Driver-free layer-2 discovery: ping-sweep to seed the OS ARP cache, read the whole
/// IPv4 neighbor table via <c>GetIpNetTable2</c>, then <c>SendARP</c>-backfill stragglers.
/// No Npcap, no raw sockets, no admin. IPv4 only.
/// </summary>
public sealed class ArpSweep
{
    /// <summary>
    /// Sweep <paramref name="targets"/> and return a resolved IP→MAC map.
    /// <paramref name="pinged"/> fires once per host as its ping completes (live progress).
    /// </summary>
    public async Task<IReadOnlyDictionary<IPAddress, PhysicalAddress>> SweepAsync(
        IReadOnlyList<IPAddress> targets,
        ArpSweepOptions options,
        IProgress<IPAddress>? pinged,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        // Hosts that answered ICMP - the only ones we'll SendARP-backfill. SendARP blocks
        // for seconds per *dead* host, so backfilling the whole subnet would hang; we only
        // ever backfill a host we already have positive liveness evidence for.
        var alive = new HashSet<IPAddress>();
        var aliveLock = new object();

        // 1. Ping fan-out to provoke ARP resolution and fill the OS cache.
        using (var gate = new SemaphoreSlim(Math.Max(1, options.PingConcurrency)))
        {
            var pings = new List<Task>(targets.Count);
            foreach (var ip in targets)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                pings.Add(PingOneAsync(ip, options.PingTimeoutMs, gate, pinged, alive, aliveLock, ct));
            }
            await Task.WhenAll(pings).ConfigureAwait(false);
        }

        // Give the stack a moment to commit neighbor entries.
        if (options.PostPingSettleMs > 0)
            await Task.Delay(options.PostPingSettleMs, ct).ConfigureAwait(false);

        // 2. Read the whole IPv4 neighbor table in one shot.
        var map = new Dictionary<IPAddress, PhysicalAddress>(ReadNeighborTable());

        // 3. Backfill alive hosts not yet in the table (rare - the ping already seeds most).
        if (options.SendArpBackfill)
        {
            foreach (var ip in alive)
            {
                ct.ThrowIfCancellationRequested();
                if (map.ContainsKey(ip)) continue;
                if (TryResolveArp(ip, out var mac)) map[ip] = mac;
            }
        }

        return map;
    }

    private static async Task PingOneAsync(
        IPAddress ip, int timeoutMs, SemaphoreSlim gate, IProgress<IPAddress>? pinged,
        HashSet<IPAddress> alive, object aliveLock, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(ip, timeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    lock (aliveLock) alive.Add(ip);
                }
            }
            catch (PingException) { /* host down / unreachable - still seeds the cache, fine */ }
            catch (OperationCanceledException) { throw; }
            catch { /* transient socket errors are non-fatal for a sweep */ }

            pinged?.Report(ip);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Walk MIB_IPNET_TABLE2 and return reachable/stale/permanent IPv4 neighbors with a real 6-byte MAC.
    /// </summary>
    internal static unsafe Dictionary<IPAddress, PhysicalAddress> ReadNeighborTable()
    {
        var result = new Dictionary<IPAddress, PhysicalAddress>();

        uint status = Interop.GetIpNetTable2(Interop.AF_INET, out IntPtr table);
        if (status != Interop.NO_ERROR || table == IntPtr.Zero)
            return result;

        try
        {
            byte* basePtr = (byte*)table;
            uint numEntries = *(uint*)basePtr;
            var rows = (Interop.MIB_IPNET_ROW2*)(basePtr + Interop.TableHeaderSize);

            Span<byte> ipBytes = stackalloc byte[4];
            for (uint i = 0; i < numEntries; i++)
            {
                ref Interop.MIB_IPNET_ROW2 row = ref rows[i];

                if (row.AddressFamily != Interop.AF_INET) continue;
                if (row.State is not (Interop.NlnsStale or Interop.NlnsReachable or Interop.NlnsPermanent))
                    continue;
                if (row.PhysicalAddressLength != 6) continue;

                // Drop multicast/broadcast neighbor entries (e.g. 224.0.0.0/4 groups keyed
                // by 01:00:5E:.. MACs, and the .255 broadcast at FF:FF:FF:FF:FF:FF). The
                // low bit of the first octet is the I/G bit - set means group, never a host NIC.
                if ((row.PhysicalAddress[0] & 0x01) != 0) continue;

                byte[] mac = new byte[6];
                bool allZero = true;
                for (int b = 0; b < 6; b++)
                {
                    mac[b] = row.PhysicalAddress[b];
                    if (mac[b] != 0) allZero = false;
                }
                if (allZero) continue;

                // Ipv4Address is network byte order in memory; BitConverter on little-endian
                // Windows yields the dotted-quad byte order directly.
                BitConverter.TryWriteBytes(ipBytes, row.Ipv4Address);
                var ip = new IPAddress(ipBytes);

                result[ip] = new PhysicalAddress(mac);
            }
        }
        finally
        {
            Interop.FreeMibTable(table);
        }

        return result;
    }

    /// <summary>SendARP fallback for a single IPv4 target.</summary>
    private static unsafe bool TryResolveArp(IPAddress ip, out PhysicalAddress mac)
    {
        mac = PhysicalAddress.None;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        // SendARP wants the destination as an in_addr (network byte order).
        uint dest = BinaryPrimitives.ReadUInt32LittleEndian(ip.GetAddressBytes());

        byte* buffer = stackalloc byte[8];
        uint len = 8;
        uint status = Interop.SendARP(dest, 0, buffer, &len);
        if (status != Interop.NO_ERROR || len < 6) return false;

        byte[] macBytes = new byte[6];
        bool allZero = true;
        for (int i = 0; i < 6; i++)
        {
            macBytes[i] = buffer[i];
            if (macBytes[i] != 0) allZero = false;
        }
        if (allZero) return false;

        mac = new PhysicalAddress(macBytes);
        return true;
    }
}
