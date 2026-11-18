using System.Net.Mail;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Managing an institution's staff accounts, shared by the Institution Admin
// (UsersController, request-scoped TenantDbContext) and SuperAdmins
// (TenantsController, a TenantDbContext for any institution) so both get
// the same safeguards:
//   - Institution admin accounts are managed only by Ahamsys (a SuperAdmin,
//     actingUserId null): an institution's own admins can't add, change,
//     deactivate or reset another admin, so no admin can take over another's
//     account or add a second admin they control (final approval and
//     dual-control rule changes depend on admins being two real people);
//   - an institution keeps at least two active Institution Admins (one, for
//     institutions created before this rule, is kept);
//   - nobody deactivates themselves or removes their own admin role;
//   - every change is written to the institution audit log;
//   - Partner API service accounts aren't people and aren't managed here
//     (they're revoked on the API keys page) — they read as "not found".
public class TenantUserService
{
    public async Task<IReadOnlyList<TenantUserDetail>> ListAsync(TenantDbContext db, CancellationToken ct)
    {
        var users = await Users(db).OrderBy(u => u.FullName).ToListAsync(ct);
        var counts = await CountsAsync(db, users.Select(u => u.Id).ToList(), ct);
        return users.Select(u => ToDetail(u, counts)).ToList();
    }

    public async Task<TenantUserDetail?> GetAsync(TenantDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await Users(db).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return null;
        return ToDetail(user, await CountsAsync(db, new List<Guid> { userId }, ct));
    }

    public const string AdminsByPlatformOnly = "Institution admins are managed by Ahamsys. Contact Ahamsys to add, change or remove one.";

    // Who acted, for the audit log: a staff member, or Ahamsys (actingUserId null).
    public record Actor(Guid? UserId, string Name)
    {
        public bool IsPlatform => UserId is null;
    }

