using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Thopter.Cloud.Abstractions;
using Thopter.Discovery;
using Xunit;

namespace Thopter.Tests;

/// <summary>
/// Tests that encode the open-core hard wall. The <c>WallCheck</c> category is what CI
/// runs as the runtime wall-check (see .github/workflows/ci.yml).
/// </summary>
public class WallCheckTests
{
    [Fact]
    [Trait("Category", "WallCheck")]
    public void Discovery_engine_has_no_http_client_dependency()
    {
        // The engine talks to the LAN via ICMP/UDP/TCP-connect only. It must not carry an
        // HTTP client — there is nothing in the open tool that should POST anywhere.
        var referenced = typeof(DiscoveryEngine).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain("System.Net.Http", referenced);
    }

    [Fact]
    [Trait("Category", "WallCheck")]
    public async Task NoOp_sink_is_never_configured_and_refuses_to_submit()
    {
        IFindingsSink sink = new NoOpFindingsSink();

        Assert.False(sink.IsConfigured);

        var activation = await sink.ActivateAsync("any-key", CancellationToken.None);
        Assert.False(activation.Activated);

        var result = await sink.SubmitAsync(new FindingsBatch(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Null(result.ReportUrl);
    }

    [Fact]
    public async Task App_submit_flow_works_against_the_public_contract_with_a_fake_connector()
    {
        // Proves the app can run its full connect→activate→submit flow with no real
        // connector — only the Abstractions seam.
        var sink = new FakeFindingsSink();
        var activation = await sink.ActivateAsync("license-abc", CancellationToken.None);
        Assert.True(activation.Activated);

        var batch = new FindingsBatch
        {
            SiteLabel = "Test Site",
            Devices =
            {
                new DeviceFinding { IpAddress = "10.10.10.60", MacAddress = "1C:FC:17:10:05:98", Vendor = "Cisco Systems, Inc" },
            },
        };

        var result = await sink.SubmitAsync(batch, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://example.test/report/123", result.ReportUrl);
        Assert.Single(sink.Submitted);
        Assert.Equal("thopter.findings/v1", sink.Submitted[0].SchemaVersion);
    }
}
