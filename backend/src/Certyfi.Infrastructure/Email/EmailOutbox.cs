using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Certyfi.Infrastructure.Email;

public record EmailContent(string Subject, string Html, string Text);

// Queues an email (see EmailOutboxEntry); EmailDispatcher sends it.
public class EmailOutbox
{
    private readonly PlatformDbContext _platformDb;
    private readonly ILogger<EmailOutbox> _logger;

    public EmailOutbox(PlatformDbContext platformDb, ILogger<EmailOutbox> logger)
    {
        _platformDb = platformDb;
        _logger = logger;
    }

    // Never throws: the email is a side effect of something that has
    // already happened (a certificate was created), which mustn't be undone
    // or reported as failed because the email couldn't be queued.
    public async Task<bool> QueueAsync(string kind, string toEmail, string? toName, EmailContent content,
        Guid? tenantId, Guid? studentId, Guid? credentialId, CancellationToken ct)
    {
        try
        {
            _platformDb.EmailOutbox.Add(new EmailOutboxEntry
            {
                Kind = kind, ToEmail = toEmail, ToName = toName,
                Subject = content.Subject.Length > 300 ? content.Subject[..300] : content.Subject,
                HtmlBody = content.Html, TextBody = content.Text,
                TenantId = tenantId, StudentId = studentId, CredentialId = credentialId,
            });
            await _platformDb.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not queue {Kind} email for tenant {TenantId}.", kind, tenantId);
            _platformDb.ChangeTracker.Clear();
            return false;
        }
    }

    // Many at once (bulk invitations), saved in chunks.
    public async Task<int> QueueManyAsync(IEnumerable<EmailOutboxEntry> entries, CancellationToken ct)
    {
        var count = 0;
        foreach (var chunk in entries.Chunk(500))
        {
            _platformDb.EmailOutbox.AddRange(chunk);
            await _platformDb.SaveChangesAsync(ct);
            _platformDb.ChangeTracker.Clear();
            count += chunk.Length;
        }
        return count;
    }

    // Erasure: drop queued or recorded emails about this certificate, and
    // about the student when they're being removed.
    public Task ForgetAsync(Guid credentialId, Guid? studentId, CancellationToken ct) =>
        _platformDb.EmailOutbox
            .Where(e => e.CredentialId == credentialId || (studentId != null && e.StudentId == studentId))
            .ExecuteDeleteAsync(ct);
}
