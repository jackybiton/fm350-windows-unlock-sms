using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Fm350Unlock.Service;

internal sealed partial class AtFccUnlockTransport(ILogger<AtFccUnlockTransport> logger)
{
    public Task<AtUnlockResult> UnlockAsync(AtPort port, CancellationToken cancellationToken) =>
        Task.Run(() => Unlock(port, cancellationToken), cancellationToken);

    private AtUnlockResult Unlock(AtPort port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string challengeReply = SendCommand(port.Name, "AT+GTFCCLOCKGEN", cancellationToken);
        Match challengeMatch = ChallengeRegex().Match(challengeReply);
        if (!challengeMatch.Success)
        {
            throw new InvalidOperationException("The modem did not return an FCC challenge.");
        }

        string challenge = challengeMatch.Groups[1].Value.ToLowerInvariant();
        uint response = FccUnlockAlgorithm.CreateResponse(challenge);
        string verifyReply = SendCommand(
            port.Name,
            $"AT+GTFCCLOCKVER={response}",
            cancellationToken);
        bool accepted = VerifyRegex().IsMatch(verifyReply);

        logger.LogInformation(
            "AT FCC authentication on {Port}: challenge={Challenge}; accepted={Accepted}.",
            port.Name,
            challenge,
            accepted);
        return new AtUnlockResult(accepted, challenge);
    }

    private static string SendCommand(
        string portName,
        string command,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = NativeMethods.CreateFile(
            $@"\.\{portName}",
            NativeMethods.GenericReadWrite,
            0,
            0,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {portName}");
        }

        using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        byte[] commandBytes = Encoding.ASCII.GetBytes(command + "\r");
        stream.Write(commandBytes);
        stream.Flush();
        cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(800));
        cancellationToken.ThrowIfCancellationRequested();

        byte[] buffer = new byte[8192];
        IAsyncResult read = stream.BeginRead(buffer, 0, buffer.Length, null, null);
        int signaled = WaitHandle.WaitAny(
            [read.AsyncWaitHandle, cancellationToken.WaitHandle],
            TimeSpan.FromSeconds(5));
        if (signaled == WaitHandle.WaitTimeout)
        {
            throw new TimeoutException($"Timed out waiting for {command} on {portName}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        int count = stream.EndRead(read);
        return Encoding.ASCII.GetString(buffer, 0, count);
    }

    [GeneratedRegex(@"0x([0-9A-Fa-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengeRegex();

    [GeneratedRegex(@"\+GTFCCLOCKVER:\s*1", RegexOptions.CultureInvariant)]
    private static partial Regex VerifyRegex();
}

internal sealed record AtUnlockResult(bool Accepted, string Challenge);
