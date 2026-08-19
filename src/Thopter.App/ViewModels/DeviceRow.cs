using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Thopter.Discovery.Model;

namespace Thopter.App.ViewModels;

/// <summary>
/// Thin, display-only wrapper around a <see cref="DiscoveredDevice"/> for the device list.
/// Devices are mutable as evidence accrues across discovery stages, so rows are updated
/// in place (<see cref="Refresh"/>) rather than replaced, keeping list scroll position stable.
/// </summary>
public sealed partial class DeviceRow : ObservableObject
{
    /// <summary>Matches <see cref="DiscoveredDevice.Key"/>; used to find the row to update.</summary>
    public string Key { get; }

    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private string _mac = "-";
    [ObservableProperty] private string _vendor = "-";
    [ObservableProperty] private string _type = "Unknown";
    [ObservableProperty] private string _sources = "-";
    [ObservableProperty] private string _ports = "-";

    public DeviceRow(DiscoveredDevice device)
    {
        Key = device.Key;
        Refresh(device);
    }

    public void Refresh(DiscoveredDevice device)
    {
        Ip = device.PrimaryAddress.ToString();
        Mac = device.MacString ?? "-";
        Vendor = device.Vendor ?? "-";
        Type = device.Type.ToString();
        Sources = device.Sources == DiscoverySource.None ? "-" : device.Sources.ToString();
        Ports = device.OpenPorts.Count == 0
            ? "-"
            : string.Join(", ", device.OpenPorts.Select(p => p.ToString()));
    }
}
