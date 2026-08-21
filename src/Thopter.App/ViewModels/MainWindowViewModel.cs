using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Thopter.Cloud.Abstractions;
using Thopter.Discovery;
using Thopter.Discovery.Model;
using Thopter.Discovery.Net;

namespace Thopter.App.ViewModels;

/// <summary>
/// Scan orchestration for the main window. Owns the one <see cref="DiscoveryEngine"/> for
/// the app's lifetime and streams results into <see cref="Devices"/> as they are found.
///
/// This app never contains cloud logic: <see cref="_findingsSink"/> is always the inert
/// <see cref="NoOpFindingsSink"/> and <c>SubmitAsync</c> is never called anywhere in this
/// project. The only outbound network action this view model ever takes is opening the
/// upgrade URL in the user's browser on an explicit click (<see cref="Upgrade"/>).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string UpgradeUrl = ResourceUrls.ThopterResources;

    private readonly DiscoveryEngine _engine = new();

    // The open-core connector seam. Held only so the shape of a future real connector is
    // visible; this field is never anything but the no-op sink in the free app, and
    // SubmitAsync is intentionally never invoked.
    private readonly IFindingsSink _findingsSink = new NoOpFindingsSink();

    private CancellationTokenSource? _scanCts;

    // Live NIC repopulation: OS network-change events arrive in bursts on pool threads
    // (link up, then DHCP, then registration), so they restart this UI-thread debounce
    // timer and the interface list settles once. Refreshes that land mid-scan are held
    // until the scan finishes.
    private DispatcherTimer? _nicDebounce;
    private bool _nicRefreshPending;

    public ObservableCollection<DeviceRow> Devices { get; } = new();

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();

    /// <summary>Shared, resizable column widths for the device grid header and every row.</summary>
    public ColumnLayout Columns { get; } = new();

    [ObservableProperty]
    private NetworkInterfaceInfo? _selectedInterface;

    /// <summary>Optional manual CIDR override (e.g. "10.10.10.0/24"). When set, overrides the interface pick.</summary>
    [ObservableProperty]
    private string _manualCidr = "";

    /// <summary>
    /// When true, sweep every active interface at once instead of only the selected
    /// subnet. Off by default so a scan stays on the network the user picked.
    /// </summary>
    [ObservableProperty]
    private bool _scanAllNetworks;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "Ready.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>Overall scan completion 0..1; paces the wing-flutter graphic and the bar.</summary>
    [ObservableProperty]
    private double _scanProgress;

    /// <summary>The device whose detail flyout is open, or null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private DeviceDetail? _selectedDetail;

    public bool IsDetailOpen => SelectedDetail is not null;

    /// <summary>True once at least one device has been found - gates the Export button.</summary>
    public bool HasResults => Devices.Count > 0;

    public IReadOnlyList<DiscoveredDevice> DevicesForExport => Devices.Select(r => r.Device).ToList();

    public MainWindowViewModel()
    {
        foreach (var nic in NetInfo.GetActiveIPv4Interfaces())
            Interfaces.Add(nic);

        SelectedInterface = NetInfo.GetPrimaryInterface() ?? Interfaces.FirstOrDefault();

        Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));

        // A NIC plugged in (or unplugged) after launch must show up on its own - walking
        // up and jacking into the camera VLAN is the tool's core workflow. These are
        // static events, so every subscriber is rooted until it unsubscribes: the window's
        // Closed hook calls Dispose for the app, and design mode never subscribes at all
        // (the previewer constructs a fresh VM per reload and never closes anything).
        if (!Design.IsDesignMode)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleInterfaceRefresh();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => ScheduleInterfaceRefresh();

    private void ScheduleInterfaceRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_nicDebounce is null)
            {
                _nicDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _nicDebounce.Tick += (_, _) =>
                {
                    _nicDebounce.Stop();
                    if (IsScanning)
                        _nicRefreshPending = true;
                    else
                        RefreshInterfaces();
                };
            }

            // Restart on every event so one refresh runs after the burst goes quiet.
            _nicDebounce.Stop();
            _nicDebounce.Start();
        });
    }

    /// <summary>
    /// Re-enumerate active interfaces and reconcile the dropdown, keeping the user's
    /// selection when that interface still exists (even if DHCP moved its address).
    /// </summary>
    private void RefreshInterfaces()
    {
        var previous = SelectedInterface;

        Interfaces.Clear();
        foreach (var nic in NetInfo.GetActiveIPv4Interfaces())
            Interfaces.Add(nic);

        SelectedInterface = PickInterfaceSelection(previous, NetInfo.GetPrimaryInterface(), Interfaces);
    }

    /// <summary>
    /// Selection policy for a refreshed interface list: keep the current pick exactly,
    /// else the same adapter on the same subnet (DHCP renewed the address), else the same
    /// adapter at all, else the OS-primary interface, else the first entry. The same-subnet
    /// tier matters because a multi-homed adapter yields one candidate per address; without
    /// it a renewal could silently retarget the next scan at the adapter's other subnet.
    /// Pure so it is unit-testable.
    /// </summary>
    public static NetworkInterfaceInfo? PickInterfaceSelection(
        NetworkInterfaceInfo? current,
        NetworkInterfaceInfo? primary,
        IReadOnlyList<NetworkInterfaceInfo> candidates)
    {
        return Exact(current) ?? SameSubnet(current) ?? SameAdapter(current)
               ?? Exact(primary) ?? candidates.FirstOrDefault();

        NetworkInterfaceInfo? Exact(NetworkInterfaceInfo? nic) => nic is null
            ? null
            : candidates.FirstOrDefault(i => i.Id == nic.Id && i.HostAddress.Equals(nic.HostAddress));

        NetworkInterfaceInfo? SameSubnet(NetworkInterfaceInfo? nic) => nic is null
            ? null
            : candidates.FirstOrDefault(i => i.Id == nic.Id
                                             && i.PrefixLength == nic.PrefixLength
                                             && i.NetworkAddress.Equals(nic.NetworkAddress));

        NetworkInterfaceInfo? SameAdapter(NetworkInterfaceInfo? nic) => nic is null
            ? null
            : candidates.FirstOrDefault(i => i.Id == nic.Id);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (!TryBuildOptions(out var options, out var scanLabel, out var error))
        {
            StatusText = error!;
            return;
        }

        SelectedDetail = null;
        Devices.Clear();
        IsScanning = true;
        IsProgressIndeterminate = true;
        ScanProgress = 0;
        ProgressValue = 0;
        StatusText = $"Scanning {scanLabel}...";

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        // DiscoveredDevice is mutated in place by the engine; marshal each report to the UI
        // thread rather than touching the collection from the scan's background thread.
        var progress = new Progress<DiscoveredDevice>(device =>
            Dispatcher.UIThread.Post(() =>
            {
                AddOrUpdateRow(device);
                StatusText = $"Scanning {scanLabel}... {Devices.Count} device(s) found";
            }));

        // Reports can race in from worker threads slightly out of order; only ever move
        // forward. The first report flips the bar from indeterminate to real progress.
        // A cancelled scan's abandoned probes keep reporting for up to their timeout, so
        // the guard is keyed to THIS scan's CTS, not just IsScanning; otherwise a quick
        // cancel-and-rescan lets stale fractions seed (and then pin) the new scan's bar.
        // Safe without locks: the callback and the _scanCts writes are both UI-thread.
        var overall = new Progress<double>(fraction =>
        {
            if (!IsScanning || !ReferenceEquals(_scanCts, cts)) return;
            IsProgressIndeterminate = false;
            if (fraction > ScanProgress)
            {
                ScanProgress = fraction;
                ProgressValue = fraction * 100;
            }
        });

        try
        {
            var results = await _engine.ScanAsync(options, progress, overall, _scanCts.Token).ConfigureAwait(true);
            StatusText = $"Done. {results.Count} device(s) found on {scanLabel}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Scan cancelled. {Devices.Count} device(s) found before stopping.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
            ScanProgress = 0;
            ProgressValue = 0;
            _scanCts?.Dispose();
            _scanCts = null;

            // The spice sweep belongs to the scan: rows identified while scrolled out of
            // view keep their cue only until the scan ends, so scrolling the results later
            // doesn't set off gusts minutes after the fact. Posted at Background priority
            // so it runs after the fusion-stage device reports still queued on the
            // dispatcher; clearing inline here could lose the race and let those re-arm.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var row in Devices)
                    row.ConsumeSpiceSweep();
            }, DispatcherPriority.Background);

            // NIC changes that arrived mid-scan were held; reconcile the dropdown now.
            if (_nicRefreshPending)
            {
                _nicRefreshPending = false;
                RefreshInterfaces();
            }
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _scanCts?.Cancel();

    private bool CanCancel() => IsScanning;

    [RelayCommand]
    private void CloseDetail() => SelectedDetail = null;

    /// <summary>Open the detail flyout for a row (called from the view on double-click).</summary>
    public void ShowDetail(DeviceRow row) => SelectedDetail = new DeviceDetail(row.Device);

    [RelayCommand]
    private void Upgrade()
    {
        // Inert CTA: just opens the marketing page. No connector, no license flow, no
        // cloud call in this project - that logic lives only in the private connector repo.
        Process.Start(new ProcessStartInfo { FileName = UpgradeUrl, UseShellExecute = true });
    }

    /// <summary>
    /// Build scan options from the UI: a validated manual CIDR takes precedence, else the
    /// selected interface. A manual CIDR must resolve to a private/link-local range - the
    /// open tool never scans public address space.
    /// </summary>
    private bool TryBuildOptions(out DiscoveryOptions options, out string label, out string? error)
    {
        options = new DiscoveryOptions();
        label = "";
        error = null;

        // "Scan all networks" sweeps every active interface's subnet in one run and turns
        // off the in-scope filter, so the results deliberately span every segment. A manual
        // CIDR still wins even in this mode - an explicit range is an explicit intent.
        if (ScanAllNetworks && string.IsNullOrWhiteSpace(ManualCidr))
        {
            var subnets = NetInfo.GetActiveIPv4Interfaces()
                .Select(i => $"{NetInfo.NetworkAddressOf(i.HostAddress, i.PrefixLength)}/{i.PrefixLength}")
                .Distinct()
                .ToArray();

            if (subnets.Length == 0)
            {
                error = "No active network interfaces to scan.";
                return false;
            }

            options = new DiscoveryOptions { Cidrs = subnets, RestrictToTargetSubnets = false };
            label = subnets.Length == 1 ? subnets[0] : $"all networks ({subnets.Length} subnets)";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ManualCidr))
        {
            if (!TryValidateCidr(ManualCidr, out var cidr, out error))
                return false;

            options = new DiscoveryOptions { Cidrs = new[] { cidr } };
            label = cidr;
            return true;
        }

        var nic = SelectedInterface;
        if (nic is null)
        {
            error = "Select a network interface, or enter a CIDR to scan.";
            return false;
        }

        options = new DiscoveryOptions { Interface = nic };
        label = $"{nic.Name} ({nic.HostAddress}/{nic.PrefixLength})";
        return true;
    }

    private static bool TryValidateCidr(string input, out string canonical, out string? error)
    {
        canonical = "";
        error = null;

        input = input.Trim();
        int slash = input.IndexOf('/');
        string ipPart = slash < 0 ? input : input[..slash];

        if (!IPAddress.TryParse(ipPart.Trim(), out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Enter a valid IPv4 CIDR, e.g. 10.10.10.0/24.";
            return false;
        }

        int prefix = 24;
        if (slash >= 0 && (!int.TryParse(input.AsSpan(slash + 1), out prefix) || prefix is < 0 or > 32))
        {
            error = "Invalid prefix length - use 0 to 32.";
            return false;
        }

        var network = NetInfo.NetworkAddressOf(addr, prefix);
        if (!NetInfo.IsLanScannable(network))
        {
            error = "Only private / link-local ranges can be scanned (RFC1918, 169.254/16).";
            return false;
        }

        canonical = $"{network}/{prefix}";
        return true;
    }

    private void AddOrUpdateRow(DiscoveredDevice device)
    {
        var existing = Devices.FirstOrDefault(r => r.Key == device.Key);
        if (existing is null)
            Devices.Add(new DeviceRow(device, Columns));
        else
            existing.Refresh(device);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _nicDebounce?.Stop();
        _scanCts?.Cancel();
        _scanCts?.Dispose();
    }
}
