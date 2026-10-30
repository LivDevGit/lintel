using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lintel.Core;
using Lintel.Services;
using Lintel.ViewModels;
using Forms = System.Windows.Forms;

namespace Lintel.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        DisplayModeBox.ItemsSource = Enum.GetValues<DisplayMode>();
        StyleBox.ItemsSource = Enum.GetValues<IslandStyle>();
        TopEdgeBox.ItemsSource = new[]
        {
            new { Value = TopEdgeMode.Slim, Label = "Slim down (recommended)" },
            new { Value = TopEdgeMode.Reserve, Label = "Reserve space at the top" },
            new { Value = TopEdgeMode.Overlay, Label = "Stay on top as usual" },
        };
        AccentBox.ItemsSource = Enum.GetValues<AccentMode>();
        DisplayBox.ItemsSource = Forms.Screen.AllScreens
            .Select((s, i) => new { Label = IslandManager.Describe(s, i), Device = s.DeviceName })
            .ToList();

        DataContext = settings;
        HotkeyBox.Text = settings.Hotkey;
        VersionText.Text = $"Lintel {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        UpdateSwatch();

        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => settings.PropertyChanged -= OnSettingsChanged;
        Nav.SelectedIndex = 0;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.CustomAccent)) UpdateSwatch();
    }

    private void UpdateSwatch()
    {
        var c = Hub.ParseColor(_settings.CustomAccent);
        Swatch.Background = c != null ? new SolidColorBrush(c.Value) : Brushes.Transparent;
    }

    private void OnTopEdgeChanged(object sender, SelectionChangedEventArgs e)
    {
        TopEdgeHint.Text = _settings.TopEdge switch
        {
            TopEdgeMode.Slim => "Over a maximized app it shrinks to a thin bar that clicks pass through, so tabs and search boxes stay usable. Rest the pointer at the very top to open it.",
            TopEdgeMode.Reserve => "Keeps a strip at the top of the display free, like a taskbar does, so maximized apps start below the island. Costs a little screen height.",
            _ => "The pill always floats over whatever is underneath, including browser tabs and title bars.",
        };
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        foreach (var panel in new FrameworkElement[] { PageGeneral, PageIsland, PageMedia, PageIndicators, PageCalendar, PageShelf, PageAbout })
            panel.Visibility = panel.Name == page ? Visibility.Visible : Visibility.Collapsed;
        Pages.ScrollToTop();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        if (mods == ModifierKeys.None)
        {
            HotkeyHint.Text = "Add at least one of Ctrl, Alt, Shift or Win.";
            return;
        }

        var gesture = HotkeyService.Format(mods, key);
        var previous = _settings.Hotkey;
        _settings.Hotkey = gesture;
        if (App.Current.RegisterHotkey(gesture))
        {
            HotkeyBox.Text = gesture;
            HotkeyHint.Text = "Saved. Press it anywhere to open or close the island.";
        }
        else
        {
            HotkeyHint.Text = $"{gesture} is already taken by another app. Try a different combination.";
            _settings.Hotkey = previous;
            HotkeyBox.Text = previous;
        }
    }

    private void OnAddFeed(object sender, RoutedEventArgs e)
    {
        var url = FeedBox.Text.Trim();
        if (!Uri.TryCreate(url.Replace("webcal://", "https://"), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            FeedStatus.Text = "That doesn't look like a web link.";
            return;
        }
        if (!_settings.CalendarFeeds.Contains(url)) _settings.CalendarFeeds.Add(url);
        FeedBox.Clear();
        FeedStatus.Text = "Added. Loading…";
        RefreshFeeds();
    }

    private void OnRemoveFeed(object sender, RoutedEventArgs e)
    {
        if (FeedList.SelectedItem is string url) _settings.CalendarFeeds.Remove(url);
    }

    private void OnRefreshFeeds(object sender, RoutedEventArgs e)
    {
        FeedStatus.Text = "Loading…";
        RefreshFeeds();
    }

    private async void RefreshFeeds()
    {
        var calendar = Hub.Instance.Calendar;
        await calendar.RefreshAsync();
        FeedStatus.Text = calendar.LastError ?? $"{calendar.Events.Count} event(s) in the next two days.";
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(SettingsStore.Folder);
        Process.Start(new ProcessStartInfo(SettingsStore.Folder) { UseShellExecute = true });
    }

    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.Quit();
}
