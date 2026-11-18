using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;

namespace Certyfi.Infrastructure.Services;

// Writes to the institution audit log (InstitutionAuditEntry). Entries are
// added to the given context and saved with the change they describe, so
// the log and the change are never out of step.
public static class InstitutionAudit
{
    public const string Staff = "Staff";
    public const string Platform = "Platform";

    public static void Add(TenantDbContext db, string actorKind, Guid? actorId, string actorName, string action, string subject, string? details = null)
    {
        db.InstitutionAuditLog.Add(new InstitutionAuditEntry
        {
            ActorKind = actorKind,
            ActorId = actorId,
            ActorName = Clip(actorName, 200),
            Action = action,
            Subject = Clip(subject, 300),
            Details = details is null ? null : Clip(details, 1000),
        });
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
