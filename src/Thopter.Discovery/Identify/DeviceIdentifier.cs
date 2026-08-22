using Thopter.Discovery.Model;

namespace Thopter.Discovery.Identify;

/// <summary>
/// Offline, deterministic, rule-based device classification. No network calls, no cloud,
/// no machine learning. It fuses the evidence already gathered (OUI vendor, ONVIF scopes,
/// SSDP/mDNS advertisements, open ports and light banners) into a type + best model.
///
/// Guardrail: a device type is asserted only from a strong signal (an ONVIF WS-Discovery
/// response) or from at least two independent corroborating signals - never from one weak hint.
/// </summary>
public static class DeviceIdentifier
{
    private static readonly string[] CameraVendors =
    {
        "axis", "hikvision", "dahua", "hanwha", "samsung techwin", "bosch", "i-pro",
        "vivotek", "uniview", "reolink", "amcrest", "geovision", "mobotix", "avigilon",
        "pelco", "acti", "lorex", "wyze", "ubiquiti"  // (ubiquiti also makes UniFi Protect cameras)
    };

    private static readonly string[] NetworkVendors =
    {
        "fortinet", "cisco", "meraki", "mikrotik", "netgear", "tp-link", "aruba",
        "juniper", "ruckus", "palo alto", "zyxel", "d-link", "sonicwall", "extreme",
        // residential gateways / cable modems / routers
        "arris", "commscope", "technicolor", "arcadyan", "sagemcom", "actiontec", "pace plc"
    };

    private static readonly string[] CameraHttpHints =
    {
        "camera", "ipcam", "ip camera", "network camera", "netcam", "webcam",
        "nvr", "dvr", "hikvision", "dahua", "axis", "webservice", "goahead-webs",
        "videojet", "vcs-videojet", "dvrdvs", "app-webs", "netsurveillance", "wisenet", "sunapi"
    };

    // Known embedded web-server / banner signatures → vendor. Used when a routed scan has
    // no MAC/OUI to go on. Kept conservative to avoid false vendor attribution.
    private static readonly (string Signature, string Vendor)[] BannerVendors =
    {
        ("videojet", "Bosch"), ("vcs-videojet", "Bosch"),
        ("dvrdvs", "Hikvision"), ("app-webs", "Hikvision"), ("hikvision", "Hikvision"),
        ("dahua", "Dahua"),
        ("axis", "Axis Communications"),
        ("wisenet", "Hanwha"), ("sunapi", "Hanwha"), ("hanwha", "Hanwha"),
    };

    /// <param name="enrichVendorFromBanner">
    /// Allow a web-server banner to fill in a missing vendor. Only the end-of-scan fusion
    /// pass may pass true: mid-scan calls run before the post-portscan neighbor-table
    /// backfill has attached OUI vendors, and a banner guess taken then would permanently
    /// outrank the authoritative OUI (both the backfill and the enricher defer to an
    /// already-set vendor). It would also let one banner string count as two "independent"
    /// camera signals, defeating the two-signal guardrail.
    /// </param>
    public static void Identify(DiscoveredDevice device, bool enrichVendorFromBanner = true)
    {
        // 1. Best model, by source precedence (most authoritative first).
        string? model = FirstAttribute(device,
            "onvif.hardware", "upnp.modelName", "mdns.txt.md", "upnp.modelNumber");
        if (model is not null) device.Model = model;

        // 1b. On a routed scan there's no OUI vendor - try to recover it from web-server banners.
        if (enrichVendorFromBanner) EnrichVendorFromBanner(device);

        // 2. Gather independent signals.
        bool onvif = device.Sources.HasFlag(DiscoverySource.Onvif) || device.Attributes.ContainsKey("onvif.present");
        bool rtspOpen = device.OpenPorts.Any(p => p.Port == 554);
        bool rtspSpoke = device.Attributes.Keys.Any(k => k.StartsWith("rtsp.", StringComparison.OrdinalIgnoreCase));
        bool webOpen = device.OpenPorts.Any(p => p.Service is "http" or "https");
        bool vendorCam = ContainsAny(device.Vendor, CameraVendors);
        bool mdnsCam = device.MdnsServices.Any(s =>
            s.Contains("rtsp", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("onvif", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("axis-video", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("dahua", StringComparison.OrdinalIgnoreCase));
        bool httpCam = device.Attributes
            .Where(kv => kv.Key.StartsWith("http.", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.StartsWith("tls.cn", StringComparison.OrdinalIgnoreCase)
                      || kv.Key.StartsWith("rtsp.", StringComparison.OrdinalIgnoreCase))
            .Any(kv => ContainsAny(kv.Value, CameraHttpHints));

        int cameraSignals = (rtspOpen ? 1 : 0) + (vendorCam ? 1 : 0) + (mdnsCam ? 1 : 0) + (httpCam ? 1 : 0);

        // An RTSP endpoint that actually spoke RTSP, alongside a web UI, is unmistakably a
        // camera/encoder/NVR - the routed-scan equivalent of a strong signal.
        bool cameraByRtsp = rtspSpoke && (webOpen || vendorCam || httpCam || mdnsCam);

        bool printer = device.MdnsServices.Any(s =>
            s.Contains("ipp", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("printer", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("pdl-datastream", StringComparison.OrdinalIgnoreCase));
        bool netGear = ContainsAny(device.Vendor, NetworkVendors);
        bool nvr = LooksLikeNvr(device);

        // 3. Classify. ONVIF or a live RTSP+web appliance is self-corroborating; otherwise
        //    require two independent camera signals before asserting "camera".
        if (onvif || cameraByRtsp || cameraSignals >= 2)
        {
            device.Type = nvr ? DeviceType.Nvr : DeviceType.Camera;
        }
        else if (printer)
        {
            device.Type = DeviceType.Printer;
        }
        else if (netGear && cameraSignals == 0)
        {
            device.Type = DeviceType.NetworkGear;
        }
        else
        {
            device.Type = DeviceType.Unknown;
        }
    }

    private static bool LooksLikeNvr(DiscoveredDevice device)
    {
        if (device.OpenPorts.Any(p => p.Port == 34567)) return true; // DVRIP (Xiongmai-style recorders)

        if (device.Attributes.TryGetValue("onvif.type", out var t) &&
            (t.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
             t.Contains("Recorder", StringComparison.OrdinalIgnoreCase)))
            return true;

        foreach (var s in new[] { device.Model, device.Hostname, Attr(device, "onvif.name") })
        {
            if (s is null) continue;
            if (s.Contains("NVR", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("DVR", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("recorder", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnrichVendorFromBanner(DiscoveredDevice device)
    {
        if (!string.IsNullOrEmpty(device.Vendor)) return; // OUI / protocol vendor already wins

        foreach (var kv in device.Attributes)
        {
            if (!(kv.Key.StartsWith("http.", StringComparison.OrdinalIgnoreCase)
                  || kv.Key.StartsWith("tls.cn", StringComparison.OrdinalIgnoreCase)
                  || kv.Key.StartsWith("rtsp.", StringComparison.OrdinalIgnoreCase)))
                continue;

            foreach (var (signature, vendor) in BannerVendors)
            {
                if (kv.Value.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    device.Vendor = vendor;
                    device.SetAttribute("vendor.source", "banner:" + signature);
                    return;
                }
            }
        }
    }

    private static bool ContainsAny(string? haystack, string[] needles)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? FirstAttribute(DiscoveredDevice device, params string[] keys)
    {
        foreach (var key in keys)
            if (device.Attributes.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    private static string? Attr(DiscoveredDevice device, string key) =>
        device.Attributes.TryGetValue(key, out var v) ? v : null;
}
