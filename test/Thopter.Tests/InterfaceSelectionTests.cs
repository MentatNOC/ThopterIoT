using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using Thopter.App.ViewModels;
using Thopter.Discovery.Net;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Selection policy for the live-refreshed interface dropdown: the user's pick must
/// survive NIC hot-plug events, DHCP address moves, and disappearing adapters sanely.
/// </summary>
public class InterfaceSelectionTests
{
    private static NetworkInterfaceInfo Nic(string id, string ip) => new()
    {
        Id = id,
        Name = id,
        Description = id,
        InterfaceType = NetworkInterfaceType.Ethernet,
        HostAddress = IPAddress.Parse(ip),
        PrefixLength = 24,
    };

    [Fact]
    public void Keeps_the_current_selection_when_still_present()
    {
        var wifi = Nic("wifi", "192.168.0.10");
        var usb = Nic("usb", "10.10.20.53");
        var candidates = new List<NetworkInterfaceInfo> { wifi, usb };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), wifi, candidates);

        Assert.Same(usb, picked);
    }

    [Fact]
    public void Keeps_the_same_adapter_when_dhcp_moved_its_address()
    {
        // A real primary is present and in the list: the renewed same-adapter entry must
        // still win over it, pinning the same-adapter-before-primary tier order.
        var wifi = Nic("wifi", "192.168.0.10");
        var usbRenewed = Nic("usb", "10.10.20.77");
        var candidates = new List<NetworkInterfaceInfo> { wifi, usbRenewed };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), wifi, candidates);

        Assert.Same(usbRenewed, picked);
    }

    [Fact]
    public void Exact_address_wins_over_an_earlier_entry_of_the_same_adapter()
    {
        // Multi-homed adapter: one candidate per address, same Id. The exact address must
        // win even when the other entry enumerates first.
        var otherAddress = Nic("usb", "10.10.20.99");
        var exact = Nic("usb", "10.10.20.53");
        var candidates = new List<NetworkInterfaceInfo> { otherAddress, exact };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), null, candidates);

        Assert.Same(exact, picked);
    }

    [Fact]
    public void Dhcp_renewal_on_a_multi_homed_adapter_stays_on_the_same_subnet()
    {
        // The adapter carries a static lab address and a DHCP corporate address. When the
        // corporate lease renews to a new IP, the selection must follow the renewed address
        // on its own subnet, not silently retarget to the adapter's lab subnet.
        var labAddress = Nic("usb", "10.10.10.5");
        var renewed = Nic("usb", "192.168.0.77");
        var candidates = new List<NetworkInterfaceInfo> { labAddress, renewed };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "192.168.0.53"), null, candidates);

        Assert.Same(renewed, picked);
    }

    [Fact]
    public void Falls_back_to_the_primary_when_the_selection_was_unplugged()
    {
        // The primary is NOT first in the list, pinning the primary-before-first tier.
        var lab = Nic("lab", "10.10.10.50");
        var wifi = Nic("wifi", "192.168.0.10");
        var candidates = new List<NetworkInterfaceInfo> { lab, wifi };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), Nic("wifi", "192.168.0.10"), candidates);

        Assert.Same(wifi, picked);
    }

    [Fact]
    public void Falls_back_to_the_first_candidate_when_nothing_matches()
    {
        var lab = Nic("lab", "10.10.10.50");
        var candidates = new List<NetworkInterfaceInfo> { lab, Nic("wifi", "192.168.0.10") };

        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), Nic("gone", "172.16.0.1"), candidates);

        Assert.Same(lab, picked);
    }

    [Fact]
    public void Returns_null_when_no_interfaces_remain()
    {
        var picked = MainWindowViewModel.PickInterfaceSelection(Nic("usb", "10.10.20.53"), null, new List<NetworkInterfaceInfo>());

        Assert.Null(picked);
    }
}
