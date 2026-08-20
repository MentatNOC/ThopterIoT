using System.Net;
using System.Net.NetworkInformation;
using Thopter.App.ViewModels;
using Thopter.Discovery.Model;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Maps a <see cref="DiscoveredDevice"/> into a <see cref="DeviceRow"/> and checks the
/// display fields the grid binds to - Hostname and Model columns plus the context-menu gate.
/// </summary>
public class DeviceRowTests
{
    [Fact]
    public void Refresh_maps_hostname_and_model_when_present()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.5" };
        device.AddAddress(IPAddress.Parse("10.10.10.5"));
        device.Hostname = "CAM-LOBBY";
        device.Model = "AXIS M3014";
        device.Mac = PhysicalAddress.Parse("1C-FC-17-10-05-98");

        var row = new DeviceRow(device);

        Assert.Equal("CAM-LOBBY", row.Hostname);
        Assert.Equal("AXIS M3014", row.Model);
        Assert.True(row.HasModel);
        Assert.True(row.HasMac);
        // The MAC string is the "Copy MAC" clipboard payload - assert the formatted value, not just the gate.
        Assert.Equal(device.MacString, row.Mac);
        Assert.Equal("1C:FC:17:10:05:98", row.Mac);
    }

    [Fact]
    public void Refresh_uses_placeholders_when_hostname_model_and_mac_absent()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.6" };
        device.AddAddress(IPAddress.Parse("10.10.10.6"));

        var row = new DeviceRow(device);

        Assert.Equal("-", row.Hostname);
        Assert.Equal("-", row.Model);
        Assert.False(row.HasModel);
        Assert.Equal("-", row.Mac);
        Assert.False(row.HasMac);
    }

    [Fact]
    public void Refresh_adds_the_nbns_badge_when_the_name_came_from_netbios()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.7" };
        device.AddAddress(IPAddress.Parse("10.10.10.7"));
        device.Hostname = "DESKTOP-01";
        device.Sources = DiscoverySource.Nbns;

        var row = new DeviceRow(device);

        Assert.Contains(row.SourceBadges, b => b.Text == "NBNS");
    }

    [Fact]
    public void Spice_sweep_arms_once_when_a_row_becomes_a_camera_and_never_rearms()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.8" };
        device.AddAddress(IPAddress.Parse("10.10.10.8"));

        var row = new DeviceRow(device);
        Assert.False(row.SpiceSweepPending);

        // Evidence accrues mid-scan: the ONVIF answer arrives and the row refreshes.
        device.Sources = DiscoverySource.Onvif;
        row.Refresh(device);
        Assert.True(row.SpiceSweepPending);

        // The view plays the gust and consumes the cue; later refreshes must not re-arm it.
        row.ConsumeSpiceSweep();
        Assert.False(row.SpiceSweepPending);
        row.Refresh(device);
        Assert.False(row.SpiceSweepPending);
    }

    [Fact]
    public void Spice_sweep_arms_for_a_fused_camera_type_without_onvif()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.9" };
        device.AddAddress(IPAddress.Parse("10.10.10.9"));
        device.Type = DeviceType.Camera;

        var row = new DeviceRow(device);

        Assert.True(row.SpiceSweepPending);
        Assert.False(row.IsOnvifConfirmed);
    }

    [Fact]
    public void Spice_sweep_arms_for_a_fused_nvr_type_without_onvif()
    {
        // A recorder found by port signature alone (e.g. DVRIP 34567 + RTSP) is typed Nvr
        // with no ONVIF source; the gust keys on what the device is, not which protocol
        // found it, so it must arm the same as an ONVIF-answering recorder.
        var device = new DiscoveredDevice { Key = "ip:10.10.10.11" };
        device.AddAddress(IPAddress.Parse("10.10.10.11"));
        device.Type = DeviceType.Nvr;

        var row = new DeviceRow(device);

        Assert.True(row.SpiceSweepPending);
        Assert.False(row.IsOnvifConfirmed);
    }

    [Fact]
    public void Spice_sweep_stays_unarmed_for_non_camera_devices()
    {
        var device = new DiscoveredDevice { Key = "ip:10.10.10.10" };
        device.AddAddress(IPAddress.Parse("10.10.10.10"));
        device.Type = DeviceType.Printer;
        device.Sources = DiscoverySource.Arp | DiscoverySource.Ssdp;

        var row = new DeviceRow(device);
        row.Refresh(device);

        Assert.False(row.SpiceSweepPending);
    }
}
