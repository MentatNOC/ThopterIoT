using System;
using System.Collections.Generic;
using System.Text;
using Thopter.Discovery.Nbns;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Parser-level tests for the NetBIOS name-service codec, using synthetic node status
/// responses. Proves name extraction deterministically without a live Windows host on the
/// segment - the same approach as <see cref="ProtocolParsingTests"/>.
/// </summary>
public class NbnsParsingTests
{
    [Fact]
    public void Request_is_a_wellformed_wildcard_node_status_query()
    {
        byte[] req = NbnsMessage.BuildNodeStatusRequest();

        // Header(12) + QNAME(1 len + 32 encoded + 1 root) + QTYPE(2) + QCLASS(2) = 50 bytes.
        Assert.Equal(50, req.Length);

        Assert.Equal(0x00, req[4]);      // QDCOUNT high
        Assert.Equal(0x01, req[5]);      // QDCOUNT low - exactly one question
        Assert.Equal(0x20, req[12]);     // label length 32

        // First-level encoding of '*' (0x2A) is "CK": 0x2 -> 'C', 0xA -> 'K'.
        Assert.Equal((byte)'C', req[13]);
        Assert.Equal((byte)'K', req[14]);
        // A NUL name byte encodes to "AA".
        Assert.Equal((byte)'A', req[15]);
        Assert.Equal((byte)'A', req[16]);

        Assert.Equal(0x00, req[45]);     // root label terminator
        Assert.Equal(0x00, req[46]);     // QTYPE high
        Assert.Equal(0x21, req[47]);     // QTYPE low  - NBSTAT (0x0021)
        Assert.Equal(0x00, req[48]);     // QCLASS high
        Assert.Equal(0x01, req[49]);     // QCLASS low - IN
    }

