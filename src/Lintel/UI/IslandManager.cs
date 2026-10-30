using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using Lintel.Core;
using Lintel.Native;
using Lintel.ViewModels;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Lintel.UI;

/// <summary>Creates one island per display that should have one, and keeps them in place.</summary>
public sealed class IslandManager : IDisposable
{
    private readonly Hub _hub;
    private readonly List<IslandWindow> _windows = new();
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _rebuildSoon = new() { Interval = TimeSpan.FromMilliseconds(800) };

    public IslandManager(Hub hub)
    {
        _hub = hub;
        _follow.Tick += (_, _) => FollowCursor();
        _rebuildSoon.Tick += (_, _) => { _rebuildSoon.Stop(); Rebuild(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        hub.Settings.PropertyChanged += OnSettingsChanged;
    }

    public IEnumerable<IntPtr> Handles => _windows.Select(w => w.Handle);

    public static string Describe(Forms.Screen s, int index) =>
        $"Display {index + 1}{(s.Primary ? " (main)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}";

    public void Rebuild()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();

        foreach (var screen in TargetScreens())
        {
            var window = new IslandWindow(_hub, screen);
            window.Show();
            _windows.Add(window);
        }

        if (_hub.Settings.DisplayMode == DisplayMode.FollowCursor) _follow.Start();
        else _follow.Stop();
    }

    private IEnumerable<Forms.Screen> TargetScreens()
    {
        var all = Forms.Screen.AllScreens;
        var primary = Forms.Screen.PrimaryScreen ?? all[0];
        switch (_hub.Settings.DisplayMode)
        {
            case DisplayMode.All:
                return all;
            case DisplayMode.Specific:
                return new[] { all.FirstOrDefault(s => s.DeviceName == _hub.Settings.PreferredDisplay) ?? primary };
            case DisplayMode.FollowCursor:
                return new[] { CursorScreen() };
            default:
                return new[] { primary };
        }
    }

    private static Forms.Screen CursorScreen()
    {
        Win32.GetCursorPos(out var p);
        return Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
    }

    private void FollowCursor()
    {
        if (_windows.Count != 1) return;
        var w = _windows[0];
        if (w.State == IslandState.Expanded) return;
        var target = CursorScreen();
        if (target.DeviceName != w.Screen.DeviceName) w.PlaceOn(target);
    }

    /// <summary>Hotkey: toggle the island on the display the pointer is on.</summary>
    public void ToggleNearCursor()
    {
        if (_windows.Count == 0) return;
        var screen = CursorScreen();
        var w = _windows.FirstOrDefault(x => x.Screen.DeviceName == screen.DeviceName) ?? _windows[0];
        if (_hub.Settings.DisplayMode == DisplayMode.FollowCursor && w.Screen.DeviceName != screen.DeviceName) w.PlaceOn(screen);
        w.Toggle();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        // Displays settle over a second or so after docking/undocking.
        _rebuildSoon.Stop();
        _rebuildSoon.Start();
    });

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.DisplayMode) or nameof(AppSettings.PreferredDisplay)) Rebuild();
    }

    public void Dispose()
    {
        _follow.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }
}
