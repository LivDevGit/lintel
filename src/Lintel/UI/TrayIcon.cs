using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Lintel.UI;

/// <summary>Notification-area icon with the app menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action openIsland, Action openSettings, Action quit)
    {
        _image = DrawIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open island", null, (_, _) => openIsland());
        menu.Items.Add("Settings", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Lintel", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Lintel",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) openIsland(); };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    /// <summary>A lintel: a bar resting on two posts.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.White);
            using var bar = RoundedRect(new RectangleF(3, 6, 26, 8), 4);
            g.FillPath(brush, bar);
            g.FillRectangle(brush, 7, 14, 4, 13);
            g.FillRectangle(brush, 21, 14, 4, 13);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
