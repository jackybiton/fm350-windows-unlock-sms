namespace Fm350Sms;

internal sealed record ImportedContact(string Name, IReadOnlyList<string> NumberKeys);
