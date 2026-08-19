using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Thopter.Discovery.Model;

namespace Thopter.App.Json;

/// <summary>
/// Flat, JSON-friendly projection of a <see cref="DiscoveredDevice"/> for the headless
/// <c>scan --json</c> mode. Serialized only via <see cref="ScanJsonContext"/> (source-gen) —
/// never reflection-based, so it stays NativeAOT-safe.
/// </summary>
public sealed class ScanResultDevice
{
    public string Ip { get; set; } = "";
    public string? Mac { get; set; }
    public string? Vendor { get; set; }
    public string Type { get; set; } = "";
    public string? Model { get; set; }
    public string? Hostname { get; set; }
    public string Sources { get; set; } = "";
    public List<int> Ports { get; set; } = new();
    public string? Note { get; set; }

    public static ScanResultDevice FromDevice(DiscoveredDevice device) => new()
    {
        Ip = device.PrimaryAddress.ToString(),
        Mac = device.MacString,
        Vendor = device.Vendor,
        Type = device.Type.ToString(),
        Model = device.Model,
        Hostname = device.Hostname,
        Sources = device.Sources.ToString(),
        Ports = device.OpenPorts.Select(p => p.Port).ToList(),
        Note = device.Note,
    };
}

/// <summary>
/// Source-generated serialization context for the headless scan output. Required for
/// NativeAOT — reflection-based <c>JsonSerializer</c> is not used anywhere in this app.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ScanResultDevice>))]
public partial class ScanJsonContext : JsonSerializerContext
{
}
