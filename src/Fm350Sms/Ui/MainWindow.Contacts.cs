using System.Text;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Fm350Sms;

/// <summary>Contact actions of the window: import, export, save/rename a thread's contact, new contact.</summary>
internal partial class MainWindow
{
    private static string DownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>ייבוא אנשי קשר: picks a Google CSV or vCard file, replaces the imported contacts and shows a summary.</summary>
    public void ImportContacts()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ייבוא אנשי קשר",
            Filter = "אנשי קשר (*.csv;*.vcf)|*.csv;*.vcf",
            InitialDirectory = ContactEdits.ImportFolder(_settings.ContactsFolder, DownloadsFolder),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IReadOnlyList<ImportedContact> imported = ContactFiles.Read(dialog.FileName);
            if (ContactEdits.ImportRefusal(imported) is { } refusal)
            {
                Log.Write("Contacts import refused: no contact with a phone number in the file.");
                Info("ייבוא אנשי קשר", refusal, MessageBoxImage.Error);
                return;
            }

            _db.ReplaceGoogleContacts(imported);
            Reload();
            Log.Write($"Imported {imported.Count} contact(s).");
            Info("ייבוא אנשי קשר", ContactEdits.ImportSummary(imported, _messages, _contacts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SqliteException)
        {
            Log.Write("Contacts import failed: " + ex.Message);
            Info("ייבוא אנשי קשר", "הייבוא נכשל: " + ex.Message, MessageBoxImage.Error);
        }
    }

    private void OnImportContacts(object sender, RoutedEventArgs e) => ImportContacts();

    /// <summary>ייצוא אנשי קשר: writes the contacts saved in the app as a Google CSV file.</summary>
    private void OnExportContacts(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ContactRow> manual = ContactEdits.ForExport(_db.Contacts());
        if (manual.Count == 0)
        {
            Info("ייצוא אנשי קשר", "אין אנשי קשר שנשמרו באפליקציה.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "ייצוא אנשי קשר",
            Filter = "Google CSV (*.csv)|*.csv",
            FileName = "fm350-contacts.csv",
            InitialDirectory = DownloadsFolder,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, ContactFiles.WriteGoogleCsv(manual), new UTF8Encoding(false));
            Log.Write($"Exported {manual.Count} contact(s).");
            Info("ייצוא אנשי קשר", $"נשמרו {manual.Count} אנשי קשר.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Contacts export failed: " + ex.Message);
            Info("ייצוא אנשי קשר", "הייצוא נכשל: " + ex.Message, MessageBoxImage.Error);
        }
    }

    /// <summary>איש קשר חדש: a manual contact typed by hand.</summary>
    private void OnNewContact(object sender, RoutedEventArgs e)
    {
        var dialog = new ContactDialog(this, "איש קשר חדש", string.Empty, []);
        if (dialog.ShowDialog() != true) return;
        _db.SaveManualContact(dialog.ContactName, dialog.NumberKeys);
        Reload();
    }

    /// <summary>
    /// שמור איש קשר / שנה שם on the shown conversation: a manual contact holding all its numbers, so the name overrides
    /// an imported one and survives re-imports.
    /// </summary>
    private void OnHeaderAction(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } thread) return;
        RenamePlan plan = ContactEdits.ForThread(thread, _contacts);
        string title = thread.IsSavedContact ? "שנה שם" : "שמור איש קשר";
        string name = thread.IsSavedContact ? thread.Title : string.Empty;
        var dialog = new ContactDialog(this, title, name, plan.NumberKeys);
        if (dialog.ShowDialog() != true) return;

        IReadOnlyList<string> keys = dialog.NumberKeys;
        if (plan.ManualContactId is { } id)
        {
            _db.UpdateManualContact(id, dialog.ContactName, keys);
        }
        else
        {
            _db.SaveManualContact(dialog.ContactName, keys);
        }

        // The conversation now lives under the contact: follow it.
        string? anchor = _draftKey ?? thread.NumberKeys.FirstOrDefault();
        IReadOnlyList<ContactRow> contacts = _db.Contacts();
        if (anchor is not null && _draftKey is null)
        {
            _selectedId = Threads.IdFor(anchor, contacts);
            _drafts.Rename(thread.Id, _selectedId); // the compose text follows the conversation
        }

        Reload();
    }
}
