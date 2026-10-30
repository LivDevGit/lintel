using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Lintel.Core;
using Lintel.Services;
using Lintel.UI;
using Lintel.ViewModels;

namespace Lintel;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = null!;
    private Hub? _hub;
    private IslandManager? _islands;
    private TrayIcon? _tray;
    private HotkeyService? _hotkey;
    private SettingsWindow? _settingsWindow;

    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var demoIndex = Array.IndexOf(e.Args, "--demo");
        if (demoIndex >= 0 && demoIndex + 1 < e.Args.Length)
        {
            await RunDemoAsync(e.Args[demoIndex + 1]);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\Lintel.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // GPU rendering keeps transitions at the display's refresh rate for about half the CPU of
        // software rendering. LINTEL_SOFTWARE=1 is a fallback for drivers that mishandle
        // transparent windows (it caps animations at ~60 fps).
        if (Environment.GetEnvironmentVariable("LINTEL_SOFTWARE") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnUnhandled;
        // Release any reserved top strip (app bar) when Windows signs out or shuts down.
        SessionEnding += (_, _) => Quit();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"[fatal] {args.ExceptionObject}");

        _settings = SettingsStore.Load();
        SettingsStore.AutoSave(_settings);
        _settings.PropertyChanged += OnSettingChanged;

        _hub = new Hub(_settings);
        await _hub.StartAsync();

        if (e.Args.Contains("--drift-demo")) IslandWindow.DriftInterval = TimeSpan.FromSeconds(1.5);

        _islands = new IslandManager(_hub);
        _islands.Rebuild();

        _tray = new TrayIcon(() => _islands.ToggleNearCursor(), OpenSettings, Quit);

        _hotkey = new HotkeyService();
        _hotkey.Pressed += () => _islands.ToggleNearCursor();
        _hotkey.Register(_settings.Hotkey);

        if (e.Args.Contains("--settings")) OpenSettings();

        // Startup touches a lot of code once (WPF, WinRT, WMI). Compact and trim shortly after,
        // then occasionally, so the resident footprint reflects what the app actually uses.
        _trim.Tick += (_, _) =>
        {
            _trim.Interval = TimeSpan.FromMinutes(10);
            if (_settingsWindow != null) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            Lintel.Native.Win32.TrimWorkingSet();
        };
        _trim.Start();
    }

    private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _demo;

    /// <summary>Records the README demos with in-memory settings: no tray, hotkey or saved state.</summary>
    private async Task RunDemoAsync(string output)
    {
        _demo = true;
        DispatcherUnhandledException += OnUnhandled;
        _settings = new AppSettings
        {
            TopEdge = TopEdgeMode.Overlay,
            HideInFullscreen = false,
            PixelShift = false,
            HideWhenIdle = false,
            RememberLastTab = false,
            AccentMode = AccentMode.AlbumArt,
            CalendarFeeds = new System.Collections.ObjectModel.ObservableCollection<string> { "demo" },
        };
        _hub = new Hub(_settings);
        await _hub.StartAsync(demo: true);

        IslandWindow.DemoMode = true;
        _islands = new IslandManager(_hub);
        _islands.Rebuild();
        try
        {
            double Arg(string name, double fallback)
            {
                var i = Array.IndexOf(Environment.GetCommandLineArgs(), name);
                var args = Environment.GetCommandLineArgs();
                return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
            await new DemoDirector(_hub, _islands.Windows[0], output, Arg("--demo-scale", 2), Arg("--demo-fps", 30)).RunAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"[demo] failed: {ex}");
        }
        Quit();
    }

    public IEnumerable<IntPtr> IslandHandles() => _islands?.Handles ?? Enumerable.Empty<IntPtr>();

    public AppSettings Settings => _settings;

    public bool RegisterHotkey(string gesture) => _hotkey?.Register(gesture) ?? false;

    public void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.LaunchAtStartup):
                StartupRegistration.Apply(_settings.LaunchAtStartup);
                break;
            case nameof(AppSettings.Hotkey):
                _hotkey?.Register(_settings.Hotkey);
                break;
        }
    }

    public void Quit()
    {
        if (!_demo) SettingsStore.Save(_settings);
        _settingsWindow?.Close();
        _islands?.Dispose();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _hub?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"[unhandled] {e.Exception}");
        e.Handled = true;
    }
}
