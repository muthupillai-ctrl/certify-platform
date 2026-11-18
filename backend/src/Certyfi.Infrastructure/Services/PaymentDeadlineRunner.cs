using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Services;

// Payment deadlines for certificates AwaitingPayment, institution by
// institution: one reminder email a few days before the due date, then
// cancellation once it's passed (Cancelled, CancelledForNonPayment, student
// emailed). A student paying at the same moment as the cancellation can't
// both succeed (CredentialIssuanceService.LockStatusAsync).
public class PaymentDeadlineRunner
{
    public const string CancellationReason = "Not paid by the due date";
    // Remind this long before the due date, or halfway through the period
    // if that's later (short periods).
    public static readonly TimeSpan ReminderBefore = TimeSpan.FromDays(3);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentDeadlineRunner> _logger;

    public PaymentDeadlineRunner(IServiceScopeFactory scopeFactory, ILogger<PaymentDeadlineRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public record Result(int Reminded, int Cancelled);

    public async Task<Result> RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<Tenant> tenants;
        using (var scope = _scopeFactory.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking()
                .Where(t => t.Status == TenantStatus.Active).ToListAsync(ct);
        }

        int reminded = 0, cancelled = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                var (r, c) = await RunForTenantAsync(tenant, now, ct);
                reminded += r;
                cancelled += c;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Payment deadlines failed for tenant {Slug}.", tenant.Slug);
            }
        }
        return new Result(reminded, cancelled);
    }

    private async Task<(int Reminded, int Cancelled)> RunForTenantAsync(Tenant tenant, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenant.Id, tenant.Slug, tenant.SchemaName);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var issuance = scope.ServiceProvider.GetRequiredService<CredentialIssuanceService>();

        // Overdue: cancel.
        var overdue = await db.Credentials.Include(c => c.Achievement)
            .Where(c => c.Status == CredentialStatus.AwaitingPayment && c.PaymentDueAt != null && c.PaymentDueAt <= now)
            .OrderBy(c => c.PaymentDueAt).Take(BatchSize).ToListAsync(ct);
        var cancelled = 0;
        foreach (var credential in overdue)
        {
            try
            {
                await issuance.CancelAsync(credential, credential.RequestedByUserId, CancellationReason, ct, forNonPayment: true);
                cancelled++;
            }
            catch (CredentialStateChangedException)
            {
                // Paid just now: it's no longer ours to cancel.
                db.ChangeTracker.Clear();
            }
        }

        // Due soon and not reminded yet: remind.
        var soon = now + ReminderBefore;
        var candidates = await db.Credentials.Include(c => c.Achievement)
            .Where(c => c.Status == CredentialStatus.AwaitingPayment && c.PaymentReminderSentAt == null
                && c.PaymentDueAt != null && c.PaymentDueAt > now && c.PaymentDueAt <= soon)
            .OrderBy(c => c.PaymentDueAt).Take(BatchSize).ToListAsync(ct);
        var reminded = 0;
        foreach (var credential in candidates)
        {
            var due = credential.PaymentDueAt!.Value;
            var halfway = credential.CreatedAt + (due - credential.CreatedAt) / 2;
            var remindAt = halfway > due - ReminderBefore ? halfway : due - ReminderBefore;
            if (now < remindAt) continue;
            await issuance.MarkPaymentReminderSentAsync(credential, ct);
            reminded++;
        }

        if (cancelled + reminded > 0)
        {
            _logger.LogInformation("Payment deadlines for {Slug}: {Reminded} reminded, {Cancelled} cancelled.", tenant.Slug, reminded, cancelled);
        }
        return (reminded, cancelled);
    }
}

// Runs PaymentDeadlineRunner every PaymentDeadlines:IntervalMinutes
// (default 5; 0 turns it off, as in the integration tests, which run it
// explicitly with a chosen "now").
public class PaymentDeadlineBackgroundService : BackgroundService
{
    private readonly PaymentDeadlineRunner _runner;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentDeadlineBackgroundService> _logger;

    public PaymentDeadlineBackgroundService(PaymentDeadlineRunner runner, IConfiguration configuration, ILogger<PaymentDeadlineBackgroundService> logger)
    {
        _runner = runner;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _configuration.GetValue("PaymentDeadlines:IntervalMinutes", 5);
        if (minutes <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Payment deadline run failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
