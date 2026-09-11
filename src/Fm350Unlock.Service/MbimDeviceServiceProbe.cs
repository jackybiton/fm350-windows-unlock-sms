using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Networking.NetworkOperators;
using Windows.Storage.Streams;

namespace Fm350Unlock.Service;

internal static class MbimDeviceServiceProbe
{
    // The only device-service GUID embedded in Dell's WWANModemAuthenticator.exe.
    internal static readonly Guid FccDeviceServiceId = new("F85D46EF-AB26-4081-9868-4D183C0A3AEC");

    public static async Task<int> RunAsync(
        ILogger logger,
        bool queryFccCommand,
        CancellationToken cancellationToken)
    {
        string selector = MobileBroadbandModem.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        logger.LogInformation("Found {Count} mobile-broadband modem device(s).", devices.Count);
        foreach (DeviceInformation device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("Modem: {Name}; id={Id}", device.Name, device.Id);

            MobileBroadbandModem? modem = MobileBroadbandModem.FromId(device.Id);
            if (modem is null)
            {
                logger.LogWarning("MobileBroadbandModem.FromId returned null for {Id}.", device.Id);
                continue;
            }

            MobileBroadbandDeviceService? service = modem.GetDeviceService(FccDeviceServiceId);
            if (service is null)
            {
                logger.LogWarning("FCC device service {ServiceId} is not exposed by {Name}.", FccDeviceServiceId, device.Name);
                continue;
            }

            logger.LogInformation(
                "FCC device service found: id={ServiceId}; supported commands={Commands}",
                service.DeviceServiceId,
                string.Join(",", service.SupportedCommands));

            if (queryFccCommand)
            {
                try
                {
                    await QueryFccCommandAsync(service, logger, cancellationToken);
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("FCC query command 1 timed out after three seconds.");
                }
            }
        }

        return 0;
    }

    private static async Task QueryFccCommandAsync(
        MobileBroadbandDeviceService service,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        MobileBroadbandDeviceServiceCommandSession session = service.OpenCommandSession();
        try
        {
            const uint commandId = 1;
            if (!service.SupportedCommands.Contains(commandId))
            {
                logger.LogWarning("FCC query command 1 is not listed as supported.");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Match Dell's helper exactly: capacity 4096, logical length zero.
            Windows.Storage.Streams.Buffer empty = new(4096);
            MobileBroadbandDeviceServiceCommandResult result = await session
                .SendQueryCommandAsync(commandId, empty)
                .AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            byte[] response = ReadBuffer(result.ResponseData);
            logger.LogInformation(
                "Query command {CommandId}: status=0x{StatusCode:X8}; response length={Length}; response={Response}",
                commandId,
                result.StatusCode,
                response.Length,
                response.Length == 0 ? "<empty>" : Convert.ToHexString(response));
        }
        finally
        {
            session.CloseSession();
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        using DataReader reader = DataReader.FromBuffer(buffer);
        byte[] bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
