using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Fm350Sms;

/// <summary>Shows Windows toast notifications from this unpackaged desktop app.</summary>
internal static class Notifier
{
    private const string AppId = "FM350.Sms";
    private const string ProtocolKey = @"Software\Classes\" + LaunchArgs.UriScheme;

    /// <summary>
    /// Registers the toast AppUserModelId and the <c>fm350sms:</c> protocol (opened when a toast is clicked)
    /// for this exe, under HKCU.
    /// </summary>
    public static void Register()
    {
        // Unpackaged apps can raise toasts once an AppUserModelId is registered under HKCU.
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
        {
            key.SetValue("DisplayName", "FM350 SMS");
            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
            // Icon shown on notifications and in Settings → Notifications (a file is required; written next to the data).
            try
            {
                key.SetValue("IconUri", AppIcon.WritePng(Path.Combine(Paths.DataDirectory, "app.png")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Could not write the notification icon: " + ex.Message);
            }
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");
        using (RegistryKey protocol = Registry.CurrentUser.CreateSubKey(ProtocolKey))
        {
            protocol.SetValue(string.Empty, "URL:FM350 SMS");
            protocol.SetValue("URL Protocol", string.Empty);
        }

        using RegistryKey command = Registry.CurrentUser.CreateSubKey(ProtocolKey + @"\shell\open\command");
        // --uri: everything after it is one untrusted URI that can only open a thread (see LaunchArgs).
        command.SetValue(string.Empty, $"\"{exe}\" {LaunchArgs.UriSwitch} \"%1\"");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + AppId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
    }

    /// <param name="title">Sender as shown in the app (contact name or formatted number).</param>
    /// <param name="launch">Protocol URI opened when the toast is clicked, e.g. <c>fm350sms:thread/c:10</c>.</param>
    public static void Show(string title, string text, DateTimeOffset? timestamp, string launch)
    {
        string time = timestamp?.ToLocalTime().ToString("HH:mm") ?? string.Empty;
        string xml =
            $"<toast activationType=\"protocol\" launch=\"{SecurityElement.Escape(launch)}\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text hint-maxLines=\"1\">{SecurityElement.Escape(title)}</text>" +
            $"<text>{SecurityElement.Escape(text)}</text>" +
            $"<text placement=\"attribution\">SMS {SecurityElement.Escape(time)}</text>" +
            "</binding></visual>" +
            "<audio src=\"ms-winsoundevent:Notification.SMS\"/>" +
            "</toast>";

        var document = new XmlDocument();
        document.LoadXml(xml);
        ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(document));
    }
}
