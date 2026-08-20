using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Thopter.App.Controls;

/// <summary>
/// The scan-in-progress graphic: an 8-bit ornithopter wing set (four blades, no fuselage)
/// that flutters while a scan runs and drifts slowly around whatever area this control is
/// stretched over.
///
/// Flap rate is driven by <see cref="Progress"/> (0..1): quick at launch, easing down to a
/// labored beat as the scan nears the end, then a hard burst over the last stretch before
/// the wings stop with the scan. A ghost of the previous frame is drawn under the current
/// one at high flap rates as cheap motion blur.
///
/// Pure code-behind control: embedded PNG frames + a DispatcherTimer, no reflection, no
/// XAML animations, NativeAOT-safe.
/// </summary>
public sealed class WingFlutter : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WingFlutter, bool>(nameof(IsActive));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WingFlutter, double>(nameof(Progress));

    private const int FrameCount = 8;
    private const double SpriteSize = 256;   // frames are baked 256x256 (2x pixel scale)
    private const int TickMs = 33;

    private static Bitmap[]? _frames;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _phase;              // fractional frame index into the flap cycle
    private double _smoothedProgress;   // low-passed Progress so rate changes glide
    private long _lastTickMs;
    private bool _attached;

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public WingFlutter()
    {
        // Integer-ish nearest-neighbor scaling keeps the chunky pixels chunky.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        IsHitTestVisible = false;
        // The sprite is a fixed 256 DIPs; when the window shrinks the results area below
        // that, clip rather than paint over the status bar and upgrade panel underneath.
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => OnTick();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
            UpdateRunning();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        bool shouldRun = IsActive && _attached;
        if (shouldRun && !_timer.IsEnabled)
        {
            _phase = 0;
            _smoothedProgress = 0;
            _lastTickMs = 0;
            _clock.Restart();
            _timer.Start();
        }
        else if (!shouldRun && _timer.IsEnabled)
        {
            _timer.Stop();
            _clock.Stop();
        }
        InvalidateVisual();
    }

    private void OnTick()
    {
        long nowMs = _clock.ElapsedMilliseconds;
        double dt = Math.Min(0.25, (nowMs - _lastTickMs) / 1000.0);
        _lastTickMs = nowMs;

        double target = Math.Clamp(Progress, 0.0, 1.0);
        _smoothedProgress += (target - _smoothedProgress) * Math.Min(1.0, dt * 4.0);

        _phase = (_phase + FlapRateHz(_smoothedProgress) * FrameCount * dt) % FrameCount;
        InvalidateVisual();
    }

    /// <summary>
    /// Flap rate over scan progress: 7.5 Hz at launch easing down to 2 Hz by 85%, then a
    /// sharp ramp to 12.5 Hz over the final stretch. The scan ending is what stops it.
    /// </summary>
    private static double FlapRateHz(double p)
    {
        const double startHz = 7.5, slowHz = 2.0, finaleHz = 12.5, knee = 0.85;
        if (p <= knee)
        {
            double t = p / knee;
            double eased = t * t * (3 - 2 * t);
            return startHz + (slowHz - startHz) * eased;
        }
        double u = (p - knee) / (1 - knee);
        return slowHz + (finaleHz - slowHz) * Math.Pow(u, 2.2);
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive || !_attached)
            return;

        var frames = _frames ??= LoadFrames();
        int index = (int)_phase % FrameCount;
        var source = new Rect(0, 0, frames[index].PixelSize.Width, frames[index].PixelSize.Height);

        // Slow two-sine wander per axis: covers the whole area over tens of seconds
        // without ever visibly bouncing off an edge.
        double t = _clock.Elapsed.TotalSeconds;
        double availW = Math.Max(0, Bounds.Width - SpriteSize);
        double availH = Math.Max(0, Bounds.Height - SpriteSize);
        double fx = 0.5 + 0.46 * (0.62 * Math.Sin(2 * Math.PI * t / 43.0) + 0.38 * Math.Sin(2 * Math.PI * t / 17.0 + 1.7));
        double fy = 0.5 + 0.46 * (0.58 * Math.Sin(2 * Math.PI * t / 31.0 + 0.9) + 0.42 * Math.Sin(2 * Math.PI * t / 13.0 + 4.2));
        var dest = new Rect(availW * fx, availH * fy, SpriteSize, SpriteSize);

        // Cheap motion blur: previous frame ghosted in, opacity rising with flap rate.
        double hz = FlapRateHz(_smoothedProgress);
        double ghostOpacity = Math.Clamp((hz - 5.0) / 7.5, 0.0, 1.0) * 0.4;
        if (ghostOpacity > 0.02)
        {
            int previous = (index + FrameCount - 1) % FrameCount;
            using (context.PushOpacity(ghostOpacity))
                context.DrawImage(frames[previous], source, dest);
        }

        context.DrawImage(frames[index], source, dest);
    }

    private static Bitmap[] LoadFrames()
    {
        var frames = new Bitmap[FrameCount];
        for (int i = 0; i < FrameCount; i++)
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Thopter.App/Assets/Wings/wings-{i}.png"));
            frames[i] = new Bitmap(stream);
        }
        return frames;
    }
}
