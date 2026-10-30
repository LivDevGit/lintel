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
/// <c>Lintel.exe --demo &lt;folder&gt;</c> plays scripted scenes on the real island with made-up
/// data (a fictional track, generated cover art, simulated power events) and records each scene
/// as PNG frames with a transparent background. Nothing real is read or saved.
/// </summary>
public sealed class DemoDirector
{
    private const double Fps = 20;
    private readonly Hub _hub;
    private readonly IslandWindow _window;
    private readonly string _output;

    public DemoDirector(Hub hub, IslandWindow window, string output)
    {
        _hub = hub;
        _window = window;
        _output = output;
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_output);
        await Task.Delay(800);

        var midnight = Track("Midnight Drive", "Neon Harbor", Cover(Color.FromRgb(0xFF, 0x4F, 0x8B), Color.FromRgb(0x3A, 0x1C, 0x71), 3), 42, 218);
        var coast = Track("Glass Coast", "Polar Youth", Cover(Color.FromRgb(0x2E, 0xC4, 0xB6), Color.FromRgb(0x0B, 0x3D, 0x5C), 11), 5, 196);
        _hub.Calendar.Inject(SampleAgenda());
        _hub.Battery.Inject(new BatterySnapshot(true, 64, false, false, false, null, 245));

        // 1. Power: plug in, then unplug.
        await Record("power", async () =>
        {
            await Task.Delay(1200);
            _hub.Battery.Inject(new BatterySnapshot(true, 64, true, true, false, 72, null));
            await Task.Delay(3400);
            _hub.Battery.Inject(new BatterySnapshot(true, 64, false, false, false, null, 245));
            await Task.Delay(3400);
        });

        // 2. Music: a song starts, the pill comes alive, open it, pause/play, skip.
        await Record("music", async () =>
        {
            await Task.Delay(800);
            _hub.Media.Inject(midnight);
            await Task.Delay(4600);
            _window.Expand(IslandTab.Home, byPointer: false);
            await Task.Delay(3200);
            _hub.Media.Inject(_hub.Media.Current with { IsPlaying = false, Position = _hub.Media.Current.LivePosition, PositionStamp = DateTimeOffset.Now });
            await Task.Delay(1300);
            _hub.Media.Inject(_hub.Media.Current with { IsPlaying = true, PositionStamp = DateTimeOffset.Now });
            await Task.Delay(1500);
            _hub.Media.Inject(coast);
            await Task.Delay(2600);
            _window.Collapse();
            await Task.Delay(1500);
        });

        // 3. Volume: a few key presses' worth of changes.
        await Record("volume", async () =>
        {
            await Task.Delay(700);
            foreach (var level in new[] { 0.30f, 0.36f, 0.42f, 0.48f, 0.54f, 0.60f })
            {
                _hub.SimulateVolume(level);
                await Task.Delay(140);
            }
            await Task.Delay(2400);
        });

        // 4. Shelf: parked files.
        foreach (var file in SampleFiles()) _hub.Shelf.AddPaths(new[] { file });
        await Record("shelf", async () =>
        {
            await Task.Delay(600);
            _window.Expand(IslandTab.Shelf, byPointer: false);
            await Task.Delay(3000);
            _window.Collapse();
            await Task.Delay(1200);
        });

        // 5. A still of the open island for the banner.
        _hub.Media.Inject(midnight with { PositionStamp = DateTimeOffset.Now });
        _window.Expand(IslandTab.Home, byPointer: false);
        await Task.Delay(1500);
        await SaveFrame(Path.Combine(_output, "hero.png"), scale: 3);
        _window.Collapse();
        await Task.Delay(600);
    }

    // ------------------------------------------------------------------ recording

    private async Task Record(string scene, Func<Task> script)
    {
        var folder = Path.Combine(_output, scene);
        Directory.CreateDirectory(folder);
        var index = 0;
        var pending = new List<Task>();
        // Frames can't always be captured exactly on schedule, so keep each one's real time;
        // the animation is then assembled with true durations and plays at real speed.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<long>();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000 / Fps) };
        timer.Tick += (_, _) =>
        {
            times.Add(clock.ElapsedMilliseconds);
            pending.Add(SaveFrame(Path.Combine(folder, $"f{index++:D4}.png"), scale: 2));
        };
        timer.Start();
        await script();
        timer.Stop();
        times.Add(clock.ElapsedMilliseconds);
        await Task.WhenAll(pending);
        await File.WriteAllLinesAsync(Path.Combine(folder, "times.txt"), times.ConvertAll(t => t.ToString()));
        Log.Write($"[demo] {scene}: {index} frames in {clock.ElapsedMilliseconds} ms");
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
        PositionStamp = DateTimeOffset.Now,
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
