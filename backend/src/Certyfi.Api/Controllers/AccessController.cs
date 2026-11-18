using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// The private home page's gate. A visitor types a mobile number; if it's on
// the platform operator's list they get a 30-day pass (cookie). Only the home
// page and the presentation are gated (by the reverse proxy calling GET
// here); verification, logins and the Partner API are not.
//
// A number is matched as typed, or by its last 10 digits, so "+234 706 289
// 1305" and "0706 289 1305" both match 2347062891305. There's no SMS code:
// it works like a shared password, which the rate limit keeps from being
// guessed quickly.
[ApiController]
[Route("api/v1/access")]
[AllowAnonymous]
public class AccessController : ControllerBase
{
    private const string NotAllowed = "This number isn't on the list for this preview. Contact info@ahamsys.com or +234 706 289 1305 for access.";

    private readonly PlatformDbContext _platformDb;
    private readonly HomeAccessPass _pass;

    public AccessController(PlatformDbContext platformDb, HomeAccessPass pass)
    {
        _platformDb = platformDb;
        _pass = pass;
    }

    [HttpPost]
    [EnableRateLimiting(HomeAccessRateLimit.PolicyName)]
    public async Task<IActionResult> Enter(EnterHomeAccessRequest request, CancellationToken ct)
    {
        var digits = HomeAccessPass.Normalize(request.Mobile);
        if (digits is null) return BadRequest(new { error = "Enter a mobile number of 10 to 16 digits." });

        var numbers = await _platformDb.HomeAccessNumbers.ToListAsync(ct);
        var match = numbers.FirstOrDefault(n => n.Number == digits) ??
            numbers.FirstOrDefault(n => n.Number.Length >= 10 && n.Number[^10..] == digits[^10..]);
        if (match is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = NotAllowed });

        var now = DateTimeOffset.UtcNow;
        match.LastUsedAt = now;
        await _platformDb.SaveChangesAsync(ct);
        Response.Cookies.Append(HomeAccessPass.CookieName, _pass.Issue(match.Id, now), new CookieOptions
        {
            HttpOnly = true,
            // Behind Cloudflare the site is https-only; locally it's http.
            Secure = Request.IsHttps || Request.Headers["X-Forwarded-Proto"] == "https",
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = HomeAccessPass.Lifetime,
        });
        return NoContent();
    }

    // 204 with a valid pass for a number still on the list, else 401. Used
    // by the reverse proxy (auth_request) and the app; never cached.
    [HttpGet]
    public async Task<IActionResult> Check(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var id = _pass.Read(Request.Cookies[HomeAccessPass.CookieName], DateTimeOffset.UtcNow);
        if (id is null || !await _platformDb.HomeAccessNumbers.AnyAsync(n => n.Id == id, ct)) return Unauthorized();
        return NoContent();
    }
}
