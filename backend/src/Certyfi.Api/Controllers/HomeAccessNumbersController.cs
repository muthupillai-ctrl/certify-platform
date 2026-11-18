using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// The platform operator's list of mobile numbers allowed to open the private
// home page (see AccessController).
[ApiController]
[Route("api/v1/platform/home-access")]
[Authorize(Roles = SystemRole.SuperAdmin)]
public class HomeAccessNumbersController : ControllerBase
{
    private readonly PlatformDbContext _platformDb;

    public HomeAccessNumbersController(PlatformDbContext platformDb)
    {
        _platformDb = platformDb;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<HomeAccessNumberResponse>>> List(CancellationToken ct) =>
        Ok(await _platformDb.HomeAccessNumbers.AsNoTracking().OrderBy(n => n.Label).ThenBy(n => n.Number)
            .Select(n => new HomeAccessNumberResponse(n.Id, n.Number, n.Label, n.CreatedAt, n.LastUsedAt)).ToListAsync(ct));

    [HttpPost]
    public async Task<ActionResult<HomeAccessNumberResponse>> Add(AddHomeAccessNumberRequest request, CancellationToken ct)
    {
        var digits = HomeAccessPass.Normalize(request.Number);
        if (digits is null) return BadRequest(new { error = "Enter a mobile number of 10 to 16 digits (spaces, + and - are fine)." });
        var label = request.Label?.Trim() ?? "";
        if (label.Length is 0 or > 100) return BadRequest(new { error = "Add a name (up to 100 characters) so you know whose number it is." });
        if (await _platformDb.HomeAccessNumbers.AnyAsync(n => n.Number == digits, ct)) return Conflict(new { error = "That number is already on the list." });

        var number = new HomeAccessNumber { Number = digits, Label = label };
        _platformDb.HomeAccessNumbers.Add(number);
        await _platformDb.SaveChangesAsync(ct);
        return Ok(new HomeAccessNumberResponse(number.Id, number.Number, number.Label, number.CreatedAt, null));
    }

    // Removing a number blocks that person at their next visit (their pass is
    // checked against the list every time).
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _platformDb.HomeAccessNumbers.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? NotFound() : NoContent();
    }
}
