using System.Net.NetworkInformation;
using System.Text;
using Thopter.Discovery.Oui;
using Xunit;

namespace Thopter.Tests;

public class OuiDatabaseTests
{
    // A tiny in-memory table exercising all three IEEE block sizes plus a shared /24
    // prefix that MA-M/MA-S refine (to prove longest-prefix wins).
    private static OuiDatabase BuildDb()
    {
        const string tsv =
            "001A2B\tAcme Cameras Inc\n" +          // MA-L /24
            "8C1F64\tShared MA-L Holder\n" +        // MA-L /24 (parent of the MA-M/MA-S below)
            "8C1F64A\tMedium Block Vendor\n" +      // MA-M /28
            "8C1F64AFA\tSmall Block Vendor\n";      // MA-S /36
        return OuiDatabase.Load(new MemoryStream(Encoding.UTF8.GetBytes(tsv)));
    }

    private static PhysicalAddress Mac(string hex) => PhysicalAddress.Parse(hex);

    [Fact]
    public void MaL_prefix_matches()
    {
        var db = BuildDb();
        Assert.Equal("Acme Cameras Inc", db.Lookup(Mac("001A2B334455")).Vendor);
    }

    [Fact]
    public void Longest_prefix_wins_MaS_over_MaM_over_MaL()
    {
        var db = BuildDb();
        // 8C:1F:64:AF:Ax -> matches the /36 small block
        Assert.Equal("Small Block Vendor", db.Lookup(Mac("8C1F64AFA123")).Vendor);
        // 8C:1F:64:Ax (but not AFA) -> falls back to the /28 medium block
        Assert.Equal("Medium Block Vendor", db.Lookup(Mac("8C1F64AB1234")).Vendor);
        // 8C:1F:64:xx (not A-block) -> falls back to the /24 MA-L holder
        Assert.Equal("Shared MA-L Holder", db.Lookup(Mac("8C1F64012345")).Vendor);
    }

    [Fact]
    public void Unknown_prefix_returns_null_vendor()
    {
        var db = BuildDb();
        var result = db.Lookup(Mac("FA1234567890"));
        // FA has the locally-administered bit set, so it's reported as LAA, not "unknown OUI".
        Assert.Null(result.Vendor);
        Assert.True(result.IsLocallyAdministered);
    }

    [Fact]
    public void Globally_unique_but_unregistered_returns_null_without_laa_flag()
    {
        var db = BuildDb();
        // 10:.. -> globally administered (bit 0x02 clear) but not in our tiny table.
        var result = db.Lookup(Mac("101112131415"));
        Assert.Null(result.Vendor);
        Assert.False(result.IsLocallyAdministered);
    }

    [Fact]
    public void Locally_administered_bit_is_detected()
    {
        var db = BuildDb();
        // 02:.. has the LAA bit set.
        var result = db.Lookup(Mac("020000000001"));
        Assert.True(result.IsLocallyAdministered);
        Assert.Null(result.Vendor);
        Assert.Contains("Locally administered", result.Note);
    }

    [Fact]
    public void Multicast_bit_is_detected()
    {
        var db = BuildDb();
        // 01:00:5E:.. multicast MAC.
        var result = db.Lookup(Mac("01005E000001"));
        Assert.True(result.IsMulticast);
    }

    [Fact]
    public void Embedded_table_loads_and_has_real_vendors()
    {
        var db = OuiDatabase.LoadEmbedded();
        Assert.True(db.Count > 40_000, $"expected the full IEEE table, got {db.Count} records");
        // Well-known camera vendor OUIs.
        Assert.Contains("Axis", db.Lookup(Mac("00408C112233")).Vendor);
        Assert.Contains("Hikvision", db.Lookup(Mac("00BC99112233")).Vendor);
    }
}
