using System.Buffers.Binary;
using System.Net;
using Thopter.Discovery;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// The scan-scope filter must test membership by subnet mask, not by the enumerated host
/// list (which is capped at MaxHostsPerSubnet). Otherwise a legitimate same-subnet device
/// beyond the cap - e.g. a camera answering multicast on a flat /16 - is wrongly dropped.
/// </summary>
public class ScopeSubnetTests
{
    private static uint ToUInt(string ip) =>
        BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(ip).GetAddressBytes());

    private static bool InScope(DiscoveryOptions options, string ip)
    {
        uint v = ToUInt(ip);
        foreach (var (network, mask) in options.ResolveTargetSubnets())
            if ((v & mask) == network) return true;
        return false;
    }

    [Fact]
    public void Slash16_includes_hosts_far_beyond_the_enumeration_cap()
    {
        // 10.0.0.0/16 is ~65k addresses; host enumeration stops at 8192 (~10.0.31.255).
        // A camera at 10.0.40.10 is in-subnet and must count as in scope.
        var options = new DiscoveryOptions { Cidrs = new[] { "10.0.0.0/16" } };

        Assert.True(InScope(options, "10.0.40.10"));
        Assert.True(InScope(options, "10.0.0.1"));
    }

    [Fact]
    public void Scope_excludes_addresses_in_other_subnets()
    {
        var options = new DiscoveryOptions { Cidrs = new[] { "10.10.20.0/24" } };

        Assert.True(InScope(options, "10.10.20.51"));
        Assert.False(InScope(options, "192.168.0.29")); // another NIC's segment
        Assert.False(InScope(options, "10.10.10.1"));   // adjacent lab subnet
    }

    [Fact]
    public void Scan_all_networks_covers_every_listed_subnet()
    {
        var options = new DiscoveryOptions
        {
            Cidrs = new[] { "10.10.20.0/24", "192.168.0.0/24" },
            RestrictToTargetSubnets = false,
        };

        // Both subnets resolve; membership holds for a host in each.
        Assert.True(InScope(options, "10.10.20.51"));
        Assert.True(InScope(options, "192.168.0.29"));
    }
}
