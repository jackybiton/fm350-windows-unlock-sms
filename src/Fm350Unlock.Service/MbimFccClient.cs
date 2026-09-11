using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Networking.NetworkOperators;
using Windows.Storage.Streams;

namespace Fm350Unlock.Service;

internal sealed class MbimFccClient(ILogger<MbimFccClient> logger)
{
    private const string Fm350PciId = "VEN_14C3&DEV_4D75";
    private const string DellRuntimeSubsystemId = "SUBSYS_59311028";

    internal static readonly Guid FccDeviceServiceId =
        new("F85D46EF-AB26-4081-9868-4D183C0A3AEC");

    private MobileBroadbandModem? _cachedModem;
    private string? _cachedDeviceId;
    private ModemAvailability _cachedAvailability = ModemAvailability.NotPresent;

    public ModemSnapshot GetDellDefaultSnapshot()
    {
        try
        {
            // Dell's authenticator uses GetDefault rather than enumerating and
            // reopening the interface with FromId. Keep this exact behavior
            // isolated from the production discovery path.
            MobileBroadbandModem? modem = MobileBroadbandModem.GetDefault();
            if (modem is not null && !IsDellRuntimePersona(modem.DeviceInformation.DeviceId))
            {
                return new ModemSnapshot(null, ModemAvailability.GenericPersona, null);
            }

            ModemSnapshot? snapshot = TryReadSnapshot(modem);
            if (snapshot is null ||
                modem!.GetDeviceService(FccDeviceServiceId) is not { } fccService ||
                !fccService.SupportedCommands.Contains(1u))
            {
                return new ModemSnapshot(null, ModemAvailability.NotPresent, null);
            }

            return snapshot;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Dell-compatible default modem lookup is not ready (HRESULT 0x{HResult:X8}).",
                exception.HResult);
            return new ModemSnapshot(null, ModemAvailability.NotReady, null);
        }
    }

    public async Task<ModemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        ModemSnapshot? cached = TryReadSnapshot(_cachedModem);
        if (cached is not null)
        {
            return cached with { Availability = _cachedAvailability };
        }

        _cachedModem = null;
        _cachedAvailability = ModemAvailability.NotPresent;

        try
        {
            string selector = MobileBroadbandModem.GetDeviceSelector();
            DeviceInformationCollection devices = await DeviceInformation
                .FindAllAsync(selector)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            if (devices.Count == 0)
            {
                _cachedDeviceId = null;
                return new ModemSnapshot(null, ModemAvailability.NotPresent, null);
            }

            // FromId does not depend on the service account having a per-user
            // "default" mobile-broadband interface. Prefer the previous device
            // and require the Dell FCC service so another modem is never targeted.
            IEnumerable<DeviceInformation> orderedDevices = devices.OrderByDescending(candidate =>
                string.Equals(candidate.Id, _cachedDeviceId, StringComparison.Ordinal));
            foreach (DeviceInformation device in orderedDevices)
            {
                if (!IsDellRuntimePersona(device.Id))
                {
                    if (device.Id.Contains(Fm350PciId, StringComparison.OrdinalIgnoreCase))
                    {
                        MobileBroadbandModem? genericModem = MobileBroadbandModem.FromId(device.Id);
                        ModemSnapshot? genericSnapshot = TryReadSnapshot(genericModem);
                        if (genericSnapshot is not null &&
                            genericModem!.GetDeviceService(FccDeviceServiceId) is { } genericFccService &&
                            genericFccService.SupportedCommands.Contains(1u))
                        {
                            _cachedModem = genericModem;
                            _cachedDeviceId = device.Id;
                            _cachedAvailability = ModemAvailability.GenericPersona;
                            return new ModemSnapshot(
                                genericModem,
                                ModemAvailability.GenericPersona,
                                genericSnapshot.RadioState);
                        }
                    }

                    continue;
                }

                MobileBroadbandModem? modem = MobileBroadbandModem.FromId(device.Id);
                ModemSnapshot? refreshed = TryReadSnapshot(modem);
                if (refreshed is null ||
                    modem!.GetDeviceService(FccDeviceServiceId) is not { } fccService ||
                    !fccService.SupportedCommands.Contains(1u))
                {
                    continue;
                }

                _cachedModem = modem;
                _cachedDeviceId = device.Id;
                _cachedAvailability = ModemAvailability.Present;
                return refreshed;
            }

            _cachedDeviceId = null;
            return new ModemSnapshot(null, ModemAvailability.NotPresent, null);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Mobile-broadband modem enumeration is not ready (HRESULT 0x{HResult:X8}).",
                exception.HResult);
            ModemSnapshot? defaultSnapshot = GetAnyDefaultSnapshot();
            return defaultSnapshot ??
                new ModemSnapshot(null, ModemAvailability.NotReady, null);
        }
    }

    public ModemSnapshot? GetAnyDefaultSnapshot()
    {
        try
        {
            MobileBroadbandModem? modem = MobileBroadbandModem.GetDefault();
            string? deviceId = modem?.DeviceInformation.DeviceId;
            if (modem is null ||
                deviceId is null ||
                !deviceId.Contains(Fm350PciId, StringComparison.OrdinalIgnoreCase) ||
                modem.GetDeviceService(FccDeviceServiceId) is not { } fccService ||
                !fccService.SupportedCommands.Contains(1u))
            {
                return null;
            }

            ModemSnapshot? snapshot = TryReadSnapshot(modem);
            if (snapshot is null)
            {
                return null;
            }

            ModemAvailability availability = IsDellRuntimePersona(deviceId)
                ? ModemAvailability.Present
                : ModemAvailability.GenericPersona;
            _cachedModem = modem;
            _cachedDeviceId = deviceId;
            _cachedAvailability = availability;
            return snapshot with { Availability = availability };
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Default modem fallback is not ready (HRESULT 0x{HResult:X8}).",
                exception.HResult);
            return null;
        }
    }

    private static bool IsDellRuntimePersona(string deviceId) =>
        deviceId.Contains(Fm350PciId, StringComparison.OrdinalIgnoreCase) &&
        deviceId.Contains(DellRuntimeSubsystemId, StringComparison.OrdinalIgnoreCase);

    private ModemSnapshot? TryReadSnapshot(MobileBroadbandModem? modem)
    {
        if (modem is null)
        {
            return null;
        }

        try
        {
            MobileBroadbandRadioState state = modem.DeviceInformation.CurrentRadioState;
            return new ModemSnapshot(modem, ModemAvailability.Present, state);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Cached mobile-broadband modem became invalid (HRESULT 0x{HResult:X8}); re-enumerating.",
                exception.HResult);
            return null;
        }
    }

    public async Task<MbimQueryResult?> QueryFccStateAsync(
        MobileBroadbandModem modem,
        CancellationToken cancellationToken)
    {
        MobileBroadbandDeviceService? service = modem.GetDeviceService(FccDeviceServiceId);
        if (service is null || !service.SupportedCommands.Contains(1u))
        {
            return null;
        }

        MobileBroadbandDeviceServiceCommandSession session = service.OpenCommandSession();
        // Dell releases the command-session object after the operation but does
        // not call CloseSession. Calling CloseSession here can tear down the
        // private MBIM channel before the following FCC Set command.
        //
        // The WinRT projection releases the native object when this local
        // wrapper becomes unreachable.
        //
        // Keep the logical input empty while retaining Dell's 4096-byte backing
        // capacity.
        Windows.Storage.Streams.Buffer empty = new(4096);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        MobileBroadbandDeviceServiceCommandResult result;
        try
        {
            result = await session
                .SendQueryCommandAsync(1, empty)
                .AsTask(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("FCC MBIM query command 1 timed out.");
        }

        MbimQueryResult queryResult =
            new(result.StatusCode, ReadBuffer(result.ResponseData));
        ReleaseSessionWithoutClosing(session);
        return queryResult;
    }

    public async Task<MbimUnlockResult?> UnlockAsync(
        MobileBroadbandModem modem,
        CancellationToken cancellationToken,
        bool skipInitialQuery = false)
    {
        MobileBroadbandDeviceService? service = modem.GetDeviceService(FccDeviceServiceId);
        if (service is null || !service.SupportedCommands.Contains(1u))
        {
            return null;
        }

        MbimQueryResult? queryResult = null;
        if (!skipInitialQuery)
        {
            queryResult = await QueryFccStateAsync(modem, cancellationToken);
            if (queryResult is null || queryResult.StatusCode != 0 || queryResult.Response.Length < 8)
            {
                throw new InvalidOperationException(
                    $"MBIM status query failed: status=0x{queryResult?.StatusCode ?? uint.MaxValue:X8}, " +
                    $"response={FormatResponse(queryResult?.Response ?? [])}.");
            }

            // Dell reads these as byte fields even though the observed response is
            // two little-endian DWORDs: lm=response[0], ls=response[4].
            bool lockMechanismPresent = queryResult.Response[0] != 0;
            bool alreadyUnlocked = queryResult.Response[4] != 0;
            if (!lockMechanismPresent)
            {
                throw new InvalidOperationException("The modem reports no FCC lock mechanism.");
            }

            if (alreadyUnlocked)
            {
                return new MbimUnlockResult(
                    true,
                    "<already-unlocked>",
                    queryResult.StatusCode,
                    FormatResponse(queryResult.Response),
                    0,
                    "<not-sent>",
                    0,
                    "<not-sent>",
                    queryResult.StatusCode,
                    FormatResponse(queryResult.Response));
            }
        }

        MbimCommandResult challengeResult = await SendSetAsync(
            GetDellDefaultFccService(),
            new byte[8],
            "challenge",
            cancellationToken);
        if (challengeResult.StatusCode != 0 || challengeResult.Response.Length < 8)
        {
            throw new InvalidOperationException(
                $"MBIM challenge failed: status=0x{challengeResult.StatusCode:X8}, " +
                $"response={FormatResponse(challengeResult.Response)}.");
        }

        byte[] challenge = challengeResult.Response[4..8];
        byte[] verifyRequest = CreateVerifyRequest(challenge);

        // Dell's authenticator opens a new command session for each Set.
        // Some firmware rejects verification when both exchanges share one session.
        MbimCommandResult verifyResult = await SendSetAsync(
            GetDellDefaultFccService(),
            verifyRequest,
            "verification",
            cancellationToken);
        bool acceptedByResponse = verifyResult.StatusCode == 0 &&
            verifyResult.Response.Length >= 1 &&
            verifyResult.Response[0] != 0;
        MbimQueryResult? postVerifyResult = null;
        if (verifyResult.StatusCode == 0)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                MobileBroadbandModem? defaultModem = MobileBroadbandModem.GetDefault();
                if (defaultModem is not null)
                {
                    postVerifyResult = await QueryFccStateAsync(defaultModem, cancellationToken);
                    if (postVerifyResult is not null &&
                        postVerifyResult.StatusCode == 0 &&
                        postVerifyResult.Response.Length >= 8 &&
                        postVerifyResult.Response[4] != 0)
                    {
                        break;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        bool acceptedByState = postVerifyResult is not null &&
            postVerifyResult.StatusCode == 0 &&
            postVerifyResult.Response.Length >= 8 &&
            postVerifyResult.Response[4] != 0;
        bool accepted = acceptedByResponse || acceptedByState;
        return new MbimUnlockResult(
            accepted,
            Convert.ToHexString(challenge).ToLowerInvariant(),
            queryResult?.StatusCode ?? uint.MaxValue,
            queryResult is null ? "<skipped>" : FormatResponse(queryResult.Response),
            challengeResult.StatusCode,
            FormatResponse(challengeResult.Response),
            verifyResult.StatusCode,
            FormatResponse(verifyResult.Response),
            postVerifyResult?.StatusCode ?? uint.MaxValue,
            FormatResponse(postVerifyResult?.Response ?? []));
    }

    private static byte[] CreateVerifyRequest(ReadOnlySpan<byte> challenge)
    {
        byte[] request = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1);
        FccUnlockAlgorithm.CreateResponseBytes(challenge).CopyTo(request, 4);
        return request;
    }

    private static MobileBroadbandDeviceService GetDellDefaultFccService()
    {
        MobileBroadbandModem? modem = MobileBroadbandModem.GetDefault();
        MobileBroadbandDeviceService? service = modem?.GetDeviceService(FccDeviceServiceId);
        if (service is null || !service.SupportedCommands.Contains(1u))
        {
            throw new InvalidOperationException(
                "The default modem no longer exposes FCC device-service command 1.");
        }

        return service;
    }

    private static void ReleaseSessionWithoutClosing(
        MobileBroadbandDeviceServiceCommandSession session)
    {
        // Match the C++/WinRT com_ptr lifetime in Dell's implementation: release
        // the interface reference without invoking the CloseSession method.
        ((WinRT.IWinRTObject)session).NativeObject.Dispose();
    }

    public static void SelfTestProtocol()
    {
        byte[] request = CreateVerifyRequest(Convert.FromHexString("c53900f7"));
        byte[] expected = Convert.FromHexString("010000002dfc5799");
        if (!request.SequenceEqual(expected))
        {
            throw new InvalidOperationException("MBIM request-layout self-test failed.");
        }
    }

    private static async Task<MbimCommandResult> SendSetAsync(
        MobileBroadbandDeviceService service,
        byte[] request,
        string stage,
        CancellationToken cancellationToken)
    {
        MobileBroadbandDeviceServiceCommandSession session = service.OpenCommandSession();
        var requestBuffer = new Windows.Storage.Streams.Buffer(4096);
        request.CopyTo(0, requestBuffer, 0, request.Length);
        requestBuffer.Length = (uint)request.Length;
        Windows.Foundation.IAsyncOperation<MobileBroadbandDeviceServiceCommandResult> operation =
            session.SendSetCommandAsync(1, requestBuffer);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (operation.Status == Windows.Foundation.AsyncStatus.Started &&
            DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        Windows.Foundation.AsyncStatus finalStatus = operation.Status;
        if (finalStatus == Windows.Foundation.AsyncStatus.Started)
        {
            throw new TimeoutException(
                $"FCC MBIM {stage} command timed out; asyncStatus={finalStatus}.");
        }

        if (finalStatus == Windows.Foundation.AsyncStatus.Canceled)
        {
            throw new OperationCanceledException(
                $"FCC MBIM {stage} command was canceled.",
                cancellationToken);
        }

        if (finalStatus == Windows.Foundation.AsyncStatus.Error)
        {
            throw new InvalidOperationException(
                $"FCC MBIM {stage} async operation failed.",
                operation.ErrorCode);
        }

        MobileBroadbandDeviceServiceCommandResult result = operation.GetResults();

        MbimCommandResult commandResult =
            new(result.StatusCode, ReadBuffer(result.ResponseData));
        ReleaseSessionWithoutClosing(session);
        return commandResult;
    }

    private static string FormatResponse(byte[] response) =>
        response.Length == 0 ? "<empty>" : Convert.ToHexString(response);

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        using DataReader reader = DataReader.FromBuffer(buffer);
        byte[] bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

internal enum ModemAvailability
{
    NotPresent,
    NotReady,
    GenericPersona,
    Present
}

internal sealed record ModemSnapshot(
    MobileBroadbandModem? Modem,
    ModemAvailability Availability,
    MobileBroadbandRadioState? RadioState);

internal sealed record MbimQueryResult(uint StatusCode, byte[] Response);

internal sealed record MbimCommandResult(uint StatusCode, byte[] Response);

internal sealed record MbimUnlockResult(
    bool Accepted,
    string Challenge,
    uint QueryStatus,
    string QueryResponse,
    uint ChallengeStatus,
    string ChallengeResponse,
    uint VerifyStatus,
    string VerifyResponse,
    uint PostVerifyStatus,
    string PostVerifyResponse);
