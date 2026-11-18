using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Certyfi.Api.Controllers;

// Tenant-scoped, InstitutionAdmin-only. This app has no cloud object storage
// (S3/Blob/etc.) — files land on local disk under wwwroot/uploads/{tenant
// schema}/, which Program.cs serves back out via UseStaticFiles ahead of
// TenantResolutionMiddleware (a plain <img src> request can't carry the
// X-Tenant-Slug header the middleware needs, so serving must bypass it
// entirely — see Program.cs). This is the pragmatic, no-new-infrastructure
// answer to "import a professionally designed certificate" via direct file
// upload, alongside the pre-existing paste-a-hosted-URL path
// (CertificateTemplate.BackgroundImageUrl accepts either — this endpoint
// just hands back a URL of its own that satisfies the same http(s)-only
// validation). Known limitation, acceptable for this app's current scope:
// local disk doesn't survive a redeploy or work across multiple app
// instances — a real cloud storage backend is the natural next step if
// this ever needs to run that way.
[ApiController]
[Route("api/v1/uploads")]
[Authorize(Roles = SystemRole.InstitutionAdmin)]
public class UploadsController : ControllerBase
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private readonly IWebHostEnvironment _env;
    private readonly ITenantContext _tenantContext;

    public UploadsController(IWebHostEnvironment env, ITenantContext tenantContext)
    {
        _env = env;
        _tenantContext = tenantContext;
    }

    // Template backgrounds and student portal banners.
    [HttpPost("background-image")]
    [HttpPost("image")]
    [RequestSizeLimit(MaxFileSizeBytes + 1024)]
    public async Task<ActionResult<UploadResponse>> UploadBackgroundImage(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file was uploaded." });
        }
        if (file.Length > MaxFileSizeBytes)
        {
            return BadRequest(new { error = "Image must be 5MB or smaller." });
        }
        if (!AllowedContentTypes.TryGetValue(file.ContentType, out var extension))
        {
            return BadRequest(new { error = "Only PNG, JPEG, WEBP, or GIF images are allowed." });
        }

        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        // SchemaName is a server-derived, already-validated identifier
        // (TenantProvisioningService's SlugPattern check at provisioning
        // time) — safe to use directly as a directory segment.
        var tenantDir = Path.Combine(webRoot, "uploads", _tenantContext.SchemaName);
        Directory.CreateDirectory(tenantDir);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        await using (var stream = System.IO.File.Create(Path.Combine(tenantDir, fileName)))
        {
            await file.CopyToAsync(stream, ct);
        }

        var url = $"{Request.Scheme}://{Request.Host}/uploads/{_tenantContext.SchemaName}/{fileName}";
        return Ok(new UploadResponse(url));
    }
}
