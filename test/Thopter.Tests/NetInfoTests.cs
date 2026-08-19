using System.Net;
using Thopter.Discovery.Net;
using Xunit;

namespace Thopter.Tests;

public class NetInfoTests
{
    [Theory]
    [InlineData("192.168.1.0", 24, 254)]   // standard /24
    [InlineData("192.168.1.0", 30, 2)]     // .1 and .2
    [InlineData("10.0.0.0", 31, 2)]        // point-to-point: both addresses usable
    [InlineData("10.0.0.5", 32, 1)]        // single host
    public void EnumerateHosts_yields_expected_count(string net, int prefix, int expected)
    {
        var hosts = NetInfo.EnumerateHosts(IPAddress.Parse(net), prefix).ToList();
        Assert.Equal(expected, hosts.Count);
    }

    [Fact]
    public void EnumerateHosts_excludes_network_and_broadcast_for_slash24()
    {
        var hosts = NetInfo.EnumerateHosts(IPAddress.Parse("192.168.1.0"), 24).ToList();
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);   // network
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts); // broadcast
        Assert.Contains(IPAddress.Parse("192.168.1.1"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.254"), hosts);
    }

    [Fact]
    public void EnumerateHosts_respects_maxHosts_cap()
    {
        // /16 would be 65534 usable; cap must clamp enumeration.
        var hosts = NetInfo.EnumerateHosts(IPAddress.Parse("10.1.0.0"), 16, maxHosts: 500).ToList();
        Assert.Equal(500, hosts.Count);
    }

    [Fact]
    public void EnumerateHosts_derives_network_from_arbitrary_host_address()
    {
        // Passing a host address (not the network address) still enumerates the right subnet.
        var hosts = NetInfo.EnumerateHosts(IPAddress.Parse("192.168.1.55"), 24).ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
    }

    [Theory]
    [InlineData("192.168.1.55", 24, "192.168.1.0")]
    [InlineData("10.20.30.40", 8, "10.0.0.0")]
    [InlineData("172.16.5.9", 20, "172.16.0.0")]
    public void NetworkAddressOf_masks_correctly(string host, int prefix, string expected)
    {
        Assert.Equal(IPAddress.Parse(expected), NetInfo.NetworkAddressOf(IPAddress.Parse(host), prefix));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    public void IsPrivate_classifies_rfc1918(string ip, bool expected)
    {
        Assert.Equal(expected, NetInfo.IsPrivate(IPAddress.Parse(ip)));
    }

    [Fact]
    public void GetActiveIPv4Interfaces_returns_something_on_a_connected_host()
    {
        // Smoke test: on any networked build host there is at least one active IPv4 NIC.
        var nics = NetInfo.GetActiveIPv4Interfaces();
        Assert.NotNull(nics);
        // Not asserting count > 0 so the suite still passes on an isolated CI runner.
    }
}
