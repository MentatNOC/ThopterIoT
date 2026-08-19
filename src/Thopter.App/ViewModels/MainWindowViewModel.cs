using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private const string UpgradeUrl = "https://mentatnoc.com/thopteriot";

    private readonly DiscoveryEngine _engine = new();

    // The open-core connector seam. Held only so the shape of a future real connector is
    // visible; this field is never anything but the no-op sink in the free app, and
    // SubmitAsync is intentionally never invoked.
    private readonly IFindingsSink _findingsSink = new NoOpFindingsSink();

    private CancellationTokenSource? _scanCts;

    public ObservableCollection<DeviceRow> Devices { get; } = new();

    public ObservableCollection<NetworkInterfaceInfo> Interfaces { get; } = new();

    [ObservableProperty]
    private NetworkInterfaceInfo? _selectedInterface;

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

    public MainWindowViewModel()
    {
        foreach (var nic in NetInfo.GetActiveIPv4Interfaces())
            Interfaces.Add(nic);

        SelectedInterface = NetInfo.GetPrimaryInterface() ?? Interfaces.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var nic = SelectedInterface;
        if (nic is null)
        {
            StatusText = "No network interface selected.";
            return;
        }

        Devices.Clear();
        IsScanning = true;
        IsProgressIndeterminate = true;
        StatusText = $"Scanning {nic.Name} ({nic.HostAddress}/{nic.PrefixLength})...";

        _scanCts = new CancellationTokenSource();

        var options = new DiscoveryOptions { Interface = nic };

        // DiscoveredDevice is mutated in place by the engine; marshal each report to the UI
        // thread rather than touching the collection from the scan's background thread.
        var progress = new Progress<DiscoveredDevice>(device =>
            Dispatcher.UIThread.Post(() => AddOrUpdateRow(device)));

        try
        {
            var results = await _engine.ScanAsync(options, progress, _scanCts.Token).ConfigureAwait(true);
            StatusText = $"Done. {results.Count} device(s) found.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsProgressIndeterminate = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _scanCts?.Cancel();

    private bool CanCancel() => IsScanning;

    [RelayCommand]
    private void Export()
    {
        // Stub for this scaffold: CSV/JSON export via StorageProvider.SaveFilePickerAsync
        // is a later step.
        StatusText = "Export is not implemented yet.";
    }

    [RelayCommand]
    private void Upgrade()
    {
        // Inert CTA: just opens the marketing page. No connector, no license flow, no
        // cloud call in this project — that logic lives only in the private connector repo.
        Process.Start(new ProcessStartInfo { FileName = UpgradeUrl, UseShellExecute = true });
    }

    private void AddOrUpdateRow(DiscoveredDevice device)
    {
        var existing = Devices.FirstOrDefault(r => r.Key == device.Key);
        if (existing is null)
            Devices.Add(new DeviceRow(device));
        else
            existing.Refresh(device);
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
    }
}
