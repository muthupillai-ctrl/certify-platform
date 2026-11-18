using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Institution Admins manage the banners shown beside the student portal
// (see PortalBanner). Images come from UploadsController.
[ApiController]
[Route("api/v1/portal-banners")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class PortalBannersController : ControllerBase
{
    public const int MaxPerSide = 6;
    private readonly TenantDbContext _db;

    public PortalBannersController(TenantDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PortalBannerResponse>>> List(CancellationToken ct) =>
        Ok((await _db.PortalBanners.AsNoTracking().OrderBy(b => b.Side).ThenBy(b => b.SortOrder).ToListAsync(ct)).Select(ToResponse));

    [HttpPost]
    public async Task<ActionResult<PortalBannerResponse>> Create(SavePortalBannerRequest request, CancellationToken ct)
    {
        if (Validate(request, out var side) is { } error) return BadRequest(new { error });
        if (await _db.PortalBanners.CountAsync(b => b.Side == side, ct) >= MaxPerSide)
        {
            return Conflict(new { error = $"Up to {MaxPerSide} banners per side. Remove one first." });
        }
        var banner = new PortalBanner
        {
            Side = side,
            SortOrder = (await _db.PortalBanners.Where(b => b.Side == side).MaxAsync(b => (int?)b.SortOrder, ct) ?? -1) + 1,
        };
        Apply(banner, request, side);
        _db.PortalBanners.Add(banner);
        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(banner));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PortalBannerResponse>> Update(Guid id, SavePortalBannerRequest request, CancellationToken ct)
    {
        var banner = await _db.PortalBanners.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (banner is null) return NotFound();
        if (Validate(request, out var side) is { } error) return BadRequest(new { error });
        if (side != banner.Side)
        {
            if (await _db.PortalBanners.CountAsync(b => b.Side == side, ct) >= MaxPerSide)
            {
                return Conflict(new { error = $"Up to {MaxPerSide} banners per side. Remove one from that side first." });
            }
            banner.SortOrder = (await _db.PortalBanners.Where(b => b.Side == side).MaxAsync(b => (int?)b.SortOrder, ct) ?? -1) + 1;
        }
        Apply(banner, request, side);
        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(banner));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _db.PortalBanners.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    // The full order of each side, top to bottom.
    [HttpPut("order")]
    public async Task<ActionResult<IEnumerable<PortalBannerResponse>>> Reorder(ReorderPortalBannersRequest request, CancellationToken ct)
    {
        var banners = await _db.PortalBanners.ToListAsync(ct);
        foreach (var (ids, side) in new[] { (request.Left, PortalBannerSide.Left), (request.Right, PortalBannerSide.Right) })
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var banner = banners.FirstOrDefault(b => b.Id == ids[i]);
                if (banner is null) continue;
                banner.Side = side;
                banner.SortOrder = i;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(banners.OrderBy(b => b.Side).ThenBy(b => b.SortOrder).Select(ToResponse));
    }

    private static string? Validate(SavePortalBannerRequest r, out PortalBannerSide side)
    {
        side = default;
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 120) return "Give the banner a title (up to 120 characters); it's read out to screen readers.";
        if (!Enum.TryParse(r.Side, ignoreCase: true, out side)) return "Side must be Left or Right.";
        if (!IsHttpUrl(r.ImageUrl)) return "Upload an image for the banner.";
        if (!string.IsNullOrWhiteSpace(r.LinkUrl) && !IsHttpUrl(r.LinkUrl)) return "The link must be a full web address starting with https://.";
        if (r.StartsOn is { } s && r.EndsOn is { } e && e < s) return "The end date is before the start date.";
        return null;
    }

    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= 500 && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static void Apply(PortalBanner b, SavePortalBannerRequest r, PortalBannerSide side)
    {
        b.Title = r.Title.Trim();
        b.ImageUrl = r.ImageUrl.Trim();
        b.LinkUrl = string.IsNullOrWhiteSpace(r.LinkUrl) ? null : r.LinkUrl.Trim();
        b.Side = side;
        b.StartsOn = r.StartsOn;
        b.EndsOn = r.EndsOn;
        b.IsActive = r.IsActive;
    }

    private static PortalBannerResponse ToResponse(PortalBanner b) =>
        new(b.Id, b.Title, b.ImageUrl, b.LinkUrl, b.Side.ToString(), b.SortOrder, b.StartsOn, b.EndsOn, b.IsActive, b.ClickCount, b.CreatedAt);
}
