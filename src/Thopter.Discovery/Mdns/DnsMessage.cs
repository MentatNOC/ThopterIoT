using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Thopter.Discovery.Mdns;

/// <summary>DNS resource record TYPE values (RFC 1035 §3.2.2) that mDNS/DNS-SD actually uses here.</summary>
internal static class DnsType
{
    public const ushort A = 1;
    public const ushort PTR = 12;
    public const ushort TXT = 16;
    public const ushort SRV = 33;
    public const ushort ANY = 255;
}

/// <summary>DNS CLASS values plus the mDNS-specific bits that live in the same field.</summary>
internal static class DnsClass
{
    public const ushort IN = 1;

    /// <summary>
    /// Top bit of a question's QCLASS: the mDNS "QU" bit (RFC 6762 §5.4). It asks the
    /// responder to unicast its answer directly back to us instead of multicasting it to
    /// the group. <see cref="Net.UdpProbe"/> never joins the multicast group - it only
    /// listens on the ephemeral port it sent from - so without this bit set, responses
    /// go to the group and we never see them.
    /// </summary>
    public const ushort UnicastResponseBit = 0x8000;

    /// <summary>
    /// Top bit of an answer's CLASS field in an mDNS *response*: the "cache-flush" bit
    /// (RFC 6762 §10.2). Mask it off before comparing a record's class against <see cref="IN"/>.
    /// </summary>
    public const ushort CacheFlushBit = 0x8000;
}

