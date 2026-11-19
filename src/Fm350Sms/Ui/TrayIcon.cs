using System.Drawing;
using Forms = System.Windows.Forms;

namespace Fm350Sms;

/// <summary>Notification-area icon with the menu פתח / ייבוא אנשי קשר / יציאה. Create and use on the UI thread.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _trayImage;
    private readonly Icon _windowImage;

    public TrayIcon(Action open, Action importContacts, Action exit)
    {
        // The tray wants the small-icon size for the current DPI (16 px at 100 %, 24 at 150 %, ...).
        _trayImage = AppIcon.Load(Forms.SystemInformation.SmallIconSize.Width);
        _windowImage = AppIcon.Load(32);
        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        menu.Items.Add("פתח", null, (_, _) => open());
        menu.Items.Add("ייבוא אנשי קשר", null, (_, _) => importContacts());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("יציאה", null, (_, _) => exit());
        ((Forms.ToolStripMenuItem)menu.Items[0]).Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);

        _icon = new Forms.NotifyIcon { Icon = _trayImage, Text = "FM350 SMS", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    /// <summary>The app icon at title-bar size, for the window.</summary>
    public Icon Image => _windowImage;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _trayImage.Dispose();
        _windowImage.Dispose();
    }
}
