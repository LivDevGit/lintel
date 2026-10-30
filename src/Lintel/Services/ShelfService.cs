using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lintel.Core;
using Lintel.Native;

namespace Lintel.Services;

public sealed class ShelfItem
{
    public ShelfItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(Name)) Name = path;
        Icon = ShelfIcons.For(path);
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public bool IsSnippet => Path.StartsWith(ShelfService.SnippetFolder, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The shelf keeps references to files you parked on the island. Dropped text and
/// links are saved as small files so they can be dragged out like anything else.
/// </summary>
public sealed class ShelfService
{
    public static readonly string SnippetFolder = System.IO.Path.Combine(SettingsStore.Folder, "Snippets");
    private static string StoreFile => System.IO.Path.Combine(SettingsStore.Folder, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = new();

    /// <summary>Off in demo mode, so sample items never overwrite the real shelf.</summary>
    public bool Persist { get; set; } = true;

    public void Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StoreFile)) ?? new();
            foreach (var p in paths.Where(Exists)) Items.Add(new ShelfItem(p));
        }
        catch (Exception ex)
        {
            Log.Error("shelf.load", ex);
        }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!Exists(p) || Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new ShelfItem(p));
        }
        Save();
    }

    public void AddText(string text)
    {
        try
        {
            Directory.CreateDirectory(SnippetFolder);
            var trimmed = text.Trim();
            var isLink = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string file;
            if (isLink)
            {
                file = System.IO.Path.Combine(SnippetFolder, $"{SafeName(uri!.Host)}-{stamp}.url");
                File.WriteAllText(file, $"[InternetShortcut]\r\nURL={trimmed}\r\n");
            }
            else
            {
                file = System.IO.Path.Combine(SnippetFolder, $"Note-{stamp}.txt");
                File.WriteAllText(file, text);
            }
            AddPaths(new[] { file });
        }
        catch (Exception ex)
        {
            Log.Error("shelf.text", ex);
        }
    }

    private static string SafeName(string s) => string.Concat(s.Where(ch => !System.IO.Path.GetInvalidFileNameChars().Contains(ch)));

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        if (item.IsSnippet && Persist)
        {
            try { File.Delete(item.Path); } catch { }
        }
        Save();
    }

    public void Clear()
    {
        foreach (var item in Items.ToList()) Remove(item);
    }

    public static void Open(ShelfItem item)
    {
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("shelf.open", ex); }
    }

    public static void Reveal(ShelfItem item)
    {
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); }
        catch (Exception ex) { Log.Error("shelf.reveal", ex); }
    }

    private void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(Items.Select(i => i.Path).ToList()));
        }
        catch (Exception ex)
        {
            Log.Error("shelf.save", ex);
        }
    }
}

internal static class ShelfIcons
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    public static ImageSource? For(string path)
    {
        if (ImageTypes.Contains(Path.GetExtension(path)))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // Fall through to the shell icon.
            }
        }

        var info = new Win32.SHFILEINFO();
        var res = Win32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            Win32.DestroyIcon(info.hIcon);
        }
    }
}
