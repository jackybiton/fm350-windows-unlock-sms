namespace Fm350Sms;

/// <summary>
/// Parsed command line. A <see cref="Command"/> (or an unknown argument) runs as a console command;
/// everything else is the app: <c>--watch</c> (what the v0.1.0 installer writes at logon) is the app, started minimized.
/// </summary>
/// <remarks>
/// A <c>fm350sms:</c> URI comes from outside (the protocol handler registers <c>"&lt;exe&gt;" --uri "%1"</c>) and a launcher
/// that does not percent-encode can split a URI holding quotes into several arguments. So everything after <c>--uri</c>
/// is one untrusted URI, and any launch with a URI ignores all other arguments: a URI can only open a thread, never run
/// a command or turn on deletion.
/// </remarks>
internal sealed record LaunchArgs(
    string? Command, string? CommandArgument, bool Minimized, bool DeleteAfterSave, bool OpenRequested, string? ThreadId,
    string? Unknown)
{
    internal const string UriScheme = "fm350sms";

    /// <summary>Marks the rest of the command line as the protocol URI.</summary>
    internal const string UriSwitch = "--uri";

    private static readonly string[] Commands =
        ["--self-test", "--list", "--register", "--unregister", "--test-notification", "--where", "--merge-db"];

    /// <summary>Runs in the console, never starts the window.</summary>
    internal bool IsConsole => Command is not null || Unknown is not null;

    internal static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        int uriSwitch = args.ToList().IndexOf(UriSwitch);
        if (uriSwitch >= 0) return FromUri(string.Join(' ', args.Skip(uriSwitch + 1)));
        if (args.FirstOrDefault(a => a.StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase)) is { } uri)
        {
            return FromUri(uri);
        }

        string? command = null, commandArgument = null, unknown = null;
        bool minimized = false, deleteAfterSave = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (Commands.Contains(a))
            {
                command ??= a;
                if (a == "--merge-db" && i + 1 < args.Count) commandArgument = args[++i];
            }
            else if (a == "--minimized") minimized = true;
            else if (a == "--delete-after-save") deleteAfterSave = true;
            else if (a == "--watch") minimized = true; // v0.1.0 logon switch: now the app, in the tray
            else unknown ??= a;
        }

        return new LaunchArgs(command, commandArgument, minimized, deleteAfterSave, OpenRequested: false, ThreadId: null, unknown);
    }

    /// <summary>A protocol launch: shows the window, on the URI's thread when it names one. Nothing else.</summary>
    private static LaunchArgs FromUri(string uri) =>
        new(null, null, Minimized: false, DeleteAfterSave: false, OpenRequested: true, ThreadIdFromUri(uri.Trim()), null);

    /// <summary>
    /// The thread id in <c>fm350sms:thread/&lt;id&gt;</c> (percent-escapes decoded, a trailing slash that the shell may add
    /// removed), or null for any other <c>fm350sms:</c> URI.
    /// </summary>
    internal static string? ThreadIdFromUri(string uri)
    {
        const string prefix = UriScheme + ":thread/";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string id = uri[prefix.Length..].TrimEnd('/');
        try
        {
            id = Uri.UnescapeDataString(id);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return id.Length == 0 ? null : id;
    }
}
