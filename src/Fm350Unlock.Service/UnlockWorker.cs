using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fm350Unlock.Service;

internal sealed class UnlockWorker(
    UnlockCoordinator coordinator,
    ServiceLog serviceLog,
    ILogger<UnlockWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FM350 unlock service started.");
        serviceLog.Write("FM350 unlock service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await coordinator.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled unlock-loop error.");
                serviceLog.Write($"Unhandled unlock-loop error: {exception.Message}");
                delay = TimeSpan.FromSeconds(5);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
