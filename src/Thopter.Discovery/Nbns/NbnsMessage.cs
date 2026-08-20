using System.Buffers.Binary;
using System.Text;

namespace Thopter.Discovery.Nbns;

/// <summary>
/// Minimal NetBIOS Name Service codec (RFC 1002, UDP 137). Builds a "node status" request
/// (a.k.a. adapter status query, the wire form of <c>nbtstat -A &lt;ip&gt;</c>) and decodes
/// the response's name list to recover a Windows machine name. Unauthenticated, one-shot,
/// no reflection, no external library - hand-decoded in the same style as the mDNS
/// <see cref="Mdns.DnsMessage"/>. Never logs in, never reads anything but the advertised
/// NetBIOS name table.
/// </summary>
internal static class NbnsMessage
{
    /// <summary>NBSTAT: node status resource-record type (RFC 1002 §4.2.17).</summary>
    private const ushort NbstatType = 0x0021;

    /// <summary>Internet class.</summary>
    private const ushort InClass = 0x0001;

    /// <summary>Top bit of a NAME_FLAGS field: the group-name (G) bit - set means a group, not a host.</summary>
    private const ushort GroupNameFlag = 0x8000;

    /// <summary>NetBIOS name suffix for the Workstation service - the plain computer name.</summary>
    private const byte SuffixWorkstation = 0x00;

    /// <summary>NetBIOS name suffix for the File Server service - also carries the machine name.</summary>
    private const byte SuffixFileServer = 0x20;

    private const int HeaderSize = 12;       // ID(2) FLAGS(2) QD(2) AN(2) NS(2) AR(2)
    private const int EncodedNameSize = 32;  // 16-byte NetBIOS name, first-level encoded (2 bytes each)
    private const int RrFixedFieldsSize = 10; // TYPE(2) CLASS(2) TTL(4) RDLENGTH(2)
    private const int NameEntrySize = 18;    // NAME(15) SUFFIX(1) NAME_FLAGS(2)

    /// <summary>
    /// Encode a node status request for the wildcard name "*" (returns the responder's whole
    /// name table). Transaction ID and all flags are zero - a unicast node status query, per
    /// RFC 1002 §4.2.17; replies are matched by the responding IP, not the transaction ID.
    /// </summary>
    public static byte[] BuildNodeStatusRequest(ushort transactionId = 0)
    {
        // Header + QNAME (length + 32 encoded bytes + root terminator) + QTYPE + QCLASS.
        var buffer = new byte[HeaderSize + 1 + EncodedNameSize + 1 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), transactionId); // ID
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 0x0000);         // FLAGS (standard query)
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), 1);              // QDCOUNT
        // ANCOUNT / NSCOUNT / ARCOUNT stay zero.

        int pos = HeaderSize;
        buffer[pos++] = EncodedNameSize;                     // label length (always 0x20)
        EncodeWildcardName(buffer.AsSpan(pos, EncodedNameSize));
        pos += EncodedNameSize;
        buffer[pos++] = 0x00;                                // root label terminator

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos, 2), NbstatType); pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos, 2), InClass);

        return buffer;
    }

    /// <summary>
    /// First-level-encode the 16-byte NetBIOS name "*" (an asterisk followed by 15 NUL bytes,
    /// RFC 1002 §4.2.17). Each name byte becomes two bytes: high nibble + 'A', low nibble + 'A'.
    /// </summary>
    private static void EncodeWildcardName(Span<byte> dest)
    {
        Span<byte> name = stackalloc byte[16];
        name.Clear();
        name[0] = (byte)'*';

        for (int i = 0; i < name.Length; i++)
        {
            byte b = name[i];
            dest[i * 2] = (byte)('A' + (b >> 4));
            dest[i * 2 + 1] = (byte)('A' + (b & 0x0F));
        }
    }

    /// <summary>
    /// Decode a node status response and return the responder's machine name, preferring the
    /// unique Workstation-suffix name and falling back to the File Server name. Never throws:
    /// a NetBIOS reply is unauthenticated input from an arbitrary host, so a truncated or
    /// malformed packet just yields <c>false</c>.
    /// </summary>
    public static bool TryParseNodeStatusResponse(ReadOnlySpan<byte> data, out string? name)
    {
        name = null;
        if (data.Length < HeaderSize) return false;

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));

        int pos = HeaderSize;

        for (int i = 0; i < qdCount; i++)
        {
            if (!SkipName(data, ref pos)) return false;
            if (pos + 4 > data.Length) return false;
            pos += 4; // QTYPE + QCLASS
        }

        for (int i = 0; i < anCount; i++)
        {
            if (!SkipName(data, ref pos)) return false;
            if (pos + RrFixedFieldsSize > data.Length) return false;

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;
            pos += 2; // CLASS
            pos += 4; // TTL
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;

            if (pos + rdLength > data.Length) return false;
            var rdata = data.Slice(pos, rdLength);
            pos += rdLength;

            if (type == NbstatType && TryReadMachineName(rdata, out name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Read the NBSTAT RDATA's name list: NUM_NAMES(1) then that many 18-byte entries
    /// (15-byte name + 1 suffix + 2 flag bytes). Pick the first unique Workstation name; if
    /// none, fall back to the first unique File Server name. Group names are skipped.
    /// </summary>
    private static bool TryReadMachineName(ReadOnlySpan<byte> rdata, out string? name)
    {
        name = null;
        if (rdata.Length < 1) return false;

        int numNames = rdata[0];
        int p = 1;
        string? fileServer = null;

        for (int i = 0; i < numNames; i++)
        {
            if (p + NameEntrySize > rdata.Length) break;
            var entry = rdata.Slice(p, NameEntrySize);
            p += NameEntrySize;

            byte suffix = entry[15];
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(16, 2));
            if ((flags & GroupNameFlag) != 0) continue; // group / workgroup entry, not a host name

            string label = DecodeName(entry.Slice(0, 15));
            if (label.Length == 0) continue;

            if (suffix == SuffixWorkstation)
            {
                name = label; // the plain computer name - best answer, take it immediately
                return true;
            }
            if (suffix == SuffixFileServer)
                fileServer ??= label;
        }

        if (fileServer is not null)
        {
            name = fileServer;
            return true;
        }
        return false;
    }

    /// <summary>Trim trailing spaces/NULs from a 15-byte NetBIOS name and keep only printable ASCII.</summary>
    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        int end = raw.Length;
        while (end > 0 && (raw[end - 1] == 0x20 || raw[end - 1] == 0x00)) end--;

        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++)
        {
            byte b = raw[i];
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '?');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Advance <paramref name="pos"/> past a NetBIOS/DNS name (length-prefixed labels ending in
    /// a zero byte, or a two-byte 0xC0 compression pointer). Returns false on any out-of-bounds read.
    /// </summary>
    private static bool SkipName(ReadOnlySpan<byte> data, ref int pos)
    {
        while (true)
        {
            if ((uint)pos >= (uint)data.Length) return false;
            byte lead = data[pos];

            if (lead == 0) { pos += 1; return true; }               // root label - name complete
            if ((lead & 0xC0) == 0xC0)                              // compression pointer (2 bytes)
            {
                if (pos + 2 > data.Length) return false;
                pos += 2;
                return true;
            }
            if ((lead & 0xC0) != 0) return false;                   // reserved length prefix - invalid

            pos += 1 + lead;
            if (pos > data.Length) return false;
        }
    }
}
