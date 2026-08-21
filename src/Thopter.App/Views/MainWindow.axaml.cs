using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Thopter.App.Export;
using Thopter.App.ViewModels;

namespace Thopter.App.Views;

public partial class MainWindow : Window
{
    // Static resource page; the click sends nothing about the device or the scan.
    private const string CveResourceUrl = "https://mentatnoc.com/resources/thopteriot";

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnCveClick(object? sender, RoutedEventArgs e)
    {
        // AOT-safe launch via the OS shell, same pattern as the upgrade CTA.
        try
        {
            Process.Start(new ProcessStartInfo { FileName = CveResourceUrl, UseShellExecute = true });
        }
        catch (Exception ex) when (DataContext is MainWindowViewModel vm)
        {
            vm.StatusText = $"Could not open browser: {ex.Message}";
        }
        catch
        {
            // no view model to report to - opening a browser is best-effort
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Releases the view model's static NetworkChange subscriptions; runs on the UI
        // thread, which the debounce timer's Stop requires.
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && DeviceList.SelectedItem is DeviceRow row)
            vm.ShowDetail(row);
    }

    // --- Row context menu. The MenuItem's DataContext is the row's DeviceRow (inherited from
    // the Border the ContextMenu is attached to). The clipboard comes from the TopLevel. ---

    private void OnCopyIpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: DeviceRow row })
            CopyToClipboard(row.Ip);
    }

    private void OnCopyMacClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: DeviceRow row } && row.HasMac)
            CopyToClipboard(row.Mac);
    }

    private void OnOpenInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: DeviceRow row }) return;

        string ip = row.Ip;
        // Reject the address-less sentinels: IPAddress.Any ("0.0.0.0") and IPAddress.None
        // ("255.255.255.255", what PrimaryAddress yields for a device with no address).
        if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" || ip == "255.255.255.255") return;

        // AOT-safe launch via the OS shell, same pattern as the upgrade CTA.
        try
        {
            Process.Start(new ProcessStartInfo { FileName = $"http://{ip}", UseShellExecute = true });
        }
        catch (Exception ex) when (DataContext is MainWindowViewModel vm)
        {
            vm.StatusText = $"Could not open browser: {ex.Message}";
        }
        catch
        {
            // no view model to report to - opening a browser is best-effort
        }
    }

    private void CopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        // Fire-and-forget: SetTextAsync completes on the UI thread; a copy failure is not fatal.
        _ = clipboard.SetTextAsync(text);
    }

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app is null)
            return;

        // Flip between the two brand palettes; DynamicResource brushes re-resolve live.
        app.RequestedThemeVariant =
            app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var devices = vm.DevicesForExport;
        if (devices.Count == 0)
            return;

        var jsonType = new FilePickerFileType("JSON")
        {
            Patterns = new[] { "*.json" },
            MimeTypes = new[] { "application/json" },
        };
        var csvType = new FilePickerFileType("CSV")
        {
            Patterns = new[] { "*.csv" },
            MimeTypes = new[] { "text/csv" },
        };

        IStorageFile? file;
        try
        {
            file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export scan results",
                SuggestedFileName = "thopter-scan",
                DefaultExtension = "json",
                FileTypeChoices = new[] { jsonType, csvType },
                ShowOverwritePrompt = true,
            });
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Export failed: {ex.Message}";
            return;
        }

        if (file is null)
            return;

        try
        {
            bool csv = file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            string content = csv ? DeviceExport.ToCsv(devices) : DeviceExport.ToJson(devices);

            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content);

            vm.StatusText = $"Exported {devices.Count} device(s) to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Export failed: {ex.Message}";
        }
    }
}
