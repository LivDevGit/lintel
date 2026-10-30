using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core;
using Lintel.Services;

namespace Lintel.ViewModels;

public enum PeekKind { Track, Volume, Brightness, Power, Download }

public sealed record Peek(PeekKind Kind, string Glyph, string Title, string Subtitle, double? Level, ImageSource? Art = null);

public sealed class EventRow
{
    public required string TimeText { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required Brush Marker { get; init; }
    public bool IsNow { get; init; }
}

/// <summary>
/// The shared state behind every island window: one instance, many views.
/// </summary>
public sealed class Hub : ObservableObject, IDisposable
{
    public static Hub Instance { get; private set; } = null!;

    private static readonly Color[] FeedColors =
    {
        Color.FromRgb(0x5B, 0xC0, 0xFF), Color.FromRgb(0xFF, 0x8A, 0x65), Color.FromRgb(0x7C, 0xE0, 0x9A),
        Color.FromRgb(0xE5, 0x8B, 0xFF), Color.FromRgb(0xFF, 0xD1, 0x5C),
    };

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime _pausedSince = DateTime.MinValue;
    private DateTime _lastEventsBuild = DateTime.MinValue;

    public AppSettings Settings { get; }
    public MediaService Media { get; } = new();
    public VolumeService Volume { get; } = new();
    public BrightnessService Brightness { get; } = new();
    public BatteryService Battery { get; } = new();
    public CalendarService Calendar { get; }
    public ShelfService Shelf { get; } = new();
    public DownloadWatcher Downloads { get; } = new();
    public SpectrumService Spectrum { get; } = new();

    public event Action<Peek>? PeekRequested;

    public Hub(AppSettings settings)
    {
        Instance = this;
        Settings = settings;
        Calendar = new CalendarService(settings);
        UpdateAccent();
    }

    /// <summary>
    /// Demo mode wires everything up but starts no real sources: the demo director injects
    /// made-up media, power and calendar events, and nothing is saved.
    /// </summary>
    public async Task StartAsync(bool demo = false)
    {
        if (demo)
        {
            Shelf.Persist = false;
            Spectrum.Simulated = true;
        }
        else
        {
            Shelf.Load();
        }
        Shelf.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasShelfItems));

        Media.Changed += OnMediaChanged;
        Media.TrackChanged += OnTrackChanged;
        Volume.VolumeChanged += OnVolumeChanged;
        Brightness.BrightnessChanged += OnBrightnessChanged;
        Battery.Changed += OnBatteryChanged;
        Calendar.Changed += () => BuildEvents();
        Downloads.ActiveChanged += () => RaisePill();
        Downloads.Completed += OnDownloadCompleted;
        Settings.PropertyChanged += OnSettingChanged;

        if (!demo)
        {
            Volume.Start();
            Brightness.Start();
            Battery.Start();
            if (Settings.WatchDownloads) Downloads.Start();
            if (Settings.CalendarEnabled) Calendar.Start();
            await Media.StartAsync();
        }

