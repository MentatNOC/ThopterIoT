using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Thopter.App.Export;
using Thopter.App.ViewModels;

namespace Thopter.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && DeviceList.SelectedItem is DeviceRow row)
            vm.ShowDetail(row);
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
