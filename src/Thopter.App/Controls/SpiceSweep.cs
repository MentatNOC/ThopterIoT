using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Thopter.App.Controls;

/// <summary>
/// One-shot 8-bit "spice on the wind" effect for a device row: a gust of chunky rust and
/// gold sand grains blows left to right across the row when the device is identified as a
/// camera or recorder. Matches the WingFlutter pixel language: hard 2px squares on a 2px
/// grid, small fixed palette, no alpha fades.
///
/// Trigger is the row's <c>SpiceSweepPending</c> one-shot; this control consumes it when
/// the sweep starts, so a recycled list container can't replay it. A row identified while
/// scrolled out of view plays when it first materializes during the scan; the view model
/// clears un-played cues at scan end so later scrolling stays quiet. Pure code-behind:
/// procedural rectangles + a DispatcherTimer, no assets, NativeAOT-safe.
/// </summary>
public sealed class SpiceSweep : Control
{
    public static readonly StyledProperty<bool> IsPendingProperty =
        AvaloniaProperty.Register<SpiceSweep, bool>(nameof(IsPending));

    private const int TickMs = 33;
    private const int GrainCount = 46;
    private const double MaxDelaySeconds = 0.35;

    // Spice palette, dark to glint; weighted toward the mid rusts. The umber reads on the
    // light theme, the gold glints read on the dark theme.
    private static readonly ImmutableSolidColorBrush[] Palette =
    {
        new(Color.FromRgb(0x7A, 0x3B, 0x10)),
        new(Color.FromRgb(0xB2, 0x4A, 0x1C)),
        new(Color.FromRgb(0xB2, 0x4A, 0x1C)),
        new(Color.FromRgb(0xD6, 0x74, 0x2C)),
        new(Color.FromRgb(0xD6, 0x74, 0x2C)),
        new(Color.FromRgb(0xE8, 0xA8, 0x54)),
        new(Color.FromRgb(0xE8, 0xA8, 0x54)),
        new(Color.FromRgb(0xFF, 0xD6, 0x78)),
    };

    private readonly struct Grain
    {
        public required double Delay { get; init; }
        public required double CrossSeconds { get; init; }
        public required double BaseY { get; init; }
        public required double Wobble { get; init; }
        public required double WobbleHz { get; init; }
        public required double WobblePhase { get; init; }
        public required int Size { get; init; }
        public required ImmutableSolidColorBrush Brush { get; init; }
    }

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private Grain[]? _grains;   // non-null while a sweep is playing
    private bool _attached;

    public bool IsPending
    {
        get => GetValue(IsPendingProperty);
        set => SetValue(IsPendingProperty, value);
    }

    public SpiceSweep()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => OnTick();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPendingProperty && change.GetNewValue<bool>())
            TryPlay();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (IsPending)
            TryPlay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        StopSweep();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        // A virtualized container being recycled mid-gust must not carry the old row's
        // sand onto the new row; the new row's own pending flag re-triggers if needed.
        StopSweep();
        base.OnDataContextChanged(e);
    }

    private void TryPlay()
    {
        if (!_attached || _grains is not null)
            return;

        // Consume the one-shot up front: once the gust starts it never replays, not even
        // if this container is recycled and rebound while the grains are mid-flight.
        (DataContext as ViewModels.DeviceRow)?.ConsumeSpiceSweep();

        _grains = BuildGust();
        _clock.Restart();
        _timer.Start();
    }

    private void StopSweep()
    {
        _timer.Stop();
        _clock.Stop();
        _grains = null;
        InvalidateVisual();
    }

    private static Grain[] BuildGust()
    {
        var rng = Random.Shared;
        var grains = new Grain[GrainCount];
        for (int i = 0; i < GrainCount; i++)
        {
            grains[i] = new Grain
            {
                Delay = rng.NextDouble() * MaxDelaySeconds,
                CrossSeconds = 0.45 + rng.NextDouble() * 0.40,
                BaseY = rng.NextDouble(),
                Wobble = 1.0 + rng.NextDouble() * 2.0,
                WobbleHz = 3.0 + rng.NextDouble() * 4.0,
                WobblePhase = rng.NextDouble() * Math.PI * 2,
                Size = rng.NextDouble() < 0.7 ? 2 : 3,
                Brush = Palette[rng.Next(Palette.Length)],
            };
        }
        return grains;
    }

    private void OnTick()
    {
        if (_grains is null)
        {
            StopSweep();
            return;
        }

        // Every grain has exited once the slowest possible grain is done.
        if (_clock.Elapsed.TotalSeconds > MaxDelaySeconds + 0.90)
        {
            StopSweep();
            return;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var grains = _grains;
        if (grains is null)
            return;

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width < 8 || height < 4)
            return;

        double t = _clock.Elapsed.TotalSeconds;
        foreach (var grain in grains)
        {
            double p = (t - grain.Delay) / grain.CrossSeconds;
            if (p is < 0 or > 1)
                continue;

            // 2px grid quantization keeps the motion stepped, like sprite-sheet sand.
            double x = p * (width + 6) - 3;
            double y = grain.BaseY * (height - grain.Size)
                       + grain.Wobble * Math.Sin(2 * Math.PI * grain.WobbleHz * t + grain.WobblePhase);
            double qx = Math.Floor(x / 2) * 2;
            double qy = Math.Clamp(Math.Floor(y / 2) * 2, 0, Math.Max(0, height - grain.Size));

            context.FillRectangle(grain.Brush, new Rect(qx, qy, grain.Size, grain.Size));
        }
    }
}
