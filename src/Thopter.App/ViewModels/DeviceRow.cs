using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Thopter.Discovery.Model;

namespace Thopter.App.ViewModels;

/// <summary>
/// One "discovered via" pill shown in a device row. <see cref="Accent"/> marks the source
/// worth highlighting (ONVIF - a positive camera/NVR signal).
/// </summary>
public sealed class SourceBadge
{
    public required string Text { get; init; }
    public bool Accent { get; init; }
}

/// <summary>
/// Thin, display-only wrapper around a <see cref="DiscoveredDevice"/> for the device list.
/// Devices are mutable as evidence accrues across discovery stages, so rows are updated
/// in place (<see cref="Refresh"/>) rather than replaced, keeping list scroll position stable.
/// The underlying <see cref="Device"/> is kept so the detail view and export read full evidence.
/// </summary>
public sealed partial class DeviceRow : ObservableObject
{
    /// <summary>Matches <see cref="DiscoveredDevice.Key"/>; used to find the row to update.</summary>
    public string Key { get; }

    /// <summary>The live device this row mirrors. Mutated in place by the engine.</summary>
    public DiscoveredDevice Device { get; }

    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private string _mac = "-";
    [ObservableProperty] private string _vendor = "-";
    [ObservableProperty] private string _type = "Unknown";
    [ObservableProperty] private string _model = "-";
    [ObservableProperty] private string _hostname = "-";
    [ObservableProperty] private string _ports = "-";

    /// <summary>A real MAC was resolved (not "-") - gates the "Copy MAC" context-menu item.</summary>
    [ObservableProperty] private bool _hasMac;

    /// <summary>An offline model string was fused - shows the dim second line under the type.</summary>
    [ObservableProperty] private bool _hasModel;

    [ObservableProperty] private IReadOnlyList<SourceBadge> _sourceBadges = [];

    /// <summary>No resolvable OUI vendor (unknown or randomized/virtual MAC) - rendered dimmed.</summary>
    [ObservableProperty] private bool _isUnknownVendor = true;

    /// <summary>Answered ONVIF WS-Discovery - a strong camera/NVR signal, rendered accented.</summary>
    [ObservableProperty] private bool _isOnvifConfirmed;

    public DeviceRow(DiscoveredDevice device)
    {
        Device = device;
        Key = device.Key;
        Refresh(device);
    }

    public void Refresh(DiscoveredDevice device)
    {
        Ip = device.PrimaryAddress.ToString();
        Mac = device.MacString ?? "-";
        HasMac = device.MacString is not null;
        Vendor = device.IsLocallyAdministered
            ? (device.Vendor ?? "randomized / local")
            : (device.Vendor ?? "-");
        Type = device.Type.ToString();
        Model = device.Model ?? "-";
        HasModel = !string.IsNullOrEmpty(device.Model);
        Hostname = device.Hostname ?? "-";
        Ports = device.OpenPorts.Count == 0
            ? "-"
            : string.Join(", ", device.OpenPorts.Select(p => p.ToString()));

        SourceBadges = BuildBadges(device.Sources);
        IsUnknownVendor = string.IsNullOrEmpty(device.Vendor);
        IsOnvifConfirmed = device.Sources.HasFlag(DiscoverySource.Onvif);
    }

    private static IReadOnlyList<SourceBadge> BuildBadges(DiscoverySource sources)
    {
        var badges = new List<SourceBadge>(5);
        if (sources.HasFlag(DiscoverySource.Arp)) badges.Add(new SourceBadge { Text = "ARP" });
        if (sources.HasFlag(DiscoverySource.Onvif)) badges.Add(new SourceBadge { Text = "ONVIF", Accent = true });
        if (sources.HasFlag(DiscoverySource.Ssdp)) badges.Add(new SourceBadge { Text = "SSDP" });
        if (sources.HasFlag(DiscoverySource.Mdns)) badges.Add(new SourceBadge { Text = "mDNS" });
        if (sources.HasFlag(DiscoverySource.PortScan)) badges.Add(new SourceBadge { Text = "TCP" });
        if (sources.HasFlag(DiscoverySource.Nbns)) badges.Add(new SourceBadge { Text = "NBNS" });
        return badges;
    }
}
