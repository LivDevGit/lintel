using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lintel.Core;
using Lintel.Services;
using Lintel.ViewModels;

namespace Lintel.UI;

/// <summary>
/// <c>Lintel.exe --demo &lt;folder&gt; [--demo-scale 3] [--demo-fps 30]</c> plays scripted scenes on the
/// real island with made-up data (a fictional track, generated cover art, simulated power events)
/// and records each scene as PNG frames with a transparent background. Time is virtual: every
/// animation advances exactly one frame per capture, so the output is smooth at any capture speed.
/// Nothing real is read or saved.
/// </summary>
public sealed class DemoDirector
{
    private readonly Hub _hub;
    private readonly IslandWindow _window;
    private readonly string _output;
    private readonly double _scale;
    private readonly double _fps;

    private readonly DateTimeOffset _epoch = DateTimeOffset.Now;
    private double _virtualSeconds;
    private string? _recordingFolder;
    private int _frameIndex;
    private readonly List<Task> _pending = new();

    /// <param name="scale">Pixels per device-independent pixel for recorded frames.</param>
    /// <param name="fps">Frames per virtual second.</param>
    public DemoDirector(Hub hub, IslandWindow window, string output, double scale = 2, double fps = 30)
    {
        _hub = hub;
        _window = window;
        _output = output;
        _scale = scale;
        _fps = fps;
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_output);
        DemoClock.Now = () => _epoch + TimeSpan.FromSeconds(_virtualSeconds);
        await Wait(0.5);

        var midnight = Track("Midnight Drive", "Neon Harbor", Cover(Color.FromRgb(0xFF, 0x4F, 0x8B), Color.FromRgb(0x3A, 0x1C, 0x71), 3), 42, 218);
        var coast = Track("Glass Coast", "Polar Youth", Cover(Color.FromRgb(0x2E, 0xC4, 0xB6), Color.FromRgb(0x0B, 0x3D, 0x5C), 11), 5, 196);
        _hub.Calendar.Inject(SampleAgenda());
        _hub.Battery.Inject(new BatterySnapshot(true, 64, false, false, false, null, 245));
        await Wait(0.5);

        // 1. Power: plug in, then unplug.
        await Record("power", async () =>
        {
            await Wait(1.2);
            _hub.Battery.Inject(new BatterySnapshot(true, 64, true, true, false, 72, null));
            await Wait(3.4);
            _hub.Battery.Inject(new BatterySnapshot(true, 64, false, false, false, null, 245));
            await Wait(3.4);
        });

        // 2. Music: a song starts, the pill comes alive, open it, pause/play, skip.
        await Record("music", async () =>
        {
            await Wait(0.8);
            _hub.Media.Inject(midnight with { PositionStamp = DemoClock.Now() });
            await Wait(4.6);
            _window.Expand(IslandTab.Home, byPointer: false);
            await Wait(3.2);
            var m = _hub.Media.Current;
            _hub.Media.Inject(m with { IsPlaying = false, Position = m.LivePosition, PositionStamp = DemoClock.Now() });
            await Wait(1.3);
            _hub.Media.Inject(_hub.Media.Current with { IsPlaying = true, PositionStamp = DemoClock.Now() });
            await Wait(1.5);
            _hub.Media.Inject(coast with { PositionStamp = DemoClock.Now() });
            await Wait(2.6);
            _window.Collapse();
            await Wait(1.5);
        });

        // 3. Volume: a few key presses' worth of changes.
        await Record("volume", async () =>
        {
            await Wait(0.7);
            foreach (var level in new[] { 0.30f, 0.36f, 0.42f, 0.48f, 0.54f, 0.60f })
            {
                _hub.SimulateVolume(level);
                await Wait(0.14);
            }
            await Wait(2.4);
        });

        // 4. Shelf: parked files.
        foreach (var file in SampleFiles()) _hub.Shelf.AddPaths(new[] { file });
        await Record("shelf", async () =>
        {
            await Wait(0.6);
            _window.Expand(IslandTab.Shelf, byPointer: false);
            await Wait(3.0);
            _window.Collapse();
            await Wait(1.2);
        });

