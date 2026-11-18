using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Email;

// Sends due outbox emails. Each one is claimed (Pending → Sending) with a
// conditional update first, so two runs never send the same email. A failure
// is retried with growing delays up to Email:MaxAttempts, then Failed.
// Bodies are deleted once an email is finished; finished records older than
// RetentionDays are purged.
public class EmailDispatcher
{
    private const int BatchSize = 20;
    // Pace sends (Resend allows about 2 a second by default), so a bulk
    // invitation drains steadily instead of hitting the provider's limit.
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(500);
    private const int RetentionDays = 90;
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
        TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
    };
    // A run that crashed mid-send leaves rows in Sending; retry them after this.
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private readonly PlatformDbContext _db;
    private readonly IEmailTransport _transport;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailDispatcher> _logger;

    public EmailDispatcher(PlatformDbContext db, IEmailTransport transport, IOptions<EmailOptions> options, ILogger<EmailDispatcher> logger)
    {
        _db = db;
        _transport = transport;
        _options = options.Value;
        _logger = logger;
    }

    // Returns how many emails were handled (sent, skipped or failed for good).
    public async Task<int> SendDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.EmailOutbox
            .Where(e => e.Status == EmailStatus.Sending && e.NextAttemptAt < now - StuckAfter)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EmailStatus.Pending), ct);

        var due = await _db.EmailOutbox.AsNoTracking()
            .Where(e => e.Status == EmailStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var handled = 0;
        foreach (var id in due)
        {
            var claimed = await _db.EmailOutbox
                .Where(e => e.Id == id && e.Status == EmailStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EmailStatus.Sending).SetProperty(e => e.NextAttemptAt, now), ct);
            if (claimed == 0) continue;

            var entry = await _db.EmailOutbox.FirstAsync(e => e.Id == id, ct);
            if (await SendOneAsync(entry, ct)) handled++;
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            if (_options.Provider == "Resend") await Task.Delay(Spacing, ct);
        }

        await _db.EmailOutbox
            .Where(e => (e.Status == EmailStatus.Sent || e.Status == EmailStatus.Failed || e.Status == EmailStatus.Skipped)
                && e.CreatedAt < now.AddDays(-RetentionDays))
            .ExecuteDeleteAsync(ct);
        return handled;
    }

    private async Task<bool> SendOneAsync(EmailOutboxEntry entry, CancellationToken ct)
    {
        entry.Attempts++;
        try
        {
            var result = await _transport.SendAsync(
                new OutgoingEmail(entry.Id, entry.ToEmail, entry.ToName, entry.Subject, entry.HtmlBody ?? "", entry.TextBody ?? ""), ct);
            entry.Status = result.Delivered ? EmailStatus.Sent : EmailStatus.Skipped;
            entry.SentAt = result.Delivered ? DateTimeOffset.UtcNow : null;
            entry.ProviderMessageId = result.ProviderMessageId;
            entry.LastError = result.Delivered ? null : "No email provider configured (Email:Provider = None).";
            Finish(entry);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            entry.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            if (entry.Attempts >= _options.MaxAttempts)
            {
                entry.Status = EmailStatus.Failed;
                Finish(entry);
                _logger.LogError(ex, "Gave up on {Kind} email {Id} after {Attempts} attempts.", entry.Kind, entry.Id, entry.Attempts);
                return true;
            }
            entry.Status = EmailStatus.Pending;
            entry.NextAttemptAt = DateTimeOffset.UtcNow + Backoff[Math.Min(entry.Attempts - 1, Backoff.Length - 1)];
            _logger.LogWarning("Sending {Kind} email {Id} failed (attempt {Attempts}): {Error}", entry.Kind, entry.Id, entry.Attempts, entry.LastError);
            return false;
        }
    }

    // Sign-in links live in the bodies; they go once there's no more sending.
    private static void Finish(EmailOutboxEntry entry)
    {
        entry.HtmlBody = null;
        entry.TextBody = null;
    }
}

public class EmailBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailBackgroundService> _logger;

    public EmailBackgroundService(IServiceScopeFactory scopeFactory, IOptions<EmailOptions> options, ILogger<EmailBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.DispatchIntervalSeconds <= 0) return;
        var interval = TimeSpan.FromSeconds(_options.DispatchIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().SendDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Email dispatch run failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
