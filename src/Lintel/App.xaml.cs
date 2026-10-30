using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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

        _singleInstance = new Mutex(true, @"Local\Lintel.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // The island is a small transparent window, which Windows composes from system memory
        // anyway; software rendering avoids a Direct3D device and its large driver allocations.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnUnhandled;
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
        SettingsStore.Save(_settings);
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
