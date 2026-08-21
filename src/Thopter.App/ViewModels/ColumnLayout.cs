using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Thopter.App.ViewModels;

/// <summary>
/// Shared, user-adjustable widths for the device grid. One instance is held by the main
/// view model and referenced by every <see cref="DeviceRow"/>, so the header's resize
/// handles and all rows bind to the same widths and stay aligned as columns are dragged.
/// Widths are <see cref="GridLength"/> pixels; the header binds them two-way (the splitter
/// writes back), the rows bind one-way.
/// </summary>
public sealed partial class ColumnLayout : ObservableObject
{
    [ObservableProperty] private GridLength _ip = new(130);
    [ObservableProperty] private GridLength _mac = new(150);
    [ObservableProperty] private GridLength _vendor = new(170);
    [ObservableProperty] private GridLength _type = new(120);
    [ObservableProperty] private GridLength _via = new(150);
    [ObservableProperty] private GridLength _hostname = new(150);

    // Ports is the widest, most variable column and sits last before the fixed CVEs cell, so
    // it takes the star: it soaks up slack when the window is wide and gives it back as the
    // window narrows, keeping the CVEs link on screen without a dead filler column.
    [ObservableProperty] private GridLength _ports = new(1, GridUnitType.Star);
}
