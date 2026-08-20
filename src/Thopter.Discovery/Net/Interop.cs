using System.Runtime.InteropServices;

namespace Thopter.Discovery.Net;

/// <summary>
/// Source-generated P/Invoke into the in-box Windows IP Helper API. No Npcap, no
/// driver, no elevation. All three imports are AOT-safe (blittable, no marshalling
/// reflection). Struct offsets are the documented x64/arm64 (LP64) layout of
/// <c>MIB_IPNET_ROW2</c> from netioapi.h.
/// </summary>
internal static partial class Interop
{
    internal const ushort AF_INET = 2;

    internal const uint NO_ERROR = 0;

    // NL_NEIGHBOR_STATE
    internal const int NlnsUnreachable = 0;
    internal const int NlnsIncomplete = 1;
    internal const int NlnsProbe = 2;
    internal const int NlnsDelay = 3;
    internal const int NlnsStale = 4;
    internal const int NlnsReachable = 5;
    internal const int NlnsPermanent = 6;

    /// <summary>Header bytes before the first row in MIB_IPNET_TABLE2 (ULONG NumEntries + 4 pad for 8-byte row alignment).</summary>
    internal const int TableHeaderSize = 8;

    /// <summary>Read the entire IPv4 neighbor (ARP) table in one shot. Caller must FreeMibTable the returned pointer.</summary>
    [LibraryImport("iphlpapi.dll")]
    internal static partial uint GetIpNetTable2(ushort Family, out IntPtr Table);

    [LibraryImport("iphlpapi.dll")]
    internal static partial void FreeMibTable(IntPtr Memory);

    /// <summary>Resolve a single IPv4 address to a MAC via the OS ARP path (backfill for multicast responders not yet cached).</summary>
    [LibraryImport("iphlpapi.dll")]
    internal static unsafe partial uint SendARP(uint DestIP, uint SrcIP, byte* pMacAddr, uint* PhyAddrLen);

    /// <summary>
    /// Return the interface the OS would route a packet to <paramref name="dwDestAddr"/> out
    /// of. This is a pure routing-table lookup that applies the full metric logic (route
    /// metric + per-interface metric) - it opens no socket and sends nothing. Called with a
    /// documentation address to resolve the true internet-facing (default-route) interface.
    /// </summary>
    [LibraryImport("iphlpapi.dll")]
    internal static partial uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    /// <summary>
    /// MIB_IPNET_ROW2, explicit x64/arm64 layout. Only the fields Thopter reads are
    /// declared; Size=88 pins the full record stride so the pointer walk is correct.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    internal unsafe struct MIB_IPNET_ROW2
    {
        /// <summary>SOCKADDR_INET.si_family (AF_INET == 2 for IPv4).</summary>
        [FieldOffset(0)] internal ushort AddressFamily;

        /// <summary>SOCKADDR_IN.sin_addr - IPv4 address in network byte order.</summary>
        [FieldOffset(4)] internal uint Ipv4Address;

        /// <summary>PhysicalAddress[IF_MAX_PHYS_ADDRESS_LENGTH == 32].</summary>
        [FieldOffset(40)] internal fixed byte PhysicalAddress[32];

        [FieldOffset(72)] internal uint PhysicalAddressLength;

        /// <summary>NL_NEIGHBOR_STATE.</summary>
        [FieldOffset(76)] internal int State;
    }
}
