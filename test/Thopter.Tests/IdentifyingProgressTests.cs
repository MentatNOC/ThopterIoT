using Thopter.Discovery.Identify;
using Thopter.Discovery.Model;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Guards the live-classification path: mid-scan progress reports must carry a fused
/// device type, not the default Unknown that used to sit in the Type column until the
/// end-of-scan fusion pass.
/// </summary>
public class IdentifyingProgressTests
{
    private sealed class CapturingSink : IProgress<DiscoveredDevice>
    {
        public DiscoveredDevice? Last;
        public int Count;
        public void Report(DiscoveredDevice value) { Last = value; Count++; }
    }

    [Fact]
    public void Wrap_of_null_stays_null()
    {
        Assert.Null(IdentifyingProgress.Wrap(null));
    }

    [Fact]
    public void Onvif_device_is_classified_camera_before_the_report_reaches_the_sink()
    {
        var d = new DiscoveredDevice { Key = "ip:10.10.20.51" };
        d.Sources |= DiscoverySource.Onvif;
        d.SetAttribute("onvif.hardware", "M3014");

        var sink = new CapturingSink();
        IdentifyingProgress.Wrap(sink)!.Report(d);

        Assert.Same(d, sink.Last);
        Assert.Equal(1, sink.Count);
        Assert.Equal(DeviceType.Camera, d.Type);
        Assert.Equal("M3014", d.Model);
    }

    [Fact]
    public void Type_upgrades_across_successive_reports_as_evidence_accrues()
    {
        // First report: one weak signal (camera-vendor OUI), honest Unknown.
        var d = new DiscoveredDevice { Key = "mac:AABBCCDDEEFF" };
        d.Vendor = "Axis Communications AB";

        var sink = new CapturingSink();
        var report = IdentifyingProgress.Wrap(sink)!;

        report.Report(d);
        Assert.Equal(DeviceType.Unknown, d.Type);

        // Later stage lands a second independent signal (open RTSP): now a camera.
        d.AddOpenPort(new OpenPort { Port = 554, Service = "rtsp" });
        d.Sources |= DiscoverySource.PortScan;

        report.Report(d);
        Assert.Equal(DeviceType.Camera, d.Type);
        Assert.Equal(2, sink.Count);
    }

    [Fact]
    public void Mid_scan_report_never_takes_a_banner_vendor()
    {
        // A ping-silent host: no OUI vendor yet when its stage-C port finding is
        // reported. The banner must NOT fill in the vendor here, because the
        // neighbor-table backfill (which runs after this report) only applies the
        // authoritative OUI vendor when Vendor is still null.
        var d = new DiscoveredDevice { Key = "ip:10.10.20.99" };
        d.Sources |= DiscoverySource.PortScan;
        d.AddOpenPort(new OpenPort { Port = 80, Service = "http" });
        d.SetAttribute("http.server.80", "App-webs/");

        IdentifyingProgress.Wrap(new CapturingSink())!.Report(d);

        Assert.Null(d.Vendor);
        Assert.False(d.Attributes.ContainsKey("vendor.source"));
    }

    [Fact]
    public void Oui_vendor_from_backfill_still_wins_over_banner_at_fusion()
    {
        // Full ordering for a ping-silent on-segment host: mid-scan report (no vendor
        // taken), then the backfill lands the OUI vendor, then fusion runs the full
        // Identify. The OUI string must survive; the banner only names the firmware.
        var d = new DiscoveredDevice { Key = "ip:10.10.20.99" };
        d.Sources |= DiscoverySource.PortScan;
        d.AddOpenPort(new OpenPort { Port = 80, Service = "http" });
        d.SetAttribute("http.server.80", "App-webs/");

        var report = IdentifyingProgress.Wrap(new CapturingSink())!;
        report.Report(d);

        d.Vendor = "Rebadger Electronics Co."; // what BackfillMacsFromNeighborTable does
        DeviceIdentifier.Identify(d);          // fusion pass, banner enrichment enabled

        Assert.Equal("Rebadger Electronics Co.", d.Vendor);
        Assert.False(d.Attributes.ContainsKey("vendor.source"));
    }

    [Fact]
    public void One_banner_string_cannot_manufacture_two_camera_signals_mid_scan()
    {
        // Regression guard for the guardrail bypass: a TLS cert CN containing "axis"
        // must not both set the vendor AND count as an http hint, which would fake
        // the two independent signals the camera classification requires.
        var d = new DiscoveredDevice { Key = "ip:10.10.30.7" };
        d.Sources |= DiscoverySource.PortScan;
        d.AddOpenPort(new OpenPort { Port = 443, Service = "https" });
        d.SetAttribute("tls.cn.443", "praxis-clinic.local");

        var report = IdentifyingProgress.Wrap(new CapturingSink())!;
        report.Report(d);
        d.Vendor = "Dell Inc."; // backfill recovers the true OUI vendor
        DeviceIdentifier.Identify(d);

        Assert.Equal("Dell Inc.", d.Vendor);
        Assert.Equal(DeviceType.Unknown, d.Type);
    }
}
