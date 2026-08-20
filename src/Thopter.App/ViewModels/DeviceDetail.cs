using System.Collections.Generic;
using System.Linq;
using Thopter.Discovery.Model;

namespace Thopter.App.ViewModels;

/// <summary>
/// Read-only snapshot of one device's full evidence, shown in the detail flyout on
/// double-click. Everything is pre-formatted to plain strings so the view is simple
/// ItemsControls with compiled bindings - no converters, no reflection.
/// </summary>
public sealed class DeviceDetail
{
    public string Title { get; }
    public string Subtitle { get; }

    public string Mac { get; }
    public string Vendor { get; }
    public string Type { get; }
    public string Model { get; }
    public string Hostname { get; }
    public string Sources { get; }
    public string MacKind { get; }

    public IReadOnlyList<string> Addresses { get; }
    public IReadOnlyList<string> Ports { get; }
    public IReadOnlyList<string> OnvifScopes { get; }
    public IReadOnlyList<string> MdnsServices { get; }
    public IReadOnlyList<string> Attributes { get; }
    public string Note { get; }

    public bool HasAddresses => Addresses.Count > 0;
    public bool HasPorts => Ports.Count > 0;
    public bool HasOnvifScopes => OnvifScopes.Count > 0;
    public bool HasMdnsServices => MdnsServices.Count > 0;
    public bool HasAttributes => Attributes.Count > 0;
    public bool HasNote => Note.Length > 0;

    public DeviceDetail(DiscoveredDevice d)
    {
        string ip = d.PrimaryAddress.ToString();
        Title = ip;
        Subtitle = string.Join(" · ", new[]
        {
            d.Type == DeviceType.Unknown ? null : d.Type.ToString(),
            d.Vendor,
        }.Where(s => !string.IsNullOrEmpty(s)));

        Mac = d.MacString ?? "-";
        Vendor = d.Vendor ?? "-";
        Type = d.Type.ToString();
        Model = d.Model ?? "-";
        Hostname = d.Hostname ?? "-";
        Sources = d.Sources == DiscoverySource.None ? "-" : d.Sources.ToString();
        MacKind = d.IsLocallyAdministered ? "locally administered (randomized / virtual)" : "universal (real OUI)";

        Addresses = d.Addresses.Select(a => a.ToString()).ToList();
        Ports = d.OpenPorts
            .OrderBy(p => p.Port)
            .Select(FormatPort)
            .ToList();
        OnvifScopes = d.OnvifScopes.ToList();
        MdnsServices = d.MdnsServices.ToList();
        Attributes = d.Attributes
            .OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} = {kv.Value}")
            .ToList();
        Note = d.Note ?? "";
    }

    private static string FormatPort(OpenPort p)
    {
        string head = p.Service is null ? p.Port.ToString() : $"{p.Port}/{p.Service}";
        return p.Banner is null ? head : $"{head}  -  {p.Banner}";
    }
}