    [Fact]
    public void Response_yields_the_unique_workstation_name_and_skips_group_names()
    {
        // A workgroup (group flag) plus the real computer name. The group entry must be skipped.
        byte[] response = BuildNodeStatusResponse(
            ("WORKGROUP", 0x00, Group:true),
            ("TESTPC", 0x00, Group:false));

        Assert.True(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Equal("TESTPC", name);
    }

    [Fact]
    public void Response_falls_back_to_the_file_server_name_when_no_workstation_entry()
    {
        byte[] response = BuildNodeStatusResponse(
            ("WORKGROUP", 0x00, Group:true),
            ("FILESRV", 0x20, Group:false)); // File Server service suffix, unique

        Assert.True(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Equal("FILESRV", name);
    }

    [Fact]
    public void Response_with_only_group_names_yields_no_name()
    {
        byte[] response = BuildNodeStatusResponse(
            ("WORKGROUP", 0x00, Group:true),
            ("WORKGROUP", 0x1E, Group:true));

        Assert.False(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Null(name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(11)]
    public void Truncated_or_garbage_input_never_throws_and_returns_false(int length)
    {
        var junk = new byte[length];
        for (int i = 0; i < length; i++) junk[i] = (byte)(i * 37);

        Assert.False(NbnsMessage.TryParseNodeStatusResponse(junk, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void Trailing_spaces_in_the_netbios_name_are_trimmed()
    {
        // NetBIOS names are space-padded to 15 bytes; the decoder must strip the padding.
        byte[] response = BuildNodeStatusResponse(("PC01", 0x00, Group:false));

        Assert.True(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Equal("PC01", name);
    }

    [Fact]
    public void Workstation_name_wins_even_when_a_file_server_entry_is_listed_first()
    {
        // File Server (0x20) appears before the Workstation (0x00) entry; the parser must still
        // prefer the Workstation name rather than returning the earlier file-server fallback.
        byte[] response = BuildNodeStatusResponse(
            ("FILESRV", 0x20, Group: false),
            ("PC01", 0x00, Group: false));

        Assert.True(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Equal("PC01", name);
    }

    [Fact]
    public void Response_with_an_echoed_question_section_still_parses()
    {
        // Some responders echo the queried name back in a question section (QDCOUNT >= 1).
        // The parser must skip it and still reach the answer - exercises the question-skip loop.
        byte[] response = BuildResponseCore(
            new[] { ("PC02", (byte)0x00, false) }, withQuestion: true, rdLengthDelta: 0);

        Assert.True(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Equal("PC02", name);
    }

    [Fact]
    public void Response_claiming_more_rdata_than_present_is_rejected_without_reading_past_the_buffer()
    {
        // A hostile RDLENGTH that overruns the packet must be caught by the bounds guard.
        byte[] response = BuildResponseCore(
            new[] { ("PC03", (byte)0x00, false) }, withQuestion: false, rdLengthDelta: 64);

        Assert.False(NbnsMessage.TryParseNodeStatusResponse(response, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void Every_truncated_prefix_of_a_valid_response_parses_safely()
    {
        // A node status reply is unauthenticated LAN input. Slicing a well-formed response at
        // every byte length must never throw and never read past the buffer - it exercises the
        // section-skip, RR-field, RDLENGTH, name-entry, and SkipName out-of-bounds guards.
        byte[] full = BuildNodeStatusResponse(
            ("WORKGROUP", 0x00, Group: true),
            ("HOSTPC", 0x00, Group: false));

        for (int k = 0; k <= full.Length; k++)
        {
            byte[] slice = full.AsSpan(0, k).ToArray();
            var ex = Record.Exception(() => NbnsMessage.TryParseNodeStatusResponse(slice, out _));
            Assert.Null(ex);
        }

        // The complete packet still resolves the name after the fuzz.
        Assert.True(NbnsMessage.TryParseNodeStatusResponse(full, out var name));
        Assert.Equal("HOSTPC", name);
    }

    /// <summary>
    /// Build a synthetic RFC 1002 node status response carrying the given name-table entries,
    /// mirroring what a Windows host returns to a wildcard adapter status query.
    /// </summary>
    private static byte[] BuildNodeStatusResponse(params (string Name, byte Suffix, bool Group)[] names)
        => BuildResponseCore(names, withQuestion: false, rdLengthDelta: 0);

    /// <summary>
    /// Core response builder. <paramref name="withQuestion"/> prepends an echoed question section
    /// (QDCOUNT=1); <paramref name="rdLengthDelta"/> perturbs the declared RDLENGTH to model a
    /// hostile/oversized length field without changing the actual RDATA.
    /// </summary>
    private static byte[] BuildResponseCore(
        (string Name, byte Suffix, bool Group)[] names, bool withQuestion, int rdLengthDelta)
    {
        var msg = new List<byte>();

        // Header: ID, FLAGS(response), QDCOUNT, ANCOUNT=1, NSCOUNT=0, ARCOUNT=0.
        byte qd = (byte)(withQuestion ? 1 : 0);
        msg.AddRange(new byte[] { 0x00, 0x00, 0x84, 0x00, 0x00, qd, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 });

        if (withQuestion)
        {
            AppendEncodedWildcardName(msg);
            msg.AddRange(new byte[] { 0x00, 0x21 }); // QTYPE NBSTAT
            msg.AddRange(new byte[] { 0x00, 0x01 }); // QCLASS IN
        }

        // Answer NAME: the encoded wildcard "*" name.
        AppendEncodedWildcardName(msg);
        msg.AddRange(new byte[] { 0x00, 0x21 }); // TYPE NBSTAT
        msg.AddRange(new byte[] { 0x00, 0x01 }); // CLASS IN
        msg.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // TTL

        var rdata = new List<byte> { (byte)names.Length };
        foreach (var (name, suffix, group) in names)
        {
            var raw = new byte[15];
            for (int i = 0; i < 15; i++) raw[i] = (byte)' ';
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Array.Copy(nameBytes, 0, raw, 0, Math.Min(nameBytes.Length, 15));
            rdata.AddRange(raw);
            rdata.Add(suffix);

            ushort flags = (ushort)(group ? 0x8400 : 0x0400); // top bit = group; 0x0400 = active
            rdata.Add((byte)(flags >> 8));
            rdata.Add((byte)(flags & 0xFF));
        }

        rdata.AddRange(new byte[46]); // statistics block - present on the wire, ignored by the parser

        int declared = rdata.Count + rdLengthDelta;
        msg.Add((byte)(declared >> 8));
        msg.Add((byte)(declared & 0xFF)); // RDLENGTH (possibly perturbed)
        msg.AddRange(rdata);

        return msg.ToArray();
    }

    /// <summary>Append the encoded wildcard "*" name (0x20 length + 32 encoded bytes + root terminator).</summary>
    private static void AppendEncodedWildcardName(List<byte> msg)
    {
        msg.Add(0x20);
        var wildcard = new byte[16];
        wildcard[0] = (byte)'*';
        foreach (byte b in wildcard)
        {
            msg.Add((byte)('A' + (b >> 4)));
            msg.Add((byte)('A' + (b & 0x0F)));
        }
        msg.Add(0x00);
    }
}
