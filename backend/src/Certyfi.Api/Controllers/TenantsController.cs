using System.Security.Claims;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Validation;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// SuperAdmin only. Not tenant-scoped (exempted path prefix). This is the
// Super Admin Console's tenant-management surface from the technical note —
// UI comes later, this is the API it will call.
[ApiController]
[Route("api/v1/platform/tenants")]
[Authorize(Roles = SystemRole.SuperAdmin)]
public class TenantsController : ControllerBase
{
    private readonly PlatformDbContext _platformDb;
    private readonly ITenantProvisioningService _provisioning;
    private readonly TenantDbContextFactory _tenantDbFactory;
    private readonly TenantUserService _users;
    private readonly StudentSignInIndex _studentSignIn;

    public TenantsController(PlatformDbContext platformDb, ITenantProvisioningService provisioning, TenantDbContextFactory tenantDbFactory, TenantUserService users, StudentSignInIndex studentSignIn)
    {
        _studentSignIn = studentSignIn;
        _platformDb = platformDb;
        _provisioning = provisioning;
        _tenantDbFactory = tenantDbFactory;
        _users = users;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantResponse>>> List()
    {
        var tenants = await _platformDb.Tenants
            .OrderBy(t => t.Name)
            .Select(t => new TenantResponse(t.Id, t.Name, t.Slug, t.Status.ToString(), t.CreatedAt))
            .ToListAsync();

        return Ok(tenants);
    }

    [HttpPost]
    public async Task<ActionResult<TenantResponse>> Create(CreateTenantRequest request)
    {
        var admins = new List<NewInstitutionAdmin>
        {
            new(request.InstitutionAdminEmail ?? "", request.InstitutionAdminFullName ?? "", request.InstitutionAdminInitialPassword ?? ""),
            new(request.SecondAdminEmail ?? "", request.SecondAdminFullName ?? "", request.SecondAdminInitialPassword ?? ""),
        };
        foreach (var (admin, which) in admins.Zip(new[] { "first", "second" }))
        {
            if (string.IsNullOrWhiteSpace(admin.Email) || !System.Net.Mail.MailAddress.TryCreate(admin.Email.Trim(), out _))
                return BadRequest(new { error = $"The {which} Institution Admin needs a valid email. Every institution has two admins." });
            if (string.IsNullOrWhiteSpace(admin.FullName) || admin.FullName.Trim().Length > 200)
                return BadRequest(new { error = $"The {which} Institution Admin needs a name (max 200 characters)." });
            if (PasswordPolicy.Validate(admin.InitialPassword, admin.Email) is { } passwordError)
                return BadRequest(new { error = $"The {which} Institution Admin's password: {passwordError}" });
        }
        try
        {
            var tenant = await _provisioning.ProvisionAsync(request.Name, request.Slug, admins);

            return CreatedAtAction(nameof(List), new TenantResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Status.ToString(), tenant.CreatedAt));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    // Backfills every active tenant's schema with any tenant-schema
    // migrations added since it was provisioned (e.g. a new table). Safe to
    // call repeatedly — see TenantDbContextFactory.EnsureTenantSchemaMigratedAsync.
    // Deliberately a manual, audited, SuperAdmin-only action rather than
    // something run automatically on every deploy: the underlying raw-script
    // path takes no Postgres advisory lock, so concurrent app instances
    // calling it at once could race — acceptable given this is expected to
    // be called rarely, by one operator, not on every restart.
    [HttpPost("migrate-schemas")]
    public async Task<IActionResult> MigrateSchemas(CancellationToken ct)
    {
        var count = await _provisioning.MigrateAllTenantSchemasAsync(ct);
        // Also brings the students' email-only sign-in index up to date.
        var indexed = await _studentSignIn.RebuildAsync(ct);
        return Ok(new { migratedTenantCount = count, studentSignInEntries = indexed });
    }

    // ---------- One institution: profile, status, users ----------

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TenantDetailResponse>> Get(Guid id, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });
        return Ok(await DetailAsync(tenant, ct));
    }