/// <summary>Decoded SRV RDATA (RFC 2782): where a service instance actually lives.</summary>
internal readonly record struct DnsSrvRecord(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>
/// One decoded resource record. <see cref="Name"/>/<see cref="Type"/>/<see cref="Class"/>/
/// <see cref="Ttl"/> are always populated; exactly one of the typed payload properties is
/// populated depending on <see cref="Type"/> (the others stay null). Record types this
/// codec doesn't care about (AAAA, NSEC, etc.) still show up with no typed payload - the
/// caller can see the record existed without us having decoded its RDATA.
/// </summary>
internal sealed class DnsRecord
{
    public required string Name { get; init; }
    public required ushort Type { get; init; }
    public required ushort Class { get; init; }
    public required uint Ttl { get; init; }

    /// <summary>PTR (12): the target domain name.</summary>
    public string? PtrTarget { get; init; }

    /// <summary>SRV (33): priority/weight/port/target host.</summary>
    public DnsSrvRecord? Srv { get; init; }

    /// <summary>TXT (16): each character-string as decoded text, in wire order.</summary>
    public IReadOnlyList<string>? Txt { get; init; }

    /// <summary>A (1): the IPv4 address.</summary>
    public IPAddress? Address { get; init; }
}

/// <summary>A decoded DNS message: the header's transaction ID plus every resource record found.</summary>
internal sealed class DnsMessage
{
    public required ushort Id { get; init; }

    /// <summary>Every RR from the Answer, Authority and Additional sections, in wire order.</summary>
    public required IReadOnlyList<DnsRecord> Records { get; init; }

    private static readonly DnsMessage Empty = new() { Id = 0, Records = Array.Empty<DnsRecord>() };

    // --- Wire-format constants (RFC 1035 §4.1.1) ---
    private const int HeaderSize = 12; // ID(2) FLAGS(2) QDCOUNT(2) ANCOUNT(2) NSCOUNT(2) ARCOUNT(2)
    private const int RrFixedFieldsSize = 10; // TYPE(2) CLASS(2) TTL(4) RDLENGTH(2), after the NAME
    private const int MaxPointerJumps = 128; // generous bound; a real name never needs more than a handful

    /// <summary>
    /// Encode a standard mDNS query: header + one question per (Name, Type) pair, every
    /// question marked QU (unicast-response requested, see <see cref="DnsClass.UnicastResponseBit"/>).
    /// Transaction ID is 0 and all counts besides QDCOUNT are 0, per RFC 6762 §18 (mDNS
    /// query/response IDs are conventionally ignored / zero).
    /// </summary>
    public static byte[] BuildQuery(IEnumerable<(string Name, ushort Type)> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var list = questions as IReadOnlyCollection<(string Name, ushort Type)> ?? questions.ToList();

        using var buffer = new MemoryStream(HeaderSize + list.Count * 32);

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], 0);                    // ID
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0);                    // FLAGS (standard query)
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], (ushort)list.Count);   // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], 0);                    // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(header[8..10], 0);                   // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(header[10..12], 0);                  // ARCOUNT
        buffer.Write(header);

        Span<byte> typeClass = stackalloc byte[4];
        foreach (var (name, type) in list)
        {
            WriteName(buffer, name);
            BinaryPrimitives.WriteUInt16BigEndian(typeClass[0..2], type);
            BinaryPrimitives.WriteUInt16BigEndian(typeClass[2..4], (ushort)(DnsClass.IN | DnsClass.UnicastResponseBit));
            buffer.Write(typeClass);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decode a DNS/mDNS message. Never throws: mDNS is unauthenticated multicast from
    /// arbitrary devices on the LAN, so a truncated or malformed packet is expected input,
    /// not an exceptional one. On any parse failure this returns whatever records were
    /// already decoded before the failure (possibly none) - it only returns <c>false</c>
    /// when the 12-byte header itself doesn't fit.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage msg)
    {
        msg = Empty;
        if (data.Length < HeaderSize) return false;

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(data[0..2]);
        // data[2..4] = FLAGS - unused for our purposes (we don't distinguish query/response,
        // truncation, or opcode; we just harvest whatever records are present).
        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
        ushort nsCount = BinaryPrimitives.ReadUInt16BigEndian(data[8..10]);
        ushort arCount = BinaryPrimitives.ReadUInt16BigEndian(data[10..12]);

        int pos = HeaderSize;

        // Skip the question section (name + QTYPE(2) + QCLASS(2)). mDNS responses often
        // echo the question back, but we only care about the answer/authority/additional RRs.
        for (int i = 0; i < qdCount; i++)
        {
            if (!TryReadName(data, ref pos, out _)) { msg = new DnsMessage { Id = id, Records = Array.Empty<DnsRecord>() }; return true; }
            if (pos + 4 > data.Length) { msg = new DnsMessage { Id = id, Records = Array.Empty<DnsRecord>() }; return true; }
            pos += 4;
        }

        // Answer + Authority + Additional are wire-identical RRs; mDNS responders routinely
        // stuff SRV/TXT/A into Additional alongside a PTR Answer, so we read all three sections.
        int totalRr = anCount + nsCount + arCount;
        var records = new List<DnsRecord>(Math.Min(totalRr, 64));
        for (int i = 0; i < totalRr; i++)
        {
            if (!TryReadRecord(data, ref pos, out var record)) break; // stop at the first malformed RR, keep what we have
            if (record is not null) records.Add(record);
        }

        msg = new DnsMessage { Id = id, Records = records };
        return true;
    }

    /// <summary>Read one resource record starting at <paramref name="pos"/>, advancing it past the RDATA.</summary>
    private static bool TryReadRecord(ReadOnlySpan<byte> data, ref int pos, out DnsRecord? record)
    {
        record = null;

        if (!TryReadName(data, ref pos, out string name)) return false;
        if (pos + RrFixedFieldsSize > data.Length) return false;

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;
        ushort rawClass = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4)); pos += 4;
        ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2)); pos += 2;

        if (pos + rdLength > data.Length) return false;
        var rdata = data.Slice(pos, rdLength);
        int rdataStart = pos;
        pos += rdLength; // always land exactly after the RDATA, whether or not we decode it below

        ushort dnsClass = (ushort)(rawClass & ~DnsClass.CacheFlushBit);

        string? ptrTarget = null;
        DnsSrvRecord? srv = null;
        List<string>? txt = null;
        IPAddress? address = null;

        switch (type)
        {
            case DnsType.PTR:
            {
                // RDATA is a single (possibly compressed) domain name; pointers inside it
                // are offsets from the start of the whole packet, so we parse from `data`.
                int p = rdataStart;
                if (TryReadName(data, ref p, out string target)) ptrTarget = target;
                break;
            }

            case DnsType.SRV:
            {
                // PRIORITY(2) WEIGHT(2) PORT(2) TARGET(name)
                if (rdLength >= 6)
                {
                    ushort priority = BinaryPrimitives.ReadUInt16BigEndian(rdata[0..2]);
                    ushort weight = BinaryPrimitives.ReadUInt16BigEndian(rdata[2..4]);
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(rdata[4..6]);
                    int p = rdataStart + 6;
                    if (TryReadName(data, ref p, out string target))
                        srv = new DnsSrvRecord(priority, weight, port, target);
                }
                break;
            }

            case DnsType.TXT:
            {
                // A run of character-strings: 1-byte length prefix + that many bytes, back to back
                // until RDLENGTH is exhausted. No name compression inside TXT RDATA.
                var list = new List<string>();
                int p = 0;
                while (p < rdata.Length)
                {
                    int len = rdata[p]; p += 1;
                    if (p + len > rdata.Length) break; // truncated character-string - stop, keep what we had
                    if (len > 0) list.Add(Encoding.UTF8.GetString(rdata.Slice(p, len)));
                    p += len;
                }
                txt = list;
                break;
            }

            case DnsType.A:
            {
                if (rdLength == 4) address = new IPAddress(rdata);
                break;
            }

            default:
                break; // unsupported type - record is still returned with no typed payload
        }

        record = new DnsRecord
        {
            Name = name,
            Type = type,
            Class = dnsClass,
            Ttl = ttl,
            PtrTarget = ptrTarget,
            Srv = srv,
            Txt = txt,
            Address = address,
        };
        return true;
    }

    /// <summary>
    /// Decode a (possibly compressed) domain name at <paramref name="pos"/> and advance
    /// <paramref name="pos"/> to just past it in the *original* stream - i.e. past the two
    /// bytes of the first compression pointer encountered, if any, not into the jumped-to
    /// data (RFC 1035 §4.1.4). Guards against out-of-bounds reads and pointer loops; on any
    /// problem it returns false and leaves <paramref name="pos"/> undefined for the caller
    /// (which then aborts the whole parse rather than reading from a bogus offset).
    /// </summary>
    private static bool TryReadName(ReadOnlySpan<byte> data, ref int pos, out string name)
    {
        name = "";
        var sb = new StringBuilder();

        int cursor = pos;
        int firstJumpReturn = -1; // where `pos` should end up if we ever follow a pointer
        bool jumped = false;
        int jumps = 0;
        HashSet<int>? visited = null; // pointer targets already followed, allocated lazily (loop guard)

        while (true)
        {
            if ((uint)cursor >= (uint)data.Length) return false;
            byte lead = data[cursor];

            if (lead == 0)
            {
                cursor += 1;
                break; // root label - name is complete
            }

            if ((lead & 0xC0) == 0xC0)
            {
                // Compression pointer: bottom 6 bits of this byte + all 8 bits of the next
                // byte form a 14-bit offset from the start of the packet.
                if (cursor + 1 >= data.Length) return false;
                int target = ((lead & 0x3F) << 8) | data[cursor + 1];

                if (!jumped)
                {
                    firstJumpReturn = cursor + 2;
                    jumped = true;
                }

                visited ??= new HashSet<int>();
                if (!visited.Add(target)) return false;      // repeated target -> loop
                if (++jumps > MaxPointerJumps) return false;  // pathologically long pointer chain

                cursor = target;
                continue;
            }

            if ((lead & 0xC0) != 0) return false; // 0x40 / 0x80: reserved label-length prefix, not valid here

            int labelLen = lead;
            cursor += 1;
            if (cursor + labelLen > data.Length) return false;

            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(data.Slice(cursor, labelLen)));
            cursor += labelLen;

            if (sb.Length > 1024) return false; // sanity cap - a real DNS name is <= 255 wire bytes
        }

        pos = jumped ? firstJumpReturn : cursor;
        name = sb.ToString();
        return true;
    }

    /// <summary>Write QNAME: length-prefixed labels split on '.', terminated by a zero (root) label.</summary>
    private static void WriteName(MemoryStream buffer, string name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var label in name.Split('.'))
            {
                if (label.Length == 0) continue; // tolerate a stray leading/trailing/double dot
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                int len = Math.Min(bytes.Length, 63); // RFC 1035 §3.1: a label is at most 63 octets
                buffer.WriteByte((byte)len);
                buffer.Write(bytes, 0, len);
            }
        }

        buffer.WriteByte(0); // root label terminator
    }
}
