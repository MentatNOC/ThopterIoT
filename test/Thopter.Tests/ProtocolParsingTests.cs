using System.Net;
using System.Text;
using Thopter.Discovery.Model;
using Thopter.Discovery.Onvif;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Parser-level tests for the camera protocols, using synthetic responses. These prove the
/// extraction logic deterministically without needing a live camera on the segment.
/// </summary>
public class ProtocolParsingTests
{
    [Fact]
    public void Onvif_probematch_scopes_yield_model_name_and_type()
    {
        const string soap =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"" +
            " xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\">" +
            "<s:Body><d:ProbeMatches><d:ProbeMatch>" +
            "<d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types>" +
            "<d:Scopes>" +
            "onvif://www.onvif.org/type/video_encoder " +
            "onvif://www.onvif.org/name/AXIS%20M3014 " +
            "onvif://www.onvif.org/hardware/M3014 " +
            "onvif://www.onvif.org/location/lab" +
            "</d:Scopes>" +
            "<d:XAddrs>http://10.10.20.50/onvif/device_service</d:XAddrs>" +
            "</d:ProbeMatch></d:ProbeMatches></s:Body></s:Envelope>";

        var finding = new ProtocolFinding
        {
            Address = IPAddress.Parse("10.10.20.50"),
            Source = DiscoverySource.Onvif,
        };

        WsDiscovery.ParseProbeMatches(Encoding.UTF8.GetBytes(soap), finding);

        Assert.Equal("M3014", finding.Model);                          // hardware scope
        Assert.Equal("AXIS M3014", finding.Attributes["onvif.name"]);  // %20 decoded
        Assert.Equal("video_encoder", finding.Attributes["onvif.type"]);
        Assert.True(finding.Attributes.ContainsKey("onvif.present"));
        Assert.Equal("http://10.10.20.50/onvif/device_service", finding.Attributes["onvif.xaddr"]);
        Assert.Contains(finding.Scopes, s => s.Contains("video_encoder"));
    }
}