    public async Task<UserOperationResult> UpdateAsync(
        TenantDbContext db, Guid userId, string? fullName, string? email, IReadOnlyCollection<string>? roles, Actor actor, CancellationToken ct)
    {
        var actingUserId = actor.UserId;
        var user = await Users(db).AsTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return UserOperationResult.NotFound();

        var name = fullName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return UserOperationResult.Invalid("Full name is required (max 200 characters).");
        var normalizedEmail = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedEmail) || normalizedEmail.Length > 320 || !MailAddress.TryCreate(normalizedEmail, out _))
            return UserOperationResult.Invalid("A valid email address is required.");
        var roleNames = (roles ?? Array.Empty<string>()).Distinct().ToList();
        if (roleNames.Count == 0) return UserOperationResult.Invalid("At least one role is required.");
        var unknown = roleNames.Where(r => !SystemRole.TenantAssignable.Contains(r)).ToList();
        if (unknown.Count > 0) return UserOperationResult.Invalid($"Unknown role(s): {string.Join(", ", unknown)}.");

        if (normalizedEmail != user.Email && await db.Users.AnyAsync(u => u.Email == normalizedEmail && u.Id != userId, ct))
            return UserOperationResult.Conflict("Another user in this institution already has that email address.");

        var wasAdmin = user.UserRoles.Any(ur => ur.Role.Name == SystemRole.InstitutionAdmin);
        var staysAdmin = roleNames.Contains(SystemRole.InstitutionAdmin);
        if (!actor.IsPlatform && (wasAdmin || staysAdmin)) return UserOperationResult.Forbidden(AdminsByPlatformOnly);
        if (wasAdmin && !staysAdmin)
        {
            if (actingUserId == userId) return UserOperationResult.Forbidden("You can't remove your own Institution Admin role.");
            if (user.IsActive && await TooFewAdminsWithoutAsync(db, userId, ct) is { } problem) return UserOperationResult.Conflict(problem);
        }
        var oldRoles = string.Join(", ", user.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r));
        var newRoles = string.Join(", ", roleNames.OrderBy(r => r));

        var roleEntities = await db.Roles.Where(r => roleNames.Contains(r.Name)).ToListAsync(ct);
        if (roleEntities.Count != roleNames.Count) return UserOperationResult.Invalid("One or more roles aren't set up for this institution.");

        user.FullName = name;
        user.Email = normalizedEmail;
        db.UserRoles.RemoveRange(user.UserRoles.Where(ur => !roleNames.Contains(ur.Role.Name)));
        foreach (var role in roleEntities.Where(r => user.UserRoles.All(ur => ur.RoleId != r.Id)))
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
        if (oldRoles != newRoles) Audit(db, actor, "RolesChanged", $"{user.FullName} ({user.Email})", $"{oldRoles} → {newRoles}");
        await db.SaveChangesAsync(ct);
        return UserOperationResult.Ok((await GetAsync(db, userId, ct))!);
    }

    public async Task<UserOperationResult> SetActiveAsync(TenantDbContext db, Guid userId, bool active, Actor actor, CancellationToken ct)
    {
        var user = await Users(db).AsTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return UserOperationResult.NotFound();
        var isAdmin = user.UserRoles.Any(ur => ur.Role.Name == SystemRole.InstitutionAdmin);
        if (isAdmin && !actor.IsPlatform) return UserOperationResult.Forbidden(AdminsByPlatformOnly);

        if (!active)
        {
            if (actor.UserId == userId) return UserOperationResult.Forbidden("You can't deactivate your own account.");
            if (isAdmin && user.IsActive && await TooFewAdminsWithoutAsync(db, userId, ct) is { } problem) return UserOperationResult.Conflict(problem);
        }

        if (user.IsActive != active) Audit(db, actor, active ? "UserReactivated" : "UserDeactivated", $"{user.FullName} ({user.Email})", RolesOf(user));
        user.IsActive = active;
        await db.SaveChangesAsync(ct);
        return UserOperationResult.Ok((await GetAsync(db, userId, ct))!);
    }

    // Sets a one-time temporary password (returned once, never stored in
    // clear) and makes the user choose a new one at their next sign-in.
    public async Task<(UserOperationResult Result, string? TemporaryPassword)> ResetPasswordAsync(
        TenantDbContext db, Guid userId, Actor actor, CancellationToken ct)
    {
        var user = await Users(db).AsTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (UserOperationResult.NotFound(), null);
        if (actor.UserId == userId)
            return (UserOperationResult.Forbidden("Use Change password to change your own password."), null);
        if (!actor.IsPlatform && user.UserRoles.Any(ur => ur.Role.Name == SystemRole.InstitutionAdmin))
            return (UserOperationResult.Forbidden(AdminsByPlatformOnly), null);
        Audit(db, actor, "PasswordReset", $"{user.FullName} ({user.Email})");

        var temporary = PasswordPolicy.GenerateTemporary();
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(temporary);
        user.MustChangePassword = true;
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (UserOperationResult.Ok((await GetAsync(db, userId, ct))!), temporary);
    }

    private static IQueryable<ApplicationUser> Users(TenantDbContext db) =>
        db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Where(u => !u.IsServiceAccount);

    // Removing this admin would leave fewer than two active admins (or none,
    // for an institution that only ever had one): returns why it's refused.
    private static async Task<string?> TooFewAdminsWithoutAsync(TenantDbContext db, Guid userId, CancellationToken ct)
    {
        var active = await ActiveAdminIdsAsync(db, ct);
        var left = active.Count(id => id != userId);
        if (left == 0) return "This is the institution's only active Institution Admin. Add another admin first.";
        if (active.Count >= 2 && left < 2) return "An institution keeps at least two active Institution Admins. Add another admin first.";
        return null;
    }

    public static Task<List<Guid>> ActiveAdminIdsAsync(TenantDbContext db, CancellationToken ct) =>
        db.UserRoles.Where(ur => ur.Role.Name == SystemRole.InstitutionAdmin && ur.User.IsActive && !ur.User.IsServiceAccount)
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);

    public static void Audit(TenantDbContext db, Actor actor, string action, string subject, string? details = null) =>
        InstitutionAudit.Add(db, actor.IsPlatform ? InstitutionAudit.Platform : InstitutionAudit.Staff, actor.UserId, actor.Name, action, subject, details);

    private static string RolesOf(ApplicationUser user) => string.Join(", ", user.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r));

    private static async Task<Dictionary<Guid, (int Requested, int Decided)>> CountsAsync(TenantDbContext db, List<Guid> userIds, CancellationToken ct)
    {
        var requested = await db.Credentials.Where(c => userIds.Contains(c.RequestedByUserId))
            .GroupBy(c => c.RequestedByUserId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var decided = await db.Credentials.Where(c => c.DecidedByUserId != null && userIds.Contains(c.DecidedByUserId.Value))
            .GroupBy(c => c.DecidedByUserId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return userIds.ToDictionary(id => id, id => (
            requested.FirstOrDefault(r => r.Key == id)?.Count ?? 0,
            decided.FirstOrDefault(d => d.Key == id)?.Count ?? 0));
    }

    private static TenantUserDetail ToDetail(ApplicationUser u, Dictionary<Guid, (int Requested, int Decided)> counts) => new(
        u.Id, u.Email, u.FullName, u.IsActive, u.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r).ToList(),
        u.CreatedAt, u.LastLoginAt, u.MustChangePassword, u.PasswordChangedAt,
        counts.TryGetValue(u.Id, out var c) ? c.Requested : 0, counts.TryGetValue(u.Id, out c) ? c.Decided : 0);
}

public record TenantUserDetail(
    Guid Id, string Email, string FullName, bool IsActive, IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, bool MustChangePassword, DateTimeOffset? PasswordChangedAt,
    int CertificatesRequested, int CertificatesDecided);

// Status is the HTTP status the caller should return.
public record UserOperationResult(int Status, string? Error, TenantUserDetail? User)
{
    public static UserOperationResult Ok(TenantUserDetail user) => new(200, null, user);
    public static UserOperationResult NotFound() => new(404, "User not found.", null);
    public static UserOperationResult Invalid(string error) => new(400, error, null);
    public static UserOperationResult Forbidden(string error) => new(403, error, null);
    public static UserOperationResult Conflict(string error) => new(409, error, null);
}
