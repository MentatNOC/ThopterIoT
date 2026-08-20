using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Thopter.App.Json;
using Thopter.Discovery;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.App;

/// <summary>
/// Entry point. Dual mode:
///  * <c>Thopter.App.exe scan [--json]</c> - headless console scan, no Avalonia loaded.
///  * anything else - the normal Avalonia desktop GUI.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "scan", StringComparison.OrdinalIgnoreCase)))
        {
            return RunHeadlessScanAsync(args).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, also used by the design-time previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunHeadlessScanAsync(string[] args)
    {
        bool json = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        var nic = NetInfo.GetPrimaryInterface();
        if (nic is null)
        {
            Console.Error.WriteLine("No active network interface found.");
            return 1;
        }

        if (!json)
        {
            Console.WriteLine($"Scanning {nic.Name} ({nic.HostAddress}/{nic.PrefixLength}) via {nic.Description} ...");
            Console.WriteLine();
        }

        var engine = new DiscoveryEngine();
        var options = new DiscoveryOptions { Interface = nic };
        using var cts = new CancellationTokenSource();

        IReadOnlyList<DiscoveredDevice> devices =
            await engine.ScanAsync(options, progress: null, cts.Token).ConfigureAwait(false);

        if (json)
        {
            var dto = devices.Select(ScanResultDevice.FromDevice).ToList();
            Console.WriteLine(JsonSerializer.Serialize(dto, ScanJsonContext.Default.ListScanResultDevice));
        }
        else
        {
            PrintTable(devices);
        }

        return 0;
    }

    private static void PrintTable(IReadOnlyList<DiscoveredDevice> devices)
    {
        Console.WriteLine($"{"IP",-16}{"MAC",-19}{"Vendor",-30}");
        Console.WriteLine(new string('-', 65));

        foreach (var d in devices)
        {
            Console.WriteLine($"{d.PrimaryAddress.ToString(),-16}{d.MacString ?? "-",-19}{d.Vendor ?? "-",-30}");
        }

        Console.WriteLine();
        Console.WriteLine($"{devices.Count} device(s) found.");
    }
}
