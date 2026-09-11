using Microsoft.Extensions.Logging;
using Windows.Networking.NetworkOperators;

namespace Fm350Unlock.Service;

internal sealed class UnlockCoordinator(
    MbimFccClient mbim,
    ServiceLog serviceLog,
    ILogger<UnlockCoordinator> logger)
{
    private string? _lastState;
    private string? _lastUnlockedDeviceId;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;
    private DateTimeOffset _genericPersonaSeen = DateTimeOffset.MinValue;

    public async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        ModemSnapshot snapshot = await mbim.GetSnapshotAsync(cancellationToken);
        bool genericPersona = snapshot.Availability == ModemAvailability.GenericPersona;
        string state = snapshot.Availability == ModemAvailability.Present
            ? $"dell-radio:{snapshot.RadioState}"
            : genericPersona
                ? $"generic-radio:{snapshot.RadioState}"
                : snapshot.Availability.ToString();
        LogStateChange(state);

        if (snapshot.Modem is null || snapshot.RadioState is null)
        {
            _genericPersonaSeen = DateTimeOffset.MinValue;
            return TimeSpan.FromSeconds(2);
        }

        if (genericPersona)
        {
            if (_genericPersonaSeen == DateTimeOffset.MinValue)
            {
                _genericPersonaSeen = DateTimeOffset.UtcNow;
            }

            if (DateTimeOffset.UtcNow - _genericPersonaSeen < TimeSpan.FromSeconds(3))
            {
                return TimeSpan.FromMilliseconds(500);
            }
        }
        else
        {
            _genericPersonaSeen = DateTimeOffset.MinValue;
        }

        if (snapshot.RadioState == MobileBroadbandRadioState.On)
        {
            return TimeSpan.FromSeconds(5);
        }

        if (snapshot.RadioState != MobileBroadbandRadioState.Off)
        {
            return TimeSpan.FromSeconds(2);
        }

        string deviceId = snapshot.Modem.DeviceInformation.DeviceId;
        if (string.Equals(_lastUnlockedDeviceId, deviceId, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _lastSuccess < TimeSpan.FromSeconds(15))
        {
            return TimeSpan.FromSeconds(2);
        }

        if (DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromSeconds(4))
        {
            return TimeSpan.FromSeconds(1);
        }

        _lastAttempt = DateTimeOffset.UtcNow;
        ModemSnapshot unlockSnapshot = genericPersona
            ? snapshot
            : mbim.GetDellDefaultSnapshot();
        if (unlockSnapshot.Modem is null ||
            unlockSnapshot.RadioState != MobileBroadbandRadioState.Off)
        {
            return TimeSpan.FromSeconds(2);
        }

        try
        {
            MbimUnlockResult? result = await mbim.UnlockAsync(
                unlockSnapshot.Modem,
                cancellationToken,
                skipInitialQuery: genericPersona);
            if (result is null)
            {
                const string unavailable =
                    "FCC MBIM device service or command 1 is unavailable.";
                logger.LogWarning("{Message}", unavailable);
                serviceLog.Write(unavailable);
                return TimeSpan.FromSeconds(4);
            }

            string evidence =
                $"challenge={result.Challenge}; " +
                $"query=0x{result.QueryStatus:X8}/{result.QueryResponse}; " +
                $"challengeSet=0x{result.ChallengeStatus:X8}/{result.ChallengeResponse}; " +
                $"verifySet=0x{result.VerifyStatus:X8}/{result.VerifyResponse}; " +
                $"postVerify=0x{result.PostVerifyStatus:X8}/{result.PostVerifyResponse}";
            if (result.Accepted)
            {
                MarkSuccess(deviceId);
                string sequence = genericPersona ? "Set-first generic" : "Query-first Dell";
                string message = $"FCC MBIM unlock succeeded ({sequence}); {evidence}.";
                logger.LogInformation("{Message}", message);
                serviceLog.Write(message);
                return TimeSpan.FromSeconds(2);
            }

            string rejected = $"FCC MBIM unlock was not confirmed; {evidence}.";
            logger.LogWarning("{Message}", rejected);
            serviceLog.Write(rejected);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FCC MBIM unlock attempt failed.");
            serviceLog.Write($"FCC MBIM unlock attempt failed: {exception.Message}");
        }

        return TimeSpan.FromSeconds(4);
    }

    private void MarkSuccess(string deviceId)
    {
        _lastUnlockedDeviceId = deviceId;
        _lastSuccess = DateTimeOffset.UtcNow;
    }

    private void LogStateChange(string state)
    {
        if (string.Equals(_lastState, state, StringComparison.Ordinal))
        {
            return;
        }

        _lastState = state;
        logger.LogInformation("Modem state changed to {State}.", state);
        serviceLog.Write($"Modem state changed to {state}.");
    }
}
