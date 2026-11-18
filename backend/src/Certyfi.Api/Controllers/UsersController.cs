using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Certyfi.Infrastructure.Auth;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Tenant-scoped. An InstitutionAdmin manages users within their own
// institution only — TenantClaimGuardMiddleware has already confirmed the
// caller's token tenant matches the resolved request tenant by the time
// these actions run.
[ApiController]
[Route("api/v1/users")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class UsersController : ControllerBase
{
    private readonly TenantDbContext _db;
    private readonly TenantUserService _users;

    public UsersController(TenantDbContext db, TenantUserService users)
    {
        _db = db;
        _users = users;
    }

    // Service accounts belong to API keys and are managed on the API keys
    // page, not here (TenantUserService leaves them out).
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantUserDetail>>> List(CancellationToken ct) =>
        Ok(await _users.ListAsync(_db, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TenantUserDetail>> Get(Guid id, CancellationToken ct)
    {
        var user = await _users.GetAsync(_db, id, ct);
        return user is null ? NotFound(new { error = "User not found." }) : Ok(user);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request)
    {
        if (!SystemRole.TenantAssignable.Contains(request.Role))
        {
            return BadRequest(new { error = $"Unknown role '{request.Role}'." });
        }
        if (request.Role == SystemRole.InstitutionAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = TenantUserService.AdminsByPlatformOnly });
        }
        var email = request.Email.Trim().ToLowerInvariant();
        if (PasswordPolicy.Validate(request.InitialPassword, email) is { } passwordError)
        {
            return BadRequest(new { error = passwordError });
        }
        if (await _db.Users.AnyAsync(u => u.Email == email))
        {
            return Conflict(new { error = "A user with that email address already exists in this institution." });
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == request.Role);
        if (role is null)
        {
            return BadRequest(new { error = $"Role '{request.Role}' is not seeded for this tenant." });
        }

        var user = new ApplicationUser
        {
            Email = email,
            FullName = request.FullName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.InitialPassword),
            IsActive = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        TenantUserService.Audit(_db, await ActorAsync(), "UserAdded", $"{user.FullName} ({user.Email})", role.Name);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = user.Id }, new UserResponse(user.Id, user.Email, user.FullName, user.IsActive, new[] { role.Name }));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TenantUserDetail>> Update(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        ToAction(await _users.UpdateAsync(_db, id, request.FullName, request.Email, request.Roles, await ActorAsync(), ct));

    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<TenantUserDetail>> Deactivate(Guid id, CancellationToken ct) =>
        ToAction(await _users.SetActiveAsync(_db, id, false, await ActorAsync(), ct));

    [HttpPost("{id:guid}/reactivate")]
    public async Task<ActionResult<TenantUserDetail>> Reactivate(Guid id, CancellationToken ct) =>
        ToAction(await _users.SetActiveAsync(_db, id, true, await ActorAsync(), ct));

    // Returns the temporary password exactly once; the user must replace it
    // at their next sign-in.
    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword(Guid id, CancellationToken ct)
    {
        var (result, temporary) = await _users.ResetPasswordAsync(_db, id, await ActorAsync(), ct);
        return result.User is null ? StatusCode(result.Status, new { error = result.Error }) : Ok(new ResetPasswordResponse(result.User, temporary!));
    }

    private ActionResult<TenantUserDetail> ToAction(UserOperationResult result) =>
        result.User is null ? StatusCode(result.Status, new { error = result.Error }) : Ok(result.User);

    private async Task<TenantUserService.Actor> ActorAsync()
    {
        var id = CurrentUserId();
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync() ?? "Unknown";
        return new TenantUserService.Actor(id, name);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