        // 5. A still of the open island for the banner.
        _hub.Media.Inject(midnight with { PositionStamp = DemoClock.Now() });
        _window.Expand(IslandTab.Home, byPointer: false);
        await Wait(1.5);
        await SaveFrame(Path.Combine(_output, "hero.png"), scale: 3);
        _window.Collapse();
        await Wait(0.6);
        await Task.WhenAll(_pending);
    }

    // ------------------------------------------------------------------ virtual time & recording

    /// <summary>Advances virtual time frame by frame, capturing each frame while a scene records.</summary>
    private async Task Wait(double seconds)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds * _fps));
        var dt = 1 / _fps;
        for (var i = 0; i < frames; i++)
        {
            _virtualSeconds += dt;
            _hub.Spectrum.SimulateStep(dt);
            _hub.DemoTick();
            _window.AdvanceDemoFrame(dt);
            // Let bindings, item generation and layout settle before the frame is captured.
            await Dispatcher.Yield(DispatcherPriority.Background);
            _window.UpdateLayout();
            if (_recordingFolder != null)
                _pending.Add(SaveFrame(Path.Combine(_recordingFolder, $"f{_frameIndex++:D4}.png"), _scale));
        }
    }

    private async Task Record(string scene, Func<Task> script)
    {
        _recordingFolder = Path.Combine(_output, scene);
        Directory.CreateDirectory(_recordingFolder);
        _frameIndex = 0;
        await script();
        var frames = _frameIndex;
        _recordingFolder = null;
        await Task.WhenAll(_pending);
        _pending.Clear();

        // Uniform timing, in the same format the media tools read.
        var times = new List<string>();
        for (var i = 0; i <= frames; i++) times.Add(((long)Math.Round(i * 1000 / _fps)).ToString());
        await File.WriteAllLinesAsync(Path.Combine(_output, scene, "times.txt"), times);
        Log.Write($"[demo] {scene}: {frames} frames ({frames / _fps:N1} s at {_fps} fps)");
    }

    /// <summary>Renders the window's content on the UI thread; encodes the PNG on a worker.</summary>
    private Task SaveFrame(string path, double scale)
    {
        var surface = _window.Surface;
        var w = (int)Math.Ceiling(surface.ActualWidth * scale);
        var h = (int)Math.Ceiling(surface.ActualHeight * scale);
        if (w <= 0 || h <= 0) return Task.CompletedTask;

        var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        bitmap.Freeze();
        return Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        });
    }

    // ------------------------------------------------------------------ made-up content

    private static MediaSnapshot Track(string title, string artist, BitmapSource art, int positionSeconds, int durationSeconds) => new()
    {
        HasSession = true,
        Title = title,
        Artist = artist,
        Album = "",
        SourceApp = "Spotify",
        Art = art,
        ArtColor = ArtPalette.Vibrant(art),
        IsPlaying = true,
        CanSeek = true,
        Position = TimeSpan.FromSeconds(positionSeconds),
        Duration = TimeSpan.FromSeconds(durationSeconds),
        PositionStamp = DemoClock.Now(),
    };

    /// <summary>Abstract generated cover art: a gradient with soft orbs and a ring.</summary>
    private static BitmapSource Cover(Color a, Color b, int seed)
    {
        const int size = 320;
        var random = new Random(seed);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, size, size));
            for (var i = 0; i < 5; i++)
            {
                var center = new Point(random.Next(size), random.Next(size));
                var radius = 40 + random.Next(110);
                var glow = new RadialGradientBrush(Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                dc.DrawEllipse(glow, null, center, radius, radius);
            }
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 6), new Point(size * 0.62, size * 0.42), 70, 70);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Point(size * 0.3, size * 0.78), 46, 46);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static IReadOnlyList<CalendarEvent> SampleAgenda()
    {
        var today = DateTime.Today;
        var now = DateTime.Now;
        DateTime At(double hoursFromNow) => now.AddHours(hoursFromNow);
        return new List<CalendarEvent>
        {
            new("Design review", At(-0.2), At(0.5), false, "Room 4", 0),
            new("Lunch with Sam", At(1.5), At(2.5), false, null, 1),
            new("Gym", At(4), At(5), false, null, 2),
        }.FindAll(e => e.Start.Date == today || e.End.Date == today);
    }

    /// <summary>Placeholder files in a temp folder, so the shelf shows real shell icons.</summary>
    private static IEnumerable<string> SampleFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LintelDemo");
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        foreach (var name in new[] { "Trip photos.zip", "Quarterly report.pdf", "Notes.txt", "Invoice.docx" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) File.WriteAllText(path, "");
            files.Add(path);
        }

        var image = Path.Combine(folder, "Sunset.png");
        if (!File.Exists(image))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Cover(Color.FromRgb(0xFF, 0x9F, 0x43), Color.FromRgb(0x8E, 0x2D, 0xE2), 21)));
            using var stream = File.Create(image);
            encoder.Save(stream);
        }
        files.Insert(1, image);
        return files;
    }
}
