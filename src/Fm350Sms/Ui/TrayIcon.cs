using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Fm350Sms;

/// <summary>Notification-area icon with the menu פתח / ייבוא אנשי קשר / יציאה. Create and use on the UI thread.</summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly IntPtr _hIcon;
    private readonly Icon _image;

    public TrayIcon(Action open, Action importContacts, Action exit)
    {
        _hIcon = DrawIcon();
        _image = Icon.FromHandle(_hIcon);
        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        menu.Items.Add("פתח", null, (_, _) => open());
        menu.Items.Add("ייבוא אנשי קשר", null, (_, _) => importContacts());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("יציאה", null, (_, _) => exit());
        ((Forms.ToolStripMenuItem)menu.Items[0]).Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "FM350 SMS", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    /// <summary>The same picture as the tray icon, for the window's title bar.</summary>
    public Icon Image => _image;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
        DestroyIcon(_hIcon); // Icon.FromHandle does not own the handle
    }

    /// <summary>A blue speech bubble, drawn so the app ships without an icon file.</summary>
    private static IntPtr DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bubble = new GraphicsPath();
            bubble.AddArc(2, 3, 10, 10, 180, 90);
            bubble.AddArc(20, 3, 10, 10, 270, 90);
            bubble.AddArc(20, 15, 10, 10, 0, 90);
            bubble.AddLine(25, 25, 12, 25);
            bubble.AddLine(12, 25, 6, 30);
            bubble.AddLine(6, 30, 7, 25);
            bubble.AddArc(2, 15, 10, 10, 90, 90);
            bubble.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0, 103, 192));
            g.FillPath(fill, bubble);
            using var dot = new SolidBrush(Color.White);
            foreach (int x in new[] { 9, 15, 21 }) g.FillEllipse(dot, x - 1, 12, 4, 4);
        }

        return bitmap.GetHicon();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