    [HttpPut("{id:guid}/profile")]
    public async Task<ActionResult<TenantDetailResponse>> UpdateProfile(Guid id, TenantProfileInput request, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });
        if (TenantProfileValidator.Apply(tenant, request) is { } error) return BadRequest(new { error });
        await _platformDb.SaveChangesAsync(ct);
        return Ok(await DetailAsync(tenant, ct));
    }

    // Staff sign-in and the Partner API stop working; the institution's
    // certificates stay publicly verifiable (see Tenant.SuspendedAt).
    [HttpPost("{id:guid}/suspend")]
    public async Task<ActionResult<TenantDetailResponse>> Suspend(Guid id, SuspendTenantRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) return BadRequest(new { error = "A reason is required (max 500 characters)." });
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });
        if (tenant.Status != TenantStatus.Active) return Conflict(new { error = $"Institution is '{tenant.Status}', not active." });

        tenant.Status = TenantStatus.Suspended;
        tenant.SuspendedAt = DateTimeOffset.UtcNow;
        tenant.SuspensionReason = reason;
        await _platformDb.SaveChangesAsync(ct);
        return Ok(await DetailAsync(tenant, ct));
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<ActionResult<TenantDetailResponse>> Reactivate(Guid id, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });
        if (tenant.Status != TenantStatus.Suspended) return Conflict(new { error = $"Institution is '{tenant.Status}', not suspended." });

        tenant.Status = TenantStatus.Active;
        tenant.SuspendedAt = null;
        tenant.SuspensionReason = null;
        await _platformDb.SaveChangesAsync(ct);
        return Ok(await DetailAsync(tenant, ct));
    }

    // Adds an Institution Admin (only Ahamsys can). They choose their own
    // password at first sign-in.
    [HttpPost("{id:guid}/admins")]
    public async Task<ActionResult<TenantUserDetail>> AddAdmin(Guid id, AddInstitutionAdminRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        var name = request.FullName?.Trim() ?? "";
        if (email.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(email, out _)) return BadRequest(new { error = "A valid email is required." });
        if (name.Length is 0 or > 200) return BadRequest(new { error = "A name is required (max 200 characters)." });
        if (PasswordPolicy.Validate(request.InitialPassword, email) is { } passwordError) return BadRequest(new { error = passwordError });

        var db = await OpenTenantAsync(id, ct);
        if (db is null) return NotFound(new { error = "Institution not found." });
        await using (db)
        {
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return Conflict(new { error = "Someone in this institution already has that email. Give them the Institution Admin role instead." });
            var role = await db.Roles.FirstAsync(r => r.Name == SystemRole.InstitutionAdmin, ct);
            var user = new ApplicationUser
            {
                Email = email, FullName = name, IsActive = true, MustChangePassword = true,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.InitialPassword),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            TenantUserService.Audit(db, PlatformActor(), "AdminAdded", $"{name} ({email})");
            await db.SaveChangesAsync(ct);
            return Ok(await _users.GetAsync(db, user.Id, ct));
        }
    }

    [HttpGet("{id:guid}/users")]
    public async Task<ActionResult<IEnumerable<TenantUserDetail>>> ListUsers(Guid id, CancellationToken ct)
    {
        var db = await OpenTenantAsync(id, ct);
        if (db is null) return NotFound(new { error = "Institution not found." });
        await using (db) return Ok(await _users.ListAsync(db, ct));
    }

    [HttpPut("{id:guid}/users/{userId:guid}")]
    public async Task<ActionResult<TenantUserDetail>> UpdateUser(Guid id, Guid userId, UpdateUserRequest request, CancellationToken ct) =>
        await WithTenantAsync(id, db => _users.UpdateAsync(db, userId, request.FullName, request.Email, request.Roles, PlatformActor(), ct), ct);

    [HttpPost("{id:guid}/users/{userId:guid}/deactivate")]
    public async Task<ActionResult<TenantUserDetail>> DeactivateUser(Guid id, Guid userId, CancellationToken ct) =>
        await WithTenantAsync(id, db => _users.SetActiveAsync(db, userId, false, PlatformActor(), ct), ct);

    [HttpPost("{id:guid}/users/{userId:guid}/reactivate")]
    public async Task<ActionResult<TenantUserDetail>> ReactivateUser(Guid id, Guid userId, CancellationToken ct) =>
        await WithTenantAsync(id, db => _users.SetActiveAsync(db, userId, true, PlatformActor(), ct), ct);

    // For support, e.g. an institution whose only admin is locked out.
    [HttpPost("{id:guid}/users/{userId:guid}/reset-password")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetUserPassword(Guid id, Guid userId, CancellationToken ct)
    {
        var db = await OpenTenantAsync(id, ct);
        if (db is null) return NotFound(new { error = "Institution not found." });
        await using (db)
        {
            var (result, temporary) = await _users.ResetPasswordAsync(db, userId, PlatformActor(), ct);
            return result.User is null ? StatusCode(result.Status, new { error = result.Error }) : Ok(new ResetPasswordResponse(result.User, temporary!));
        }
    }

    // Ahamsys's actions appear in the institution's audit log under this name.
    private TenantUserService.Actor PlatformActor() =>
        new(null, $"Ahamsys ({User.FindFirstValue(System.Security.Claims.ClaimTypes.Email) ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "platform"})");

    private async Task<TenantDbContext?> OpenTenantAsync(Guid id, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return tenant is null ? null : _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
    }

    private async Task<ActionResult<TenantUserDetail>> WithTenantAsync(Guid id, Func<TenantDbContext, Task<UserOperationResult>> action, CancellationToken ct)
    {
        var db = await OpenTenantAsync(id, ct);
        if (db is null) return NotFound(new { error = "Institution not found." });
        await using (db)
        {
            var result = await action(db);
            return result.User is null ? StatusCode(result.Status, new { error = result.Error }) : Ok(result.User);
        }
    }

    private async Task<TenantDetailResponse> DetailAsync(Tenant tenant, CancellationToken ct)
    {
        await using var db = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var users = await db.Users.Where(u => !u.IsServiceAccount).GroupBy(u => u.IsActive).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var credentials = await db.Credentials.CountAsync(c => c.Status == CredentialStatus.Active, ct);
        var apiKeys = await db.ApiClients.CountAsync(k => k.RevokedAt == null, ct);
        return new TenantDetailResponse(
            tenant.Id, tenant.Name, tenant.Slug, tenant.Status.ToString(), tenant.CreatedAt, tenant.ActivatedAt, tenant.SuspendedAt, tenant.SuspensionReason,
            TenantProfileResponse.From(tenant),
            users.Sum(u => u.Count), users.FirstOrDefault(u => u.Key)?.Count ?? 0, credentials, apiKeys);
    }
}

