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
}
