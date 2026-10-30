using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;
using Lintel.Core;
using Lintel.Native;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Forms = System.Windows.Forms;

namespace Lintel.Services;

/// <summary>One system-wide shortcut, e.g. "Ctrl+Alt+Space".</summary>
public sealed class HotkeyService : IDisposable
{
    private const int Id = 0x4C49; // "LI"
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("LintelHotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public bool Register(string gesture)
    {
        Unregister();
        if (!TryParse(gesture, out var mods, out var vk)) return false;
        _registered = Win32.RegisterHotKey(_source.Handle, Id, mods | Win32.MOD_NOREPEAT, vk);
        if (!_registered) Log.Write($"[hotkey] could not register {gesture} (already in use?)");
        return _registered;
    }

    private void Unregister()
    {
        if (_registered) Win32.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        Key? key = null;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": case "windows": modifiers |= Win32.MOD_WIN; break;
                default:
                    if (Enum.TryParse<Key>(raw, true, out var k)) key = k;
                    else return false;
                    break;
            }
        }
        if (key == null || modifiers == 0) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Lintel";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
        }
    }
}

/// <summary>Opens the Windows 11 share sheet for files, from a desktop (non-packaged) app.</summary>
public static class ShareSheet
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static void ShareFiles(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            var ptr = interop.GetForWindow(hwnd, ref iid);
            var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);

            void OnRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                sender.DataRequested -= OnRequested;
                var deferral = args.Request.GetDeferral();
                _ = FillAsync(args.Request, paths).ContinueWith(_ => deferral.Complete(), TaskScheduler.Default);
            }

            manager.DataRequested += OnRequested;
            interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("share", ex);
        }
    }

    private static async Task FillAsync(DataRequest request, IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                if (System.IO.Directory.Exists(p)) items.Add(await StorageFolder.GetFolderFromPathAsync(p));
                else items.Add(await StorageFile.GetFileFromPathAsync(p));
            }
            catch (Exception ex)
            {
                Log.Error("share.item", ex);
            }
        }
        request.Data.Properties.Title = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        request.Data.SetStorageItems(items);
    }
}

public static class FullscreenDetector
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>True when the foreground window covers the whole of <paramref name="screen"/>.</summary>
    public static bool IsFullscreenOn(Forms.Screen screen, IEnumerable<IntPtr> ownWindows)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || ownWindows.Contains(fg)) return false;
        if (ShellClasses.Contains(Win32.ClassNameOf(fg))) return false;
        // A maximized window can cover the whole display when the taskbar auto-hides; that isn't fullscreen.
        if (Win32.IsZoomed(fg)) return false;
        if (!Win32.GetWindowRect(fg, out var r)) return false;
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}

public static class SystemAccent
{
    public static System.Windows.Media.Color Get()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
            return System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF);
        }
    }
}