        _clock.Tick += (_, _) => OnClock();
        if (!demo) _clock.Start(); // the demo director ticks the clock once per recorded frame
        OnClock();
        BuildEvents();
    }

    /// <summary>Demo recording: advance the once-a-tick work (progress, clock) by hand.</summary>
    public void DemoTick() => OnClock();

    // ---------------------------------------------------------------- media

    public bool HasMedia => Media.Current.HasSession && !string.IsNullOrWhiteSpace(Media.Current.Title);
    public bool IsPlaying => Media.Current.IsPlaying;
    public string Title => Media.Current.Title;
    public string Artist => Media.Current.Artist;
    public string SourceApp => Media.Current.SourceApp;
    public ImageSource? Art => Media.Current.Art;
    public bool HasArt => Media.Current.Art != null;
    public string PlayGlyph => IsPlaying ? "" : "";

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private string _elapsedText = "0:00";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private string _remainingText = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    public bool CanSeek => Media.Current.CanSeek && Media.Current.Duration > TimeSpan.Zero;

    /// <summary>Whether the collapsed pill should show the "now playing" activity.</summary>
    public bool MediaActive =>
        Settings.ShowMediaActivity && HasMedia &&
        (IsPlaying || (DateTime.Now - _pausedSince).TotalSeconds < Settings.PausedActivitySeconds);

    private void OnMediaChanged()
    {
        if (!Media.Current.IsPlaying && _pausedSince == DateTime.MinValue) _pausedSince = DateTime.Now;
        if (Media.Current.IsPlaying) _pausedSince = DateTime.MinValue;
        if (!HasMedia) _pausedSince = DateTime.MinValue.AddYears(1);

        foreach (var name in new[] { nameof(HasMedia), nameof(IsPlaying), nameof(Title), nameof(Artist), nameof(SourceApp), nameof(Art), nameof(HasArt), nameof(PlayGlyph), nameof(CanSeek) })
            OnPropertyChanged(name);
        UpdateAccent();
        UpdateProgress();
        RaisePill();
        UpdateSpectrum();
    }

    private void OnTrackChanged()
    {
        if (!Settings.ShowTrackPeek || !Media.Current.IsPlaying) return;
        PeekRequested?.Invoke(new Peek(PeekKind.Track, "", Title, Artist, null, Art));
    }

    private void UpdateProgress()
    {
        var m = Media.Current;
        var pos = m.LivePosition;
        // Never exactly 0: a zero ScaleX is a degenerate transform and WPF renders stray pixels for it.
        Progress = m.Duration > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / m.Duration.TotalSeconds, 0.001, 1) : 0.001;
        ElapsedText = Format(pos);
        RemainingText = m.Duration > TimeSpan.Zero ? "-" + Format(m.Duration - pos) : "";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public void PlayPause() => Media.PlayPause();
    public void Next() => Media.Next();
    public void Previous() => Media.Previous();
    public void SeekTo(double fraction) => Media.SeekTo(fraction);

    private void UpdateSpectrum()
    {
        var want = Settings.LiveVisualizer && Settings.ShowMediaActivity && IsPlaying;
        if (want && !Spectrum.Running) Spectrum.Start();
        else if (!want && Spectrum.Running) Spectrum.Stop();
    }

    // ---------------------------------------------------------------- pill

    public string PillText
    {
        get
        {
            if (MediaActive && Settings.ShowTitleInPill)
                return string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title}  ·  {Artist}";
            if (Settings.ShowClockWhenIdle) return ClockText;
            return "";
        }
    }

    public bool PillShowsArt => MediaActive && HasArt;
    public bool PillShowsBars => MediaActive && IsPlaying && Settings.LiveVisualizer;
    public bool PillShowsPaused => MediaActive && !IsPlaying;
    public bool PillShowsDownload => !MediaActive && Downloads.ActiveCount > 0;
    public bool PillShowsCharging => !MediaActive && Downloads.ActiveCount == 0 && Settings.ShowBattery && HasBattery && IsCharging;

    private void RaisePill()
    {
        foreach (var name in new[] { nameof(MediaActive), nameof(PillText), nameof(PillShowsArt), nameof(PillShowsBars), nameof(PillShowsPaused), nameof(PillShowsDownload), nameof(PillShowsCharging), nameof(DownloadsText) })
            OnPropertyChanged(name);
    }

    public string DownloadsText => Downloads.ActiveCount switch
    {
        0 => "",
        1 => "1 download in progress",
        var n => $"{n} downloads in progress",
    };

    // ---------------------------------------------------------------- accent

    private Color _accent;
    public Color Accent { get => _accent; private set { if (Set(ref _accent, value)) { OnPropertyChanged(nameof(AccentBrush)); OnPropertyChanged(nameof(AccentWashBrush)); } } }

    public Brush AccentBrush { get { var b = new SolidColorBrush(Accent); b.Freeze(); return b; } }

    /// <summary>Soft radial tint for the media card background.</summary>
    public Brush AccentWashBrush
    {
        get
        {
            var b = new RadialGradientBrush(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B), Color.FromArgb(0, Accent.R, Accent.G, Accent.B))
            {
                Center = new System.Windows.Point(0.15, 0.3),
                GradientOrigin = new System.Windows.Point(0.15, 0.3),
                RadiusX = 0.7,
                RadiusY = 1.1,
            };
            b.Freeze();
            return b;
        }
    }

    private void UpdateAccent()
    {
        Accent = Settings.AccentMode switch
        {
            AccentMode.Custom => ParseColor(Settings.CustomAccent) ?? SystemAccent.Get(),
            AccentMode.AlbumArt when Media.Current.ArtColor is { } c => c,
            _ => SystemAccent.Get(),
        };
    }

    public static Color? ParseColor(string? text)
    {
        try { return text == null ? null : (Color)ColorConverter.ConvertFromString(text); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- clock

    private string _clockText = "";
    public string ClockText { get => _clockText; private set { if (Set(ref _clockText, value)) OnPropertyChanged(nameof(PillText)); } }

    private string _dateText = "";
    public string DateText { get => _dateText; private set => Set(ref _dateText, value); }

    private bool _lastMediaActive;

    private void OnClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("t", CultureInfo.CurrentCulture);
        DateText = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        UpdateProgress();

        // The paused-activity timeout expires on its own, so poll it.
        if (MediaActive != _lastMediaActive)
        {
            _lastMediaActive = MediaActive;
            RaisePill();
        }
        if ((now - _lastEventsBuild).TotalSeconds >= 30) BuildEvents();
    }

    // ---------------------------------------------------------------- volume, brightness

    private void OnVolumeChanged(float level, bool muted)
    {
        if (!Settings.ShowVolumeHud) return;
        var glyph = muted || level <= 0.001 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
        PeekRequested?.Invoke(new Peek(PeekKind.Volume, glyph, muted ? "Muted" : "Volume", $"{Math.Round(level * 100)}%", muted ? 0 : level));
    }

    private void OnBrightnessChanged(int level)
    {
        if (!Settings.ShowBrightnessHud) return;
        PeekRequested?.Invoke(new Peek(PeekKind.Brightness, "", "Brightness", $"{level}%", level / 100.0));
    }

    /// <summary>Demo mode: behave as if the system volume changed.</summary>
    public void SimulateVolume(float level) => OnVolumeChanged(level, false);

    public void NudgeVolume(int wheelDelta) => Volume.Nudge(wheelDelta > 0 ? 0.04f : -0.04f);

    // ---------------------------------------------------------------- battery

    public bool HasBattery => Battery.Current.HasBattery;
    public bool IsCharging => Battery.Current.Charging || Battery.Current.PluggedIn;
    public bool ShowBatteryBadge => Settings.ShowBattery && HasBattery;
    public int BatteryPercent => Battery.Current.Percent;
    public string BatteryText => Settings.ShowBatteryPercent ? $"{BatteryPercent}%" : "";
    public double BatteryLevel => BatteryPercent / 100.0;
    public Brush BatteryFill
    {
        get
        {
            var c = IsCharging ? Color.FromRgb(0x6B, 0xE3, 0x8C)
                : Battery.Current.Saver ? Color.FromRgb(0xFF, 0xC8, 0x4A)
                : BatteryPercent <= Settings.LowBatteryThreshold ? Color.FromRgb(0xFF, 0x5F, 0x57)
                : Color.FromRgb(0xF2, 0xF2, 0xF5);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    private void OnBatteryChanged(BatterySnapshot previous, BatterySnapshot current)
    {
        foreach (var name in new[] { nameof(HasBattery), nameof(IsCharging), nameof(ShowBatteryBadge), nameof(BatteryPercent), nameof(BatteryText), nameof(BatteryLevel), nameof(BatteryFill) })
            OnPropertyChanged(name);
        RaisePill();

        // The first reading only fills in the defaults; nothing actually changed yet.
        if (!previous.HasBattery || !current.HasBattery || !Settings.PowerPeeks) return;

        if (current.PluggedIn != previous.PluggedIn)
        {
            var detail = current.PluggedIn
                ? current.MinutesToFull is { } m && m > 0 ? $"{current.Percent}% · full in {Duration(m)}" : $"{current.Percent}%"
                : current.MinutesLeft is { } l && l > 0 ? $"{current.Percent}% · about {Duration(l)} left" : $"{current.Percent}%";
            PeekRequested?.Invoke(new Peek(PeekKind.Power, current.PluggedIn ? "" : "",
                current.PluggedIn ? "Charging" : "On battery", detail, current.Percent / 100.0));
        }
        else if (!current.PluggedIn && previous.Percent > Settings.LowBatteryThreshold && current.Percent <= Settings.LowBatteryThreshold)
        {
            PeekRequested?.Invoke(new Peek(PeekKind.Power, "", "Battery low", $"{current.Percent}% remaining", current.Percent / 100.0));
        }
        else if (current.Saver != previous.Saver)
        {
            PeekRequested?.Invoke(new Peek(PeekKind.Power, "", current.Saver ? "Energy saver on" : "Energy saver off", $"{current.Percent}%", null));
        }
    }

    private static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";

    // ---------------------------------------------------------------- calendar

    public ObservableCollection<EventRow> Events { get; } = new();

    private string _calendarHint = "";
    public string CalendarHint { get => _calendarHint; private set => Set(ref _calendarHint, value); }

    public bool ShowCalendar => Settings.CalendarEnabled;

    private void BuildEvents()
    {
        _lastEventsBuild = DateTime.Now;
        Events.Clear();
        if (!Settings.CalendarEnabled) return;

        var now = DateTime.Now;
        var endOfToday = DateTime.Today.AddDays(1);
        var upcoming = Calendar.Events
            .Where(e => !(Settings.HideAllDayEvents && e.AllDay))
            .Where(e => e.End > now || (e.AllDay && e.Start.Date == DateTime.Today))
            .Where(e => e.Start < endOfToday)
            .Take(8);

        foreach (var e in upcoming)
        {
            var color = FeedColors[e.FeedIndex % FeedColors.Length];
            var marker = new SolidColorBrush(color);
            marker.Freeze();
            var isNow = !e.AllDay && e.Start <= now && e.End > now;
            string detail;
            if (e.AllDay) detail = "All day";
            else if (isNow) detail = $"Now · until {e.End.ToString("t", CultureInfo.CurrentCulture)}";
            else if ((e.Start - now).TotalMinutes < 60) detail = $"In {Math.Max(1, (int)(e.Start - now).TotalMinutes)} min";
            else detail = e.End > e.Start ? $"{e.Start.ToString("t", CultureInfo.CurrentCulture)} – {e.End.ToString("t", CultureInfo.CurrentCulture)}" : "";
            if (!string.IsNullOrWhiteSpace(e.Location)) detail = detail.Length > 0 ? $"{detail} · {e.Location}" : e.Location!;

            Events.Add(new EventRow
            {
                TimeText = e.AllDay ? "" : e.Start.ToString("t", CultureInfo.CurrentCulture),
                Title = e.Title,
                Detail = detail,
                Marker = marker,
                IsNow = isNow,
            });
        }

        CalendarHint = Settings.CalendarFeeds.Count == 0
            ? "Add a calendar link in Settings to see your day here."
            : Calendar.LastError != null && Calendar.Events.Count == 0
                ? "Couldn't load your calendar."
                : "Nothing else today.";
        OnPropertyChanged(nameof(HasEvents));
    }

    public bool HasEvents => Events.Count > 0;

    // ---------------------------------------------------------------- shelf & downloads

    public bool HasShelfItems => Shelf.Items.Count > 0;

    private void OnDownloadCompleted(string path)
    {
        RaisePill();
        if (Settings.AddDownloadsToShelf && Settings.ShelfEnabled) Shelf.AddPaths(new[] { path });
        PeekRequested?.Invoke(new Peek(PeekKind.Download, "", "Download finished", Path.GetFileName(path), null));
    }

    // ---------------------------------------------------------------- settings

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.AccentMode):
            case nameof(AppSettings.CustomAccent):
                UpdateAccent();
                break;
            case nameof(AppSettings.ShowMediaActivity):
            case nameof(AppSettings.ShowTitleInPill):
            case nameof(AppSettings.ShowClockWhenIdle):
            case nameof(AppSettings.PausedActivitySeconds):
                RaisePill();
                UpdateSpectrum();
                break;
            case nameof(AppSettings.LiveVisualizer):
                RaisePill();
                UpdateSpectrum();
                break;
            case nameof(AppSettings.ShowBattery):
            case nameof(AppSettings.ShowBatteryPercent):
            case nameof(AppSettings.LowBatteryThreshold):
                OnPropertyChanged(nameof(ShowBatteryBadge));
                OnPropertyChanged(nameof(BatteryText));
                OnPropertyChanged(nameof(BatteryFill));
                RaisePill();
                break;
            case nameof(AppSettings.CalendarEnabled):
                OnPropertyChanged(nameof(ShowCalendar));
                if (Settings.CalendarEnabled) Calendar.Start();
                BuildEvents();
                break;
            case nameof(AppSettings.CalendarFeeds):
                _ = Calendar.RefreshAsync();
                BuildEvents();
                break;
            case nameof(AppSettings.HideAllDayEvents):
                BuildEvents();
                break;
            case nameof(AppSettings.WatchDownloads):
                if (Settings.WatchDownloads) Downloads.Start();
                else Downloads.Stop();
                RaisePill();
                break;
        }
    }

    public void Dispose()
    {
        _clock.Stop();
        Spectrum.Dispose();
        Volume.Dispose();
        Brightness.Dispose();
        Battery.Dispose();
        Downloads.Dispose();
    }
}
