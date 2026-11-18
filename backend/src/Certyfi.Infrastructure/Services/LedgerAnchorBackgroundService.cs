using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Services;

// Thin scheduler over ILedgerCheckpointService — the actual checkpoint/
// upgrade logic lives there so it can also be triggered immediately and
// synchronously via AnchoringController's admin endpoints (same
// "deliberate, audited, on-demand action" pattern as
// TenantsController.MigrateSchemas), which is also what makes it directly
// integration-testable without waiting on these timers.
public class LedgerAnchorBackgroundService : BackgroundService
{
    // Tuning knobs, not architectural constraints — cheap to change later.
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpgradeInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnchoringRunStatus _runStatus;
    private readonly ILogger<LedgerAnchorBackgroundService> _logger;

    public LedgerAnchorBackgroundService(IServiceScopeFactory scopeFactory, AnchoringRunStatus runStatus, ILogger<LedgerAnchorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _runStatus = runStatus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _runStatus.MarkBackgroundJobStarted(CheckpointInterval, UpgradeInterval);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var lastCheckpointRun = DateTimeOffset.MinValue;
        var lastUpgradeRun = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var checkpointService = scope.ServiceProvider.GetRequiredService<ILedgerCheckpointService>();

                if (now - lastCheckpointRun >= CheckpointInterval)
                {
                    await checkpointService.RunCheckpointStepAsync("Scheduled", stoppingToken);
                    lastCheckpointRun = now;
                }
                if (now - lastUpgradeRun >= UpgradeInterval)
                {
                    await checkpointService.RunUpgradeStepAsync("Scheduled", stoppingToken);
                    lastUpgradeRun = now;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "LedgerAnchorBackgroundService iteration failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
