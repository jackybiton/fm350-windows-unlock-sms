using Fm350Unlock.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    FccUnlockAlgorithm.SelfTest();
    MbimFccClient.SelfTestProtocol();
    Console.WriteLine("FM350 FCC unlock algorithm self-test passed.");
    return 0;
}

if (args.Contains("--locate-at", StringComparer.OrdinalIgnoreCase))
{
    AtPort? port = new AtPortLocator().FindStartedPort();
    if (port is null)
    {
        Console.Error.WriteLine("No started FM350/DW5931E AT port was found.");
        return 1;
    }

    Console.WriteLine($"{port.Name}: {port.DeviceId} ({port.FriendlyName})");
    return 0;
}

if (args.Contains("--probe-mbim", StringComparer.OrdinalIgnoreCase))
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options => options.SingleLine = true));
    return await MbimDeviceServiceProbe.RunAsync(
        loggerFactory.CreateLogger("MbimProbe"),
        args.Contains("--query-fcc", StringComparer.OrdinalIgnoreCase),
        CancellationToken.None);
}

if (args.Contains("--query-mbim-default", StringComparer.OrdinalIgnoreCase))
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options => options.SingleLine = true));
    var client = new MbimFccClient(loggerFactory.CreateLogger<MbimFccClient>());
    ModemSnapshot? snapshot = client.GetAnyDefaultSnapshot();
    if (snapshot is null)
    {
        Console.Error.WriteLine("No default FCC-capable FM350 modem was found.");
        return 1;
    }

    if (snapshot.Modem is null)
    {
        Console.Error.WriteLine($"The default FCC-capable modem is not ready ({snapshot.Availability}).");
        return 1;
    }

    MbimQueryResult? result = await client.QueryFccStateAsync(
        snapshot.Modem,
        CancellationToken.None);
    if (result is null)
    {
        Console.Error.WriteLine("The FCC device service or command 1 is unavailable.");
        return 1;
    }

    string response = result.Response.Length == 0
        ? "<empty>"
        : Convert.ToHexString(result.Response);
    Console.WriteLine(
        $"radio={snapshot.RadioState}; status=0x{result.StatusCode:X8}; response={response}");
    return result.StatusCode == 0 ? 0 : 1;
}

bool waitForMbim =
    args.Contains("--unlock-mbim-wait", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--unlock-mbim-experimental-wait", StringComparer.OrdinalIgnoreCase);
bool unlockMbim =
    args.Contains("--unlock-mbim", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--unlock-mbim-experimental", StringComparer.OrdinalIgnoreCase);
if (waitForMbim || unlockMbim)
{
    var auditLog = new ServiceLog();
    string identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
    auditLog.Write($"One-shot MBIM unlock started as {identity}.");
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options => options.SingleLine = true));
    try
    {
        var client = new MbimFccClient(loggerFactory.CreateLogger<MbimFccClient>());
        ModemSnapshot snapshot = client.GetDellDefaultSnapshot();
        if (waitForMbim)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while ((snapshot.Modem is null || snapshot.RadioState is null) &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                snapshot = client.GetDellDefaultSnapshot();
            }

            if (snapshot.Modem is not null && snapshot.RadioState is not null)
            {
                // Let the private device-service endpoint settle after the
                // default modem first becomes visible during startup.
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                snapshot = client.GetDellDefaultSnapshot();
            }
        }

        if (snapshot.Modem is null || snapshot.RadioState is null)
        {
            string message = $"No ready FCC-capable modem was found ({snapshot.Availability}).";
            Console.Error.WriteLine(message);
            auditLog.Write($"One-shot MBIM unlock stopped: {message}");
            return 1;
        }

        if (snapshot.RadioState != Windows.Networking.NetworkOperators.MobileBroadbandRadioState.Off)
        {
            string message =
                $"Refusing MBIM Set while radio state is {snapshot.RadioState}; expected Off.";
            Console.Error.WriteLine(message);
            auditLog.Write($"One-shot MBIM unlock stopped: {message}");
            return 2;
        }

        Console.WriteLine("Executing the verified Dell MBIM Query/Set sequence once.");
        MbimUnlockResult? result = await client.UnlockAsync(snapshot.Modem, CancellationToken.None);
        if (result is null)
        {
            const string message = "The FCC device service or command 1 is unavailable.";
            Console.Error.WriteLine(message);
            auditLog.Write($"One-shot MBIM unlock stopped: {message}");
            return 1;
        }

        string resultMessage =
            $"accepted={result.Accepted}; queryStatus=0x{result.QueryStatus:X8}; " +
            $"queryResponse={result.QueryResponse}; challenge={result.Challenge}; " +
            $"challengeStatus=0x{result.ChallengeStatus:X8}; " +
            $"challengeResponse={result.ChallengeResponse}; verifyStatus=0x{result.VerifyStatus:X8}; " +
            $"verifyResponse={result.VerifyResponse}; " +
            $"postVerifyStatus=0x{result.PostVerifyStatus:X8}; " +
            $"postVerifyResponse={result.PostVerifyResponse}";
        Console.WriteLine(resultMessage);
        auditLog.Write($"One-shot MBIM unlock result: {resultMessage}");
        return result.Accepted ? 0 : 1;
    }
    catch (Exception exception)
    {
        string message =
            $"One-shot MBIM unlock failed: {exception.GetType().Name}: {exception.Message}";
        Console.Error.WriteLine(message);
        auditLog.Write(message);
        return 1;
    }
}

if (args.Contains("--once", StringComparer.OrdinalIgnoreCase))
{
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        builder.AddSimpleConsole(options => options.SingleLine = true));
    var coordinator = new UnlockCoordinator(
        new MbimFccClient(loggerFactory.CreateLogger<MbimFccClient>()),
        new ServiceLog(),
        loggerFactory.CreateLogger<UnlockCoordinator>());
    await coordinator.RunOnceAsync(CancellationToken.None);
    return 0;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "FM350 FCC Unlock Service";
});
builder.Services.AddSingleton<ServiceLog>();
builder.Services.AddSingleton<MbimFccClient>();
builder.Services.AddSingleton<UnlockCoordinator>();
builder.Services.AddHostedService<UnlockWorker>();

using IHost host = builder.Build();
await host.RunAsync();
return 0;
