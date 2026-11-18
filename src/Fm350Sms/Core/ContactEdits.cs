namespace Fm350Sms;

/// <summary>What saving or renaming a thread writes: update this manual contact (null = create one) with these numbers.</summary>
internal sealed record RenamePlan(long? ManualContactId, IReadOnlyList<string> NumberKeys);

/// <summary>Pure rules behind the contact actions of the window: save/rename, import summary, export, import folder.</summary>
internal static class ContactEdits
{
    /// <summary>
    /// Save/rename of a thread: a manual contact holding every number of the thread (and of the contact it shows), so the
    /// name overrides an imported one and survives re-imports. An existing manual contact is updated rather than duplicated.
    /// </summary>
    internal static RenamePlan ForThread(ThreadInfo thread, IReadOnlyList<ContactRow> contacts)
    {
        ContactRow? contact = thread.ContactId is { } id ? contacts.FirstOrDefault(c => c.Id == id) : null;
        List<string> keys = thread.NumberKeys.Concat(contact?.NumberKeys ?? []).Distinct().ToList();
        return new RenamePlan(contact?.Origin == "manual" ? contact.Id : null, keys);
    }

    /// <summary>Numbers typed one per line (commas and semicolons also separate), as keys, without duplicates.</summary>
    internal static IReadOnlyList<string> ParseNumbers(string text) =>
        text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PhoneNumbers.Key)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>"נקראו X אנשי קשר, Y מספרים, Z שיחות זוהו": contacts and numbers read, conversations now shown under a contact.</summary>
    internal static string ImportSummary(IReadOnlyList<ImportedContact> imported, IReadOnlyList<MessageRow> messages, IReadOnlyList<ContactRow> contacts)
    {
        int numbers = imported.Sum(c => c.NumberKeys.Count);
        int matched = Threads.Build(messages, contacts).Count(t => t.IsSavedContact);
        return $"נקראו {imported.Count} אנשי קשר, {numbers} מספרים, {matched} שיחות זוהו";
    }

    internal const string NoContactsInFile = "לא נמצאו אנשי קשר עם מספר טלפון בקובץ. אנשי הקשר הקיימים לא שונו.";

    /// <summary>
    /// Why an import must not replace the imported contacts, or null when it may: a file with no contact that has a
    /// phone number (a wrong or empty export) would otherwise wipe them all.
    /// </summary>
    internal static string? ImportRefusal(IReadOnlyList<ImportedContact> imported) =>
        imported.Any(c => c.NumberKeys.Count > 0) ? null : NoContactsInFile;

    /// <summary>Export writes only the contacts saved in the app.</summary>
    internal static IReadOnlyList<ContactRow> ForExport(IReadOnlyList<ContactRow> contacts) =>
        contacts.Where(c => c.Origin == "manual").ToList();

    /// <summary>The import dialog opens in the configured folder when it exists, else in <paramref name="fallback"/>.</summary>
    internal static string ImportFolder(string? configured, string fallback) =>
        !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) ? configured : fallback;
}
