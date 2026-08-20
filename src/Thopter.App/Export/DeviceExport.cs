using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Thopter.App.Json;
using Thopter.Discovery.Model;

namespace Thopter.App.Export;

/// <summary>
/// Serializes scan results to CSV or JSON for the GUI export. Pure string producers with
/// no file IO, so they are unit-testable and the View owns only the file picker. JSON goes
/// through the source-generated <see cref="ScanJsonContext"/> - never reflection - so the
/// whole app stays NativeAOT-safe.
/// </summary>
public static class DeviceExport
{
    public const string CsvHeader =
        "IP,MAC,Vendor,LocallyAdministered,Type,Model,Hostname,Sources,OpenPorts,OnvifScopes,MdnsServices,Note";

    public static string ToJson(IEnumerable<DiscoveredDevice> devices)
    {
        var dto = devices.Select(ScanResultDevice.FromDevice).ToList();
        return JsonSerializer.Serialize(dto, ScanJsonContext.Default.ListScanResultDevice);
    }

    public static string ToCsv(IEnumerable<DiscoveredDevice> devices)
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append("\r\n");

        foreach (var d in devices)
        {
            string ports = string.Join("; ", d.OpenPorts.Select(p =>
                p.Service is null ? p.Port.ToString() : $"{p.Port}/{p.Service}"));

            sb.Append(Escape(d.PrimaryAddress.ToString())).Append(',');
            sb.Append(Escape(d.MacString)).Append(',');
            sb.Append(Escape(d.Vendor)).Append(',');
            sb.Append(d.IsLocallyAdministered ? "yes" : "no").Append(',');
            sb.Append(Escape(d.Type.ToString())).Append(',');
            sb.Append(Escape(d.Model)).Append(',');
            sb.Append(Escape(d.Hostname)).Append(',');
            sb.Append(Escape(d.Sources.ToString())).Append(',');
            sb.Append(Escape(ports)).Append(',');
            sb.Append(Escape(string.Join("; ", d.OnvifScopes))).Append(',');
            sb.Append(Escape(string.Join("; ", d.MdnsServices))).Append(',');
            sb.Append(Escape(d.Note)).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>RFC 4180 field escaping: quote when the value holds a comma, quote, CR or LF; double interior quotes.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!mustQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
