using System.Net;
using Thopter.Discovery.Net;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// The egress guard: the tool may only ever talk to LAN addresses. HttpLite and the port
/// scanner gate every outbound socket on <see cref="NetInfo.IsLanScannable"/>, so this is
/// the unit-level enforcement of open-core wall-check #3.
/// </summary>
public class EgressGuardTests
{
    [Theory]
    [Trait("Category", "WallCheck")]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.5.9", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("169.254.10.20", true)]   // link-local
    [InlineData("127.0.0.1", true)]       // loopback
    [InlineData("8.8.8.8", false)]        // public
    [InlineData("1.1.1.1", false)]        // public
    [InlineData("100.64.0.1", false)]     // CGNAT - not RFC1918, refused
    [InlineData("52.10.20.30", false)]    // public cloud
    public void IsLanScannable_only_allows_lan_destinations(string ip, bool expected)
    {
        Assert.Equal(expected, NetInfo.IsLanScannable(IPAddress.Parse(ip)));
    }
}
