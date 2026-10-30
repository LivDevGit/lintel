using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lintel.Core;
using Lintel.Native;
using Lintel.Services;
using Lintel.ViewModels;
using Forms = System.Windows.Forms;

namespace Lintel.UI;

public enum IslandState { Collapsed, Peek, Expanded }

public partial class IslandWindow : Window
{
    private const double ExpandedWidth = 580;
    private const double ExpandedHeight = 240;
    private const double PeekRowHeight = 46;
    private const double FloatingOffset = 8;
    private const double FullWindowWidth = 660;
    private const double FullWindowHeight = 320;

    private readonly Hub _hub;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _peekTimer = new();

    private readonly Spring _width, _height, _radius, _collapsedFade, _peekFade, _expandedFade, _shadow, _presence;
    private readonly Spring[] _springs;
    private bool _animating;

    // OLED care: slow positional drift, and fading out while idle.
    private readonly TranslateTransform _drift = new();
    private readonly DispatcherTimer _driftTimer = new() { Interval = DriftInterval };

    /// <summary>How often pixel shift moves the island. <c>--drift-demo</c> shortens it to show the effect.</summary>
    public static TimeSpan DriftInterval { get; set; } = TimeSpan.FromMinutes(2.5);
    private readonly Random _random = new();
    private DateTime _lastActivity = DateTime.Now;
    private TimeSpan _lastFrame;

    private IntPtr _hwnd;
    private int _tickCount;
    private Size _windowSize = new(FullWindowWidth, FullWindowHeight);
    private bool _hiddenForFullscreen;

    private DateTime? _hoverStart;
    private DateTime? _leaveStart;
    private DateTime _openedAt;
    private bool _openedByPointer;
    private bool _enteredSinceOpen;
    private bool _buttonDownOutside;
    private bool _pressInside;
    private DateTime _lastDragOver = DateTime.MinValue;
    private int _menusOpen;
    private Peek? _peek;

    public Forms.Screen Screen { get; private set; }
    public IslandState State { get; private set; } = IslandState.Collapsed;
    public IntPtr Handle => _hwnd;

    public IslandWindow(Hub hub, Forms.Screen screen)
    {
        InitializeComponent();
        _hub = hub;
        _settings = hub.Settings;
        Screen = screen;
        DataContext = hub;

        var ch = _settings.CollapsedHeight;
        _width = new Spring(_settings.CollapsedWidth, 380, 30);
        _height = new Spring(ch, 380, 30);
        _radius = new Spring(ch / 2, 380, 34);
        _collapsedFade = new Spring(1, 500, 45);
        _peekFade = new Spring(0, 500, 45);
        _expandedFade = new Spring(0, 420, 41);
        _shadow = new Spring(0, 300, 35);
        _presence = new Spring(1, 120, 22);
        _springs = new[] { _width, _height, _radius, _collapsedFade, _peekFade, _expandedFade, _shadow, _presence };

        Root.RenderTransform = _drift;
        ExpandedLayer.RenderTransform = _expandedShift;
        _driftTimer.Tick += (_, _) => Drift();
        _driftTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { ApplyLayoutSettings(); SnapToTargets(); };
        Closed += OnClosed;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => PlaceOn(Screen), DispatcherPriority.Background);

        _tick.Tick += OnTick;
        _peekTimer.Tick += (_, _) => EndPeek();

        hub.PeekRequested += OnPeekRequested;
        hub.PropertyChanged += OnHubChanged;
        _settings.PropertyChanged += OnSettingsChanged;

