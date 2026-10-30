using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lintel.Services;
using Lintel.ViewModels;

namespace Lintel.UI;

public sealed class InverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Live spectrum bars, drawn straight from <see cref="SpectrumService.Levels"/>.</summary>
public sealed class SpectrumBars : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SpectrumBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public SpectrumBars()
    {
        Loaded += (_, _) => { if (Hub.Instance != null) Hub.Instance.Spectrum.Updated += InvalidateVisual; };
        Unloaded += (_, _) => { if (Hub.Instance != null) Hub.Instance.Spectrum.Updated -= InvalidateVisual; };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var levels = Hub.Instance?.Spectrum.Levels;
        var n = SpectrumService.Bands;
        const double gap = 2;
        var barW = Math.Max(1.5, (ActualWidth - gap * (n - 1)) / n);
        for (var i = 0; i < n; i++)
        {
            var level = levels == null ? 0 : levels[i];
            var h = Math.Max(barW, level * ActualHeight);
            var x = i * (barW + gap);
            var y = (ActualHeight - h) / 2;
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, y, barW, h), barW / 2, barW / 2);
        }
    }
}

/// <summary>Single-line text that scrolls when it doesn't fit, pausing at the start of each pass.</summary>
public sealed class MarqueeText : UserControl
{
    private const double Gap = 36;
    private const double PixelsPerSecond = 28;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText), new PropertyMetadata("", (d, _) => ((MarqueeText)d).Refresh()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private readonly TextBlock _first = new();
    private readonly TextBlock _second = new();
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _shift = new();
    private double _lastWidth = -1;
    private string _lastText = "";

    public MarqueeText()
    {
        _second.Margin = new Thickness(Gap, 0, 0, 0);
        _strip.Children.Add(_first);
        _strip.Children.Add(_second);
        _strip.RenderTransform = _shift;
        // Let the strip move in sub-pixel steps and keep glyph shapes stable while it scrolls.
        _strip.SnapsToDevicePixels = false;
        _strip.UseLayoutRounding = false;
        TextOptions.SetTextHintingMode(_strip, TextHintingMode.Animated);
        Content = new Grid { ClipToBounds = true, Children = { _strip } };
        SizeChanged += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var text = Text ?? "";
        var width = ActualWidth;
        if (text == _lastText && Math.Abs(width - _lastWidth) < 0.5 && _strip.Children.Count > 0 && _lastWidth >= 0) return;
        _lastText = text;
        _lastWidth = width;

        _first.Text = text;
        _second.Text = text;
        _first.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _first.DesiredSize.Width;

        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;

        var overflow = IsVisible && width > 0 && textWidth > width;
        _second.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _strip.HorizontalAlignment = overflow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        if (!overflow) return;

        var distance = textWidth + Gap;
        var travel = TimeSpan.FromSeconds(distance / PixelsPerSecond);
        var hold = TimeSpan.FromSeconds(2.2);
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        // 30 fps is smooth for slow scrolling and halves the redraw cost of the transparent window.
        Timeline.SetDesiredFrameRate(anim, 30);
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(hold + travel)));
        _shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}

/// <summary>Damped spring used for every island transition.</summary>
internal sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness;
    public double Damping;

    public Spring(double value, double stiffness, double damping)
    {
        Value = Target = value;
        Stiffness = stiffness;
        Damping = damping;
    }

    public bool Settled => Math.Abs(Value - Target) < 0.25 && Math.Abs(Velocity) < 0.25;

    public void Step(double dt)
    {
        // Semi-implicit Euler in small sub-steps keeps stiff springs stable at low frame rates.
        const double maxStep = 1 / 240.0;
        while (dt > 0)
        {
            var h = Math.Min(dt, maxStep);
            var force = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += force * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Settled)
        {
            Value = Target;
            Velocity = 0;
        }
    }
}

internal static class IslandShape
{
    /// <summary>Rounded rectangle with separate top and bottom corner radii.</summary>
    public static Geometry Build(double w, double h, double top, double bottom)
    {
        top = Math.Max(0, Math.Min(top, Math.Min(w / 2, h / 2)));
        bottom = Math.Max(0, Math.Min(bottom, Math.Min(w / 2, h / 2)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(top, 0), true, true);
            c.LineTo(new Point(w - top, 0), false, false);
            if (top > 0) c.ArcTo(new Point(w, top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(w, h - bottom), false, false);
            if (bottom > 0) c.ArcTo(new Point(w - bottom, h), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(bottom, h), false, false);
            if (bottom > 0) c.ArcTo(new Point(0, h - bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(0, top), false, false);
            if (top > 0) c.ArcTo(new Point(top, 0), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }
}
