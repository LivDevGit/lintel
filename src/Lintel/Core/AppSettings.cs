using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Lintel.Core;

public enum DisplayMode { Primary, FollowCursor, Specific, All }
public enum IslandStyle { Floating, Attached }
public enum AccentMode { System, AlbumArt, Custom }
public enum IslandTab { Home, Shelf }

public sealed class AppSettings : ObservableObject
{
    // General
    private bool _launchAtStartup;
    public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }

    private DisplayMode _displayMode = DisplayMode.Primary;
    public DisplayMode DisplayMode { get => _displayMode; set => Set(ref _displayMode, value); }

    private string? _preferredDisplay;
    public string? PreferredDisplay { get => _preferredDisplay; set => Set(ref _preferredDisplay, value); }

    private bool _hideInFullscreen = true;
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    private bool _hideFromCapture;
    public bool HideFromCapture { get => _hideFromCapture; set => Set(ref _hideFromCapture, value); }

    private string _hotkey = "Ctrl+Alt+L";
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }

    // Island
    private IslandStyle _style = IslandStyle.Floating;
    public IslandStyle Style { get => _style; set => Set(ref _style, value); }

    private double _collapsedWidth = 200;
    public double CollapsedWidth { get => _collapsedWidth; set => Set(ref _collapsedWidth, Math.Clamp(value, 140, 320)); }

    private double _collapsedHeight = 32;
    public double CollapsedHeight { get => _collapsedHeight; set => Set(ref _collapsedHeight, Math.Clamp(value, 26, 40)); }

    private bool _openOnHover = true;
    public bool OpenOnHover { get => _openOnHover; set => Set(ref _openOnHover, value); }

    private int _hoverDelayMs = 180;
    public int HoverDelayMs { get => _hoverDelayMs; set => Set(ref _hoverDelayMs, Math.Clamp(value, 0, 1500)); }

    private int _closeDelayMs = 450;
    public int CloseDelayMs { get => _closeDelayMs; set => Set(ref _closeDelayMs, Math.Clamp(value, 100, 3000)); }

    // OLED care
    private bool _hideWhenIdle;
    public bool HideWhenIdle { get => _hideWhenIdle; set => Set(ref _hideWhenIdle, value); }

    private int _idleHideSeconds = 8;
    public int IdleHideSeconds { get => _idleHideSeconds; set => Set(ref _idleHideSeconds, Math.Clamp(value, 2, 120)); }

    private bool _pixelShift = true;
    public bool PixelShift { get => _pixelShift; set => Set(ref _pixelShift, value); }

    private bool _showShadow = true;
    public bool ShowShadow { get => _showShadow; set => Set(ref _showShadow, value); }

    private AccentMode _accentMode = AccentMode.AlbumArt;
    public AccentMode AccentMode { get => _accentMode; set => Set(ref _accentMode, value); }

    private string _customAccent = "#FF8A5CF6";
    public string CustomAccent { get => _customAccent; set => Set(ref _customAccent, value); }

    private bool _showClockWhenIdle = true;
    public bool ShowClockWhenIdle { get => _showClockWhenIdle; set => Set(ref _showClockWhenIdle, value); }

    private bool _scrollChangesVolume = true;
    public bool ScrollChangesVolume { get => _scrollChangesVolume; set => Set(ref _scrollChangesVolume, value); }

    private bool _rememberLastTab = true;
    public bool RememberLastTab { get => _rememberLastTab; set => Set(ref _rememberLastTab, value); }

    private IslandTab _lastTab = IslandTab.Home;
    public IslandTab LastTab { get => _lastTab; set => Set(ref _lastTab, value); }

    // Media
    private bool _showMediaActivity = true;
    public bool ShowMediaActivity { get => _showMediaActivity; set => Set(ref _showMediaActivity, value); }

    private bool _showTitleInPill = true;
    public bool ShowTitleInPill { get => _showTitleInPill; set => Set(ref _showTitleInPill, value); }

    private bool _showTrackPeek = true;
    public bool ShowTrackPeek { get => _showTrackPeek; set => Set(ref _showTrackPeek, value); }

    private double _peekSeconds = 2.5;
    public double PeekSeconds { get => _peekSeconds; set => Set(ref _peekSeconds, Math.Clamp(value, 1, 10)); }

    private bool _liveVisualizer = true;
    public bool LiveVisualizer { get => _liveVisualizer; set => Set(ref _liveVisualizer, value); }

    private int _pausedActivitySeconds = 20;
    public int PausedActivitySeconds { get => _pausedActivitySeconds; set => Set(ref _pausedActivitySeconds, Math.Clamp(value, 0, 600)); }

    // Indicators
    private bool _showVolumeHud = true;
    public bool ShowVolumeHud { get => _showVolumeHud; set => Set(ref _showVolumeHud, value); }

    private bool _showBrightnessHud = true;
    public bool ShowBrightnessHud { get => _showBrightnessHud; set => Set(ref _showBrightnessHud, value); }

    // Battery
    private bool _showBattery = true;
    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }

    private bool _showBatteryPercent = true;
    public bool ShowBatteryPercent { get => _showBatteryPercent; set => Set(ref _showBatteryPercent, value); }

    private bool _powerPeeks = true;
    public bool PowerPeeks { get => _powerPeeks; set => Set(ref _powerPeeks, value); }

    private int _lowBatteryThreshold = 20;
    public int LowBatteryThreshold { get => _lowBatteryThreshold; set => Set(ref _lowBatteryThreshold, Math.Clamp(value, 5, 50)); }

    // Calendar
    private bool _calendarEnabled = true;
    public bool CalendarEnabled { get => _calendarEnabled; set => Set(ref _calendarEnabled, value); }

    private ObservableCollection<string> _calendarFeeds = new();
    public ObservableCollection<string> CalendarFeeds
    {
        get => _calendarFeeds;
        set
        {
            _calendarFeeds.CollectionChanged -= OnFeedsChanged;
            _calendarFeeds = value ?? new ObservableCollection<string>();
            _calendarFeeds.CollectionChanged += OnFeedsChanged;
            OnPropertyChanged();
        }
    }

    private bool _hideAllDayEvents;
    public bool HideAllDayEvents { get => _hideAllDayEvents; set => Set(ref _hideAllDayEvents, value); }

    // Shelf
    private bool _shelfEnabled = true;
    public bool ShelfEnabled { get => _shelfEnabled; set => Set(ref _shelfEnabled, value); }

    private bool _openShelfWhenItems;
    public bool OpenShelfWhenItems { get => _openShelfWhenItems; set => Set(ref _openShelfWhenItems, value); }

    private bool _removeAfterDragOut;
    public bool RemoveAfterDragOut { get => _removeAfterDragOut; set => Set(ref _removeAfterDragOut, value); }

    private bool _watchDownloads = true;
    public bool WatchDownloads { get => _watchDownloads; set => Set(ref _watchDownloads, value); }

    private bool _addDownloadsToShelf;
    public bool AddDownloadsToShelf { get => _addDownloadsToShelf; set => Set(ref _addDownloadsToShelf, value); }

    public AppSettings()
    {
        _calendarFeeds.CollectionChanged += OnFeedsChanged;
    }

    private void OnFeedsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(CalendarFeeds));
}

public static class SettingsStore
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lintel");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static DispatcherTimer? _debounce;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings.load", ex);
        }
        return new AppSettings();
    }

    /// <summary>Saves shortly after each change, so slider drags don't hammer the disk.</summary>
    public static void AutoSave(AppSettings settings)
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Save(settings);
        };
        settings.PropertyChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings.save", ex);
        }
    }
}