        Island.MouseLeftButtonUp += OnIslandClick;
        Island.PreviewMouseLeftButtonDown += (_, _) => _pressInside = true;
        Island.PreviewMouseWheel += OnIslandWheel;
        Island.DragEnter += OnDragEnter;
        Island.DragOver += OnDragOver;
        Island.Drop += OnDrop;
        Island.AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => _menusOpen++), true);
        Island.AddHandler(ContextMenuClosingEvent, new ContextMenuEventHandler((_, _) => _menusOpen = Math.Max(0, _menusOpen - 1)), true);

        SelectTab(_settings.RememberLastTab ? _settings.LastTab : IslandTab.Home);
    }

    // ================================================================ window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        ApplyCaptureAffinity();
        PlaceOn(Screen);
        _tick.Start();
    }

    public void PlaceOn(Forms.Screen screen)
    {
        Screen = screen;
        if (_hwnd == IntPtr.Zero) return;
        var b = screen.Bounds;
        var scale = Win32.ScaleForPoint(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var w = (int)Math.Round(_windowSize.Width * scale);
        var h = (int)Math.Round(_windowSize.Height * scale);
        // Even width/offset so the island's centre lands on the same pixel at every window size.
        if (((b.Width - w) & 1) != 0) w++;
        var x = b.Left + (b.Width - w) / 2;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, b.Top, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// A transparent window costs in proportion to its whole surface on every redraw, so while
    /// the island rests it shrinks to hug the pill; it grows back before any animation starts.
    /// </summary>
    private void FitWindow(bool full)
    {
        var size = full
            ? new Size(FullWindowWidth, FullWindowHeight)
            : new Size(Math.Min(FullWindowWidth, CollapsedTargetWidth + 48), Math.Min(FullWindowHeight, TopOffset + _settings.CollapsedHeight + 16));
        if (Math.Abs(size.Width - _windowSize.Width) < 0.5 && Math.Abs(size.Height - _windowSize.Height) < 0.5) return;
        _windowSize = size;
        PlaceOn(Screen);
    }

    public void ApplyCaptureAffinity()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowDisplayAffinity(_hwnd, _settings.HideFromCapture ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
    }

    private void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        _peekTimer.Stop();
        _driftTimer.Stop();
        StopAnimating();
        _hub.PeekRequested -= OnPeekRequested;
        _hub.PropertyChanged -= OnHubChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    // ================================================================ state machine

    private double TopOffset => _settings.Style == IslandStyle.Floating ? FloatingOffset : 0;

    private double CollapsedTargetWidth =>
        _settings.CollapsedWidth + (_hub.MediaActive ? 120 : 0);

    public void Expand(IslandTab? tab = null, bool byPointer = true)
    {
        if (tab != null) SelectTab(tab.Value);
        else SelectTab(DefaultTab());
        _peekTimer.Stop();
        _peek = null;
        State = IslandState.Expanded;
        _openedAt = DateTime.Now;
        _openedByPointer = byPointer;
        _enteredSinceOpen = byPointer;
        _leaveStart = null;
        UpdateTargets();
    }

    public void Collapse()
    {
        if (State == IslandState.Collapsed) return;
        State = IslandState.Collapsed;
        _hoverStart = null;
        _peek = null;
        _peekTimer.Stop();
        UpdateTargets();
    }

    public void Toggle()
    {
        if (State == IslandState.Expanded) Collapse();
        else Expand(byPointer: false);
    }

    private IslandTab DefaultTab()
    {
        if (_settings.ShelfEnabled && _settings.OpenShelfWhenItems && _hub.HasShelfItems) return IslandTab.Shelf;
        if (_settings.RememberLastTab) return _settings.ShelfEnabled ? _settings.LastTab : IslandTab.Home;
        return IslandTab.Home;
    }

    private void OnPeekRequested(Peek peek)
    {
        if (State == IslandState.Expanded || _hiddenForFullscreen) return;
        _peek = peek;
        ShowPeekContent(peek);
        State = IslandState.Peek;
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(peek.Kind is PeekKind.Volume or PeekKind.Brightness ? 1.6 : _settings.PeekSeconds);
        _peekTimer.Start();
        UpdateTargets();
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        if (State != IslandState.Peek) return;
        State = IslandState.Collapsed;
        _peek = null;
        UpdateTargets();
    }

    private void ShowPeekContent(Peek peek)
    {
        PeekTitle.Text = peek.Title;
        PeekSubtitle.Text = peek.Subtitle;
        var hasArt = peek.Art != null;
        PeekArt.Visibility = hasArt ? Visibility.Visible : Visibility.Collapsed;
        PeekArtBrush.ImageSource = peek.Art;
        PeekGlyph.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        PeekGlyph.Text = peek.Glyph;
        PeekMeter.Visibility = peek.Level != null ? Visibility.Visible : Visibility.Collapsed;
        PeekMeterFill.Width = 120 * Math.Clamp(peek.Level ?? 0, 0, 1);
    }

    /// <summary>Sets spring targets for the current state; the render loop does the rest.</summary>
    private void UpdateTargets()
    {
        var ch = _settings.CollapsedHeight;
        switch (State)
        {
            case IslandState.Collapsed:
                _width.Target = CollapsedTargetWidth;
                _height.Target = ch;
                _radius.Target = ch / 2;
                _collapsedFade.Target = 1;
                _peekFade.Target = 0;
                _expandedFade.Target = 0;
                _shadow.Target = 0;
                break;
            case IslandState.Peek:
                _width.Target = Math.Max(CollapsedTargetWidth, 380);
                _height.Target = ch + PeekRowHeight;
                _radius.Target = 22;
                _collapsedFade.Target = 1;
                _peekFade.Target = 1;
                _expandedFade.Target = 0;
                _shadow.Target = _settings.ShowShadow ? 0.7 : 0;
                break;
            case IslandState.Expanded:
                _width.Target = ExpandedWidth;
                _height.Target = ExpandedHeight;
                _radius.Target = 28;
                _collapsedFade.Target = 0;
                _peekFade.Target = 0;
                _expandedFade.Target = 1;
                _shadow.Target = _settings.ShowShadow ? 1 : 0;
                break;
        }

        CollapsedLayer.IsHitTestVisible = State != IslandState.Expanded;
        PeekLayer.IsHitTestVisible = false;
        ExpandedLayer.IsHitTestVisible = State == IslandState.Expanded;
        FitWindow(full: true);
        StartAnimating();
    }

    private void SnapToTargets()
    {
        UpdateTargets();
        foreach (var s in _springs)
        {
            s.Value = s.Target;
            s.Velocity = 0;
        }
        ApplyFrame();
        StopAnimating();
        if (State == IslandState.Collapsed) FitWindow(full: false);
    }

    // ================================================================ animation loop

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 20.0);
        _lastFrame = now;

        foreach (var s in _springs) s.Step(dt);
        ApplyFrame();
        if (!_springs.All(s => s.Settled)) return;
        StopAnimating();
        if (State == IslandState.Collapsed) FitWindow(full: false);
    }

    private void ApplyFrame()
    {
        var w = Math.Max(20, _width.Value);
        var h = Math.Max(10, _height.Value);
        var r = Math.Max(0, _radius.Value);
        var top = _settings.Style == IslandStyle.Attached ? 0 : r;

        Island.Width = w;
        Island.Height = h;
        Island.Margin = new Thickness(0, TopOffset, 0, 0);
        Island.CornerRadius = new CornerRadius(top, top, r, r);
        Island.Clip = IslandShape.Build(w, h, top, r);

        ShadowPlate.Width = w;
        ShadowPlate.Height = h;
        ShadowPlate.Margin = Island.Margin;
        ShadowPlate.CornerRadius = Island.CornerRadius;
        ShadowPlate.Opacity = Math.Clamp(_shadow.Value, 0, 1);

        Root.Opacity = _hiddenForFullscreen ? 0 : Math.Clamp(_presence.Value, 0, 1);
        SetLayer(CollapsedLayer, _collapsedFade.Value);
        SetLayer(PeekLayer, _peekFade.Value);
        SetLayer(ExpandedLayer, _expandedFade.Value);
        // Slight drop-in for the expanded content as it fades in.
        _expandedShift.Y = (1 - Math.Clamp(_expandedFade.Value, 0, 1)) * -10;
    }

    private readonly TranslateTransform _expandedShift = new();

    /// <summary>Fully faded layers are hidden, so their bindings and animations cost nothing to render.</summary>
    private static void SetLayer(UIElement layer, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        layer.Opacity = opacity;
        var visibility = opacity < 0.01 ? Visibility.Hidden : Visibility.Visible;
        if (layer.Visibility != visibility) layer.Visibility = visibility;
    }

    // ================================================================ pointer polling

    private Rect IslandRect()
    {
        var w = _width.Value;
        var left = (Root.ActualWidth - w) / 2;
        return new Rect(left, TopOffset, w, _height.Value);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _tickCount++;
        if (_tickCount % 12 == 0) CheckFullscreen();
        if (_tickCount % 50 == 0) KeepOnTop();
        if (_hiddenForFullscreen || PresentationSource.FromVisual(Root) == null) return;

        Win32.GetCursorPos(out var cursor);
        Point p;
        try { p = Root.PointFromScreen(new Point(cursor.X, cursor.Y)); }
        catch (InvalidOperationException) { return; }

        var leftDown = Win32.IsLeftButtonDown();
        if (!leftDown) _pressInside = false;
        var now = DateTime.Now;
        var island = IslandRect();
        // Hot zone: the pill, a little padding, and everything up to the screen edge above it.
        var hot = new Rect(island.Left - 10, -2, island.Width + 20, island.Bottom + 8);
        UpdatePresence(now, hot.Contains(p));

        // Poll quickly only while the pointer is anywhere near; otherwise a few times a second is plenty.
        var near = p.Y < island.Bottom + 160 && p.X > island.Left - 200 && p.X < island.Right + 200;
        var interval = TimeSpan.FromMilliseconds(near || State != IslandState.Collapsed || leftDown ? 40 : 160);
        if (_tick.Interval != interval) _tick.Interval = interval;

        if (State != IslandState.Expanded)
        {
            if (!hot.Contains(p))
            {
                _buttonDownOutside = leftDown;
                _hoverStart = null;
                return;
            }

            // Arrived while holding the button down elsewhere: that's a drag heading for the shelf.
            if (leftDown && _buttonDownOutside)
            {
                if (_settings.ShelfEnabled) Expand(IslandTab.Shelf);
                return;
            }

            if (_settings.OpenOnHover && !leftDown)
            {
                _hoverStart ??= now;
                if ((now - _hoverStart.Value).TotalMilliseconds >= _settings.HoverDelayMs) Expand();
            }
            return;
        }

        // Expanded: close once the pointer has left for a moment.
        var keep = new Rect(island.Left - 14, -2, island.Width + 28, island.Bottom + 16);
        if (keep.Contains(p))
        {
            _enteredSinceOpen = true;
            _leaveStart = null;
            return;
        }

        var dragging = (now - _lastDragOver).TotalMilliseconds < 350;
        if (_menusOpen > 0 || dragging || (leftDown && _pressInside)) return;

        if (_enteredSinceOpen)
        {
            _leaveStart ??= now;
            if ((now - _leaveStart.Value).TotalMilliseconds >= _settings.CloseDelayMs) Collapse();
        }
        else if (!_openedByPointer && (now - _openedAt).TotalSeconds > 5)
        {
            Collapse();
        }
    }

    /// <summary>OLED care: fade out while nothing is happening; anything at all brings it back.</summary>
    private void UpdatePresence(DateTime now, bool pointerNear)
    {
        var busy = pointerNear || State != IslandState.Collapsed || _hub.MediaActive || _hub.PillShowsDownload;
        if (busy) _lastActivity = now;
        var dormant = _settings.HideWhenIdle && (now - _lastActivity).TotalSeconds >= _settings.IdleHideSeconds;
        var target = dormant ? 0 : 1;
        if (_presence.Target == target) return;
        _presence.Target = target;
        StartAnimating();
    }

    /// <summary>OLED care: move a few pixels so the island's edges don't burn into one spot.</summary>
    private void Drift()
    {
        double x = 0, y = 0;
        if (_settings.PixelShift)
        {
            x = _random.Next(-8, 9);
            // Attached islands have to stay touching the top edge.
            y = _settings.Style == IslandStyle.Floating ? _random.Next(-3, 4) : 0;
        }
        var ease = new System.Windows.Media.Animation.SineEase();
        _drift.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(x, TimeSpan.FromSeconds(3)) { EasingFunction = ease });
        _drift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(y, TimeSpan.FromSeconds(3)) { EasingFunction = ease });
    }

    private void CheckFullscreen()
    {
        var hide = _settings.HideInFullscreen &&
                   FullscreenDetector.IsFullscreenOn(Screen, App.Current.IslandHandles());
        if (hide == _hiddenForFullscreen) return;
        _hiddenForFullscreen = hide;
        Root.IsHitTestVisible = !hide;
        if (hide) Collapse();
        StartAnimating();
    }

    // ================================================================ input handlers

    private void OnIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (State == IslandState.Expanded) return;
        Expand();
        e.Handled = true;
    }

    private void OnIslandWheel(object sender, MouseWheelEventArgs e)
    {
        if (State == IslandState.Expanded || !_settings.ScrollChangesVolume) return;
        _hub.NudgeVolume(e.Delta);
        e.Handled = true;
    }

    private void OnMediaWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_settings.ScrollChangesVolume) return;
        _hub.NudgeVolume(e.Delta);
        e.Handled = true;
    }

    private void OnShelfWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(ShelfList) is { } sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => _hub.PlayPause();
    private void OnNext(object sender, RoutedEventArgs e) => _hub.Next();
    private void OnPrevious(object sender, RoutedEventArgs e) => _hub.Previous();
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        App.Current.OpenSettings();
    }

    private void OnSeek(object sender, MouseButtonEventArgs e)
    {
        if (!_hub.CanSeek || SeekBar.ActualWidth <= 0) return;
        _hub.SeekTo(e.GetPosition(SeekBar).X / SeekBar.ActualWidth);
    }

    // ================================================================ tabs

    private bool _selectingTab;

    private void SelectTab(IslandTab tab)
    {
        if (!_settings.ShelfEnabled) tab = IslandTab.Home;
        _selectingTab = true;
        HomeTab.IsChecked = tab == IslandTab.Home;
        ShelfTab.IsChecked = tab == IslandTab.Shelf;
        _selectingTab = false;
        HomeView.Visibility = tab == IslandTab.Home ? Visibility.Visible : Visibility.Collapsed;
        ShelfView.Visibility = tab == IslandTab.Shelf ? Visibility.Visible : Visibility.Collapsed;
        ShelfTab.Visibility = _settings.ShelfEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (_selectingTab) return;
        var tab = sender == ShelfTab ? IslandTab.Shelf : IslandTab.Home;
        SelectTab(tab);
        if (_settings.RememberLastTab) _settings.LastTab = tab;
    }

    // ================================================================ shelf: drop in

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _lastDragOver = DateTime.Now;
        if (!_settings.ShelfEnabled) return;
        if (State != IslandState.Expanded) Expand(IslandTab.Shelf);
        else SelectTab(IslandTab.Shelf);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _lastDragOver = DateTime.Now;
        var ok = _settings.ShelfEnabled &&
                 (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText));
        e.Effects = ok ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        DropZone.BorderBrush = ok ? _hub.AccentBrush : (Brush)new BrushConverter().ConvertFrom("#40FFFFFF")!;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _lastDragOver = DateTime.MinValue;
        DropZone.BorderBrush = (Brush)new BrushConverter().ConvertFrom("#40FFFFFF")!;
        if (!_settings.ShelfEnabled || _draggingOut) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _hub.Shelf.AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text)) _hub.Shelf.AddText(text);
        _enteredSinceOpen = true;
        e.Handled = true;
    }

    // ================================================================ shelf: drag out & tile actions

    private Point _tileDownAt;
    private ShelfItem? _tileDown;
    private bool _draggingOut;

    private static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

    private void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        _tileDown = ItemOf(sender);
        _tileDownAt = e.GetPosition(this);
    }

    private void OnTileMove(object sender, MouseEventArgs e)
    {
        if (_tileDown == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _tileDownAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _tileDown;
        _tileDown = null;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        _draggingOut = true;
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        finally { _draggingOut = false; }
        if (result != DragDropEffects.None && _settings.RemoveAfterDragOut) _hub.Shelf.Remove(item);
    }

    private void OnTileUp(object sender, MouseButtonEventArgs e)
    {
        if (_tileDown != null && e.ClickCount == 1 && ItemOf(sender) == _tileDown) ShelfService.Open(_tileDown);
        _tileDown = null;
    }

    private static ShelfItem? MenuItemOf(object sender) => (sender as MenuItem)?.DataContext as ShelfItem;

    private void OnTileOpen(object sender, RoutedEventArgs e) { if (MenuItemOf(sender) is { } i) ShelfService.Open(i); }
    private void OnTileReveal(object sender, RoutedEventArgs e) { if (MenuItemOf(sender) is { } i) ShelfService.Reveal(i); }
    private void OnTileRemove(object sender, RoutedEventArgs e) { if (MenuItemOf(sender) is { } i) _hub.Shelf.Remove(i); }

    private void OnTileCopyPath(object sender, RoutedEventArgs e)
    {
        if (MenuItemOf(sender) is not { } i) return;
        try { Clipboard.SetText(i.Path); } catch (Exception ex) { Log.Error("shelf.copy", ex); }
    }

    private void OnTileShare(object sender, RoutedEventArgs e)
    {
        if (MenuItemOf(sender) is { } i) ShareSheet.ShareFiles(_hwnd, new[] { i.Path });
    }

    private void OnShareAll(object sender, RoutedEventArgs e) =>
        ShareSheet.ShareFiles(_hwnd, _hub.Shelf.Items.Select(i => i.Path).ToList());

    private void OnClearShelf(object sender, RoutedEventArgs e) => _hub.Shelf.Clear();

    // ================================================================ reacting to state

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Hub.MediaActive) && State != IslandState.Expanded) UpdateTargets();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CollapsedWidth):
            case nameof(AppSettings.CollapsedHeight):
            case nameof(AppSettings.Style):
            case nameof(AppSettings.ShowShadow):
                ApplyLayoutSettings();
                UpdateTargets();
                break;
            case nameof(AppSettings.ShelfEnabled):
                SelectTab(_settings.LastTab);
                break;
            case nameof(AppSettings.HideFromCapture):
                ApplyCaptureAffinity();
                break;
            case nameof(AppSettings.PixelShift):
                Drift();
                break;
            case nameof(AppSettings.HideWhenIdle):
                _lastActivity = DateTime.Now;
                break;
        }
    }

    private void ApplyLayoutSettings()
    {
        var ch = _settings.CollapsedHeight;
        CollapsedLayer.Height = ch;
        PeekSpacer.Height = new GridLength(ch);
        PillTitle.FontSize = ch >= 34 ? 13 : 12.5;
    }
}
