using Thopter.Discovery.Identify;
using Thopter.Discovery.Model;
using Xunit;

namespace Thopter.Tests;

public class DeviceIdentifierTests
{
    private static DiscoveredDevice Device(string ip = "192.168.1.10") =>
        new() { Key = "ip:" + ip };

    [Fact]
    public void Onvif_response_alone_classifies_as_camera_and_takes_hardware_model()
    {
        var d = Device();
        d.Sources |= DiscoverySource.Onvif;
        d.SetAttribute("onvif.present", "true");
        d.SetAttribute("onvif.hardware", "M3014");

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Camera, d.Type);
        Assert.Equal("M3014", d.Model);
    }

    [Fact]
    public void Rtsp_plus_camera_vendor_is_two_signals_and_classifies_as_camera()
    {
        var d = Device();
        d.Vendor = "Hangzhou Hikvision Digital Technology Co.,Ltd.";
        d.AddOpenPort(new OpenPort { Port = 554, Service = "rtsp" });
        d.Sources |= DiscoverySource.PortScan;

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Camera, d.Type);
    }

    [Fact]
    public void Camera_vendor_alone_is_one_signal_and_stays_unknown()
    {
        // The >=2 corroborating-signals rule: an Axis OUI by itself is not enough.
        var d = Device();
        d.Vendor = "Axis Communications AB";

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Unknown, d.Type);
    }

    [Fact]
    public void Ipp_mdns_service_classifies_as_printer()
    {
        var d = Device();
        d.AddMdnsService("_ipp._tcp");
        d.Sources |= DiscoverySource.Mdns;

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Printer, d.Type);
    }

    [Fact]
    public void Network_vendor_without_camera_signals_classifies_as_network_gear()
    {
        var d = Device();
        d.Vendor = "Fortinet, Inc.";

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.NetworkGear, d.Type);
    }

    [Fact]
    public void Model_precedence_prefers_onvif_hardware_over_ssdp_model()
    {
        var d = Device();
        d.Sources |= DiscoverySource.Onvif;
        d.SetAttribute("onvif.present", "true");
        d.SetAttribute("onvif.hardware", "AXIS-M3014");
        d.SetAttribute("upnp.modelName", "Generic UPnP Cam");

        DeviceIdentifier.Identify(d);

        Assert.Equal("AXIS-M3014", d.Model);
    }

    [Fact]
    public void Rtsp_response_plus_web_ui_classifies_as_camera_on_routed_scan()
    {
        // A routed sweep has no MAC/OUI, but an RTSP endpoint that spoke RTSP alongside a
        // web UI is unmistakably a camera/encoder.
        var d = Device();
        d.AddOpenPort(new OpenPort { Port = 554, Service = "rtsp" });
        d.AddOpenPort(new OpenPort { Port = 80, Service = "http" });
        d.SetAttribute("rtsp.server.554", "RTSP OPTIONS: DESCRIBE, SETUP, PLAY, TEARDOWN");
        d.Sources |= DiscoverySource.PortScan;

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Camera, d.Type);
    }

    [Fact]
    public void VideoJet_web_banner_recovers_bosch_vendor_without_oui()
    {
        var d = Device();
        d.AddOpenPort(new OpenPort { Port = 80, Service = "http" });
        d.AddOpenPort(new OpenPort { Port = 554, Service = "rtsp" });
        d.SetAttribute("http.server.80", "VCS-VideoJet-Webserver");
        d.SetAttribute("rtsp.server.554", "RTSP OPTIONS: DESCRIBE, PLAY");

        DeviceIdentifier.Identify(d);

        Assert.Equal("Bosch", d.Vendor);
        Assert.Equal(DeviceType.Camera, d.Type);
    }

    [Fact]
    public void Dvrip_port_marks_device_as_nvr()
    {
        var d = Device();
        d.Sources |= DiscoverySource.Onvif;
        d.SetAttribute("onvif.present", "true");
        d.AddOpenPort(new OpenPort { Port = 34567, Service = "dvrip" });

        DeviceIdentifier.Identify(d);

        Assert.Equal(DeviceType.Nvr, d.Type);
    }
}
