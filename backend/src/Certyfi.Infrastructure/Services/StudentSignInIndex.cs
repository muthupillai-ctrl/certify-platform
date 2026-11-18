using System.Security.Cryptography;
using System.Text;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Services;

// Keeps platform.student_sign_in_index (see StudentSignInEntry) in step
// with each institution's students: added when a student is created or
// changes email, removed when one is deleted or changes away, rebuilt for
// every institution by migrate-schemas (each deploy). Updates are
// best-effort (logged, never failing the student change): a missing entry
// only means that student uses their institution's own sign-in link until
// the next rebuild, and signing in there adds it back.
public class StudentSignInIndex
{
    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbs;
    private readonly ILogger<StudentSignInIndex> _logger;
    private readonly byte[] _key;

    public StudentSignInIndex(PlatformDbContext platformDb, TenantDbContextFactory tenantDbs, IOptions<KmsOptions> kms, ILogger<StudentSignInIndex> logger)
    {
        _platformDb = platformDb;
        _tenantDbs = tenantDbs;
        _logger = logger;
        _key = HMACSHA256.HashData(Convert.FromBase64String(kms.Value.LocalMasterKey), Encoding.UTF8.GetBytes("certyfi-student-sign-in-index-v1"));
    }

    // Email as normalised by StudentDirectory.Normalize.
    public string Hash(string email) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(StudentDirectory.Normalize(email) ?? ""))).ToLowerInvariant();

    // Active institutions that may have a student with this email.
    public async Task<List<Tenant>> InstitutionsForAsync(string email, CancellationToken ct)
    {
        var hash = Hash(email);
        return await _platformDb.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active && _platformDb.StudentSignInIndex.Any(e => e.EmailHash == hash && e.TenantId == t.Id))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Guid tenantId, string email, CancellationToken ct)
    {
        try
        {
            var hash = Hash(email);
            await _platformDb.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO platform.student_sign_in_index ("EmailHash", "TenantId", "AddedAt") VALUES ({hash}, {tenantId}, now()) ON CONFLICT DO NOTHING""", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not add a student to the sign-in index for tenant {TenantId}.", tenantId);
        }
    }

    public async Task RemoveAsync(Guid tenantId, string email, CancellationToken ct)
    {
        try
        {
            var hash = Hash(email);
            await _platformDb.StudentSignInIndex.Where(e => e.EmailHash == hash && e.TenantId == tenantId).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove a student from the sign-in index for tenant {TenantId}.", tenantId);
        }
    }

    // Replaces every institution's entries with its current students.
    // Returns the number of entries written.
    public async Task<int> RebuildAsync(CancellationToken ct)
    {
        var tenants = await _platformDb.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended).ToListAsync(ct);
        var total = 0;
        foreach (var tenant in tenants)
        {
            List<string> emails;
            await using (var db = _tenantDbs.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName))
            {
                emails = await db.Students.AsNoTracking().Select(s => s.Email).ToListAsync(ct);
            }
            var hashes = emails.Select(Hash).Distinct().ToArray();
            await using var tx = await _platformDb.Database.BeginTransactionAsync(ct);
            await _platformDb.StudentSignInIndex.Where(e => e.TenantId == tenant.Id).ExecuteDeleteAsync(ct);
            if (hashes.Length > 0)
            {
                await _platformDb.Database.ExecuteSqlInterpolatedAsync(
                    $"""INSERT INTO platform.student_sign_in_index ("EmailHash", "TenantId", "AddedAt") SELECT h, {tenant.Id}, now() FROM unnest({hashes}) AS h ON CONFLICT DO NOTHING""", ct);
            }
            await tx.CommitAsync(ct);
            total += hashes.Length;
        }
        return total;
    }
}
