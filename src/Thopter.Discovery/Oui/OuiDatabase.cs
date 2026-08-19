using System.Net.NetworkInformation;

namespace Thopter.Discovery.Oui;

/// <summary>Result of an OUI vendor lookup.</summary>
public readonly record struct OuiLookup(
    string? Vendor,
    bool IsLocallyAdministered,
    bool IsMulticast,
    string? Note)
{
    public static readonly OuiLookup Empty = new(null, false, false, null);
}

/// <summary>
/// Offline IEEE OUI → vendor lookup with longest-prefix matching across the three IEEE
/// block sizes (MA-S /36, MA-M /28, MA-L /24). Data is the embedded <c>oui.tsv</c>,
/// built from IEEE's own public CSVs (see tools/update-oui). No network access.
/// </summary>
public sealed class OuiDatabase
{
    // Key: uppercase hex prefix (6, 7, or 9 nibbles). Value: registrant/vendor name.
    private readonly Dictionary<string, string> _map;

    private OuiDatabase(Dictionary<string, string> map) => _map = map;

    public int Count => _map.Count;

    /// <summary>Load the OUI table embedded in this assembly.</summary>
    public static OuiDatabase LoadEmbedded()
    {
        var asm = typeof(OuiDatabase).Assembly;
        using Stream? s = asm.GetManifestResourceStream("Thopter.Discovery.Oui.oui.tsv");
        if (s is null)
            throw new InvalidOperationException("Embedded resource 'Thopter.Discovery.Oui.oui.tsv' was not found.");
        return Load(s);
    }

    /// <summary>Load an OUI table from a TSV stream (<c>PREFIXHEX\tVendor</c> per line).</summary>
    public static OuiDatabase Load(Stream tsv)
    {
        ArgumentNullException.ThrowIfNull(tsv);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        using var reader = new StreamReader(tsv);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab >= line.Length - 1) continue;

            string prefix = line.AsSpan(0, tab).ToString().ToUpperInvariant();
            string vendor = line.AsSpan(tab + 1).Trim().ToString();
            if (prefix.Length is 6 or 7 or 9 && vendor.Length > 0)
                map[prefix] = vendor;
        }

        return new OuiDatabase(map);
    }

    /// <summary>Look up the vendor for a MAC, honoring the locally-administered and multicast bits.</summary>
    public OuiLookup Lookup(PhysicalAddress mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return Lookup(mac.GetAddressBytes());
    }

    public OuiLookup Lookup(ReadOnlySpan<byte> mac)
    {
        if (mac.Length < 3) return OuiLookup.Empty;

        bool multicast = (mac[0] & 0x01) != 0;
        bool localAdmin = (mac[0] & 0x02) != 0;

        // Locally-administered addresses are not IEEE-registered (VMs, randomized privacy MACs).
        if (localAdmin)
            return new OuiLookup(null, IsLocallyAdministered: true, multicast, "Locally administered / randomized MAC");

        Span<char> hex = stackalloc char[12];
        int n = Math.Min(mac.Length, 6);
        for (int i = 0; i < n; i++)
        {
            hex[i * 2] = ToHex(mac[i] >> 4);
            hex[i * 2 + 1] = ToHex(mac[i] & 0xF);
        }
        var hexStr = new string(hex[..(n * 2)]);

        // Longest-prefix first: MA-S (/36, 9 nibbles) → MA-M (/28, 7) → MA-L (/24, 6).
        if (hexStr.Length >= 9 && _map.TryGetValue(hexStr[..9], out var v36))
            return new OuiLookup(v36, false, multicast, null);
        if (hexStr.Length >= 7 && _map.TryGetValue(hexStr[..7], out var v28))
            return new OuiLookup(v28, false, multicast, null);
        if (hexStr.Length >= 6 && _map.TryGetValue(hexStr[..6], out var v24))
            return new OuiLookup(v24, false, multicast, null);

        return new OuiLookup(null, false, multicast, null);
    }

    private static char ToHex(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
}
