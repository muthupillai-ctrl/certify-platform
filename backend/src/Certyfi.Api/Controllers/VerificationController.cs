using Certyfi.Api.Auth;
using Certyfi.Domain.Entities;
using Certyfi.Domain.Enums;
using Certyfi.Infrastructure.Persistence;
using Certyfi.Infrastructure.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Api.Controllers;

// Public, anonymous, no login — capability note 4.5.4. Takes the tenant
// slug from the URL path (not X-Tenant-Slug/subdomain, since an anonymous
// verifier has no way to know to send that) and resolves the tenant itself
// via PlatformDbContext, exactly like TenantResolutionMiddleware does for a
// normal request — this path is exempted from that middleware (see
// TenantResolutionMiddleware.ExemptPrefixes) precisely so it can do this.
[ApiController]
[Route("api/v1/verify")]
public class VerificationController : ControllerBase
{
    // Sent by the Angular verify page so its requests are recorded as
    // "VerifyPage" in the verification history rather than "Api".
    public const string ClientHeader = "X-Certyfi-Client";

    private readonly PlatformDbContext _platformDb;
    private readonly TenantDbContextFactory _tenantDbFactory;
    private readonly OfflineProofSigner _offlineProofs;
    private readonly OpenBadgeCredentialIssuer _openBadges;
    private readonly IConfiguration _configuration;

    public VerificationController(
        PlatformDbContext platformDb, TenantDbContextFactory tenantDbFactory, OfflineProofSigner offlineProofs,
        OpenBadgeCredentialIssuer openBadges, IConfiguration configuration)
    {
        _platformDb = platformDb;
        _tenantDbFactory = tenantDbFactory;
        _offlineProofs = offlineProofs;
        _openBadges = openBadges;
        _configuration = configuration;
    }

    [HttpGet("{tenantSlug}/{credentialId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<VerifyCredentialResponse>> Verify(string tenantSlug, Guid credentialId, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug.Trim().ToLowerInvariant() && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended), ct);
        if (tenant is null) return NotFound(new { error = "Credential not found." });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);

        // PendingApproval/Rejected never resolve — same 404 as a genuinely
        // nonexistent id, so this endpoint can't be used as a workflow-state
        // oracle for still-pending drafts.
        var credential = await tenantDb.Credentials.AsNoTracking()
            .Include(c => c.Achievement)
            .FirstOrDefaultAsync(c => c.Id == credentialId &&
                (c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Suspended || c.Status == CredentialStatus.Revoked)
                && c.PersonalDataErasedAt == null, ct);
        if (credential is null) return NotFound(new { error = "Credential not found." });

        // "Expired" is never stored — computed live, matching the note's
        // "status resolved live at verification time... never cached in the
        // credential artefact itself."
        var effectiveStatus = credential.Status == CredentialStatus.Active
            && credential.ExpiresOn is { } exp && exp < DateTimeOffset.UtcNow
            ? "Expired"
            : credential.Status.ToString();

        // Credentials approved before offline proofs existed get one the
        // first time they're verified, so their QR codes gain it too.
        var offlineProof = credential.OfflineProof;
        if (offlineProof is null && credential.SignatureKeyId is not null)
        {
            offlineProof = await _offlineProofs.CreateAsync(tenant.Id, tenant.Slug, credential, credential.Achievement.Title, credential.SignatureKeyId, ct);
            await tenantDb.Credentials.Where(c => c.Id == credential.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.OfflineProof, offlineProof), ct);
        }

        var channel = Request.Headers[ClientHeader] == "verify-page" ? VerificationChannel.VerifyPage : VerificationChannel.Api;
        await RecordAsync(tenantDb, credential.Id, channel, effectiveStatus, ct);

        // Looked up from the key itself rather than stored on Credential —
        // see VerifyCredentialResponse's own comment on why: a verifier
        // must know exactly which algorithm to use, even for an old
        // credential signed before a tenant's active key algorithm changed.
        string? signatureAlgorithm = null;
        if (credential.SignatureKeyId is not null)
        {
            signatureAlgorithm = await _platformDb.TenantSigningKeys.AsNoTracking()
                .Where(k => k.TenantId == tenant.Id && k.KeyId == credential.SignatureKeyId)
                .Select(k => k.Algorithm)
                .FirstOrDefaultAsync(ct);
        }

        // Deliberately excludes RecipientEmail, RejectionReason,
        // SuspensionReason, RevocationReason — the entity keeps them, this
        // DTO is the privacy boundary (matches the note's "privacy-filtered"
        // recipient framing and the originally-approved public field list).
        // FieldSchemaJson/FieldValuesJson ARE included — custom fields (roll
        // number, marks tables, etc.) are exactly what a certificate/marks
        // card is meant to publicly display when verified, same as a real
        // physical document would show an employer.
        var coveringCheckpoint = await FindCoveringCheckpointAsync(tenantDb, credentialId, ct);
        var anchorStatus = coveringCheckpoint?.Status ?? "NotYetCheckpointed";

        return Ok(new VerifyCredentialResponse(
            credential.Id, credential.RecipientName, credential.Achievement.Title, credential.Achievement.Description,
            tenant.Name, tenant.BrandingLogoUrl, tenant.BrandingPrimaryColor,
            effectiveStatus, credential.IssuedAt, credential.ExpiresOn,
            // FieldSchemaJson comes from the credential's own snapshot, NOT
            // credential.Achievement.FieldSchemaJson — amending an
            // achievement's fields later must never change how an
            // already-issued credential renders (see Credential.FieldSchemaJson).
            credential.FieldSchemaJson, credential.FieldValuesJson, credential.Achievement.TemplateKind.ToString(),
            credential.Achievement.TemplateTitleText, credential.Achievement.TemplateSubtitleText,
            credential.Achievement.TemplateAccentColor, credential.Achievement.TemplateFontFamily, credential.Achievement.TemplateLayoutJson,
            credential.Achievement.TemplateBackgroundImageUrl,
            credential.CanonicalHash, credential.Signature, credential.SignatureKeyId, signatureAlgorithm,
            anchorStatus, coveringCheckpoint?.BitcoinBlockHeight, coveringCheckpoint?.MerkleRootVerified ?? false,
            VerificationCodes.Format(credential.VerificationCode), offlineProof, PublicInstitutionProfile.From(tenant),
            credential.OriginalIssueDate, credential.LegacyCertificateNumber,
            SignatoryName.FromSnapshot(credential.SignatoriesJson)));
    }

    // Public, anonymous — returns the raw downloadable `.ots` proof file
    // covering this credential's ledger entries, verifiable with the
    // official `ots` CLI or any standard OpenTimestamps tool, entirely
    // independent of Certyfi. 404 until the tenant's next hourly checkpoint
    // (LedgerAnchorBackgroundService) has run past this credential's latest
    // ledger entry.
    [HttpGet("{tenantSlug}/{credentialId:guid}/anchor-proof")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAnchorProof(string tenantSlug, Guid credentialId, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug.Trim().ToLowerInvariant() && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended), ct);
        if (tenant is null) return NotFound(new { error = "Anchor proof not found." });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);

        var exists = await tenantDb.Credentials.AsNoTracking().AnyAsync(c => c.Id == credentialId, ct);
        if (!exists) return NotFound(new { error = "Anchor proof not found." });

        var checkpoint = await FindCoveringCheckpointAsync(tenantDb, credentialId, ct);
        if (checkpoint is null) return NotFound(new { error = "This credential has not been checkpointed yet." });

        return File(checkpoint.OtsProof, "application/octet-stream", $"{credentialId}.ots");
    }

    // The checkpoint whose OtsProof is the FIRST one (earliest
    // UpToSequenceNumber) to have covered this credential's latest ledger
    // entry — checkpoints only grow UpToSequenceNumber over time, so any
    // checkpoint with UpToSequenceNumber >= the credential's own latest
    // entry already commits to it; the earliest such checkpoint is the one
    // whose Status/proof is most relevant to show.
    private static async Task<LedgerCheckpoint?> FindCoveringCheckpointAsync(TenantDbContext tenantDb, Guid credentialId, CancellationToken ct)
    {
        var credentialMaxSequence = await tenantDb.LedgerEntries.AsNoTracking()
            .Where(e => e.CredentialId == credentialId)
            .OrderByDescending(e => e.SequenceNumber)
            .Select(e => (long?)e.SequenceNumber)
            .FirstOrDefaultAsync(ct);
        if (credentialMaxSequence is null) return null;

        return await tenantDb.LedgerCheckpoints.AsNoTracking()
            .Where(c => c.UpToSequenceNumber >= credentialMaxSequence)
            .OrderBy(c => c.UpToSequenceNumber)
            .FirstOrDefaultAsync(ct);
    }

    // Public, anonymous — the whole point of real signing is that a
    // verifier doesn't have to trust THIS endpoint's own Verify response;
    // they can fetch the tenant's public key here and check
    // VerifyCredentialResponse.Signature themselves: SHA-256 the same
    // canonical payload the platform hashed (documented alongside
    // CredentialsController.ComputeCanonicalHash) and verify it against
    // CanonicalHash's bytes with this key, independent of Certyfi entirely.
    [HttpGet("{tenantSlug}/signing-keys/{keyId}")]
    [AllowAnonymous]
    public async Task<ActionResult<TenantSigningPublicKeyResponse>> GetSigningPublicKey(string tenantSlug, string keyId, CancellationToken ct)
    {
        var tenant = await _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug.Trim().ToLowerInvariant() && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended), ct);
        if (tenant is null) return NotFound(new { error = "Signing key not found." });

        var key = await _platformDb.TenantSigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenant.Id && k.KeyId == keyId, ct);
        if (key is null) return NotFound(new { error = "Signing key not found." });

        return Ok(new TenantSigningPublicKeyResponse(key.KeyId, key.Algorithm, key.PublicKeyPem));
    }

    // Look up a certificate by the code printed on it, for a verifier who
    // has the paper but not the link. Rate-limited per client IP (random
    // 12-character codes can't be guessed, but this keeps anyone from
    // trying). Returns only the id to open the normal verify page with.
    [HttpGet("{tenantSlug}/lookup/{code}")]
    [AllowAnonymous]
    [EnableRateLimiting(VerificationLookupRateLimit.PolicyName)]
    public async Task<ActionResult<VerificationLookupResponse>> Lookup(string tenantSlug, string code, CancellationToken ct)
    {
        const string notFound = "No certificate matches that institution and code.";
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = notFound });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var normalized = VerificationCodes.Normalize(code);
        var credentialId = normalized is null ? null : await tenantDb.Credentials.AsNoTracking()
            .Where(c => c.VerificationCode == normalized &&
                (c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Suspended || c.Status == CredentialStatus.Revoked)
                && c.PersonalDataErasedAt == null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        await RecordAsync(tenantDb, credentialId, VerificationChannel.CodeLookup, credentialId is null ? "NotFound" : "Found", ct);
        return credentialId is { } id
            ? Ok(new VerificationLookupResponse(tenant.Slug, id))
            : NotFound(new { error = notFound });
    }

    // Look up a digitised paper certificate by the number printed on the
    // paper. Old numbers are often sequential and easy to guess, so the
    // recipient's name as printed is required too: every word of three or
    // more letters in the name on record must be given (initials optional),
    // and nothing that isn't in it. Rate-limited like the code look-up.
    [HttpGet("{tenantSlug}/lookup-paper")]
    [AllowAnonymous]
    [EnableRateLimiting(VerificationLookupRateLimit.PolicyName)]
    public async Task<ActionResult<VerificationLookupResponse>> LookupPaper(string tenantSlug, [FromQuery] string? number, [FromQuery] string? name, CancellationToken ct)
    {
        const string notFound = "No certificate matches that institution, number and name.";
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null || string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name) || number.Length > 100 || name.Length > 200)
            return NotFound(new { error = notFound });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var key = new string(number.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        var match = await tenantDb.Credentials.AsNoTracking()
            .Where(c => c.LegacyNumberKey == key &&
                (c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Suspended || c.Status == CredentialStatus.Revoked)
                && c.PersonalDataErasedAt == null)
            .Select(c => new { c.Id, c.RecipientName })
            .FirstOrDefaultAsync(ct);
        var credentialId = match is not null && NameMatches(name, match.RecipientName) ? match.Id : (Guid?)null;

        await RecordAsync(tenantDb, credentialId, VerificationChannel.CodeLookup, credentialId is null ? "NotFound" : "Found", ct);
        return credentialId is { } id
            ? Ok(new VerificationLookupResponse(tenant.Slug, id))
            : NotFound(new { error = notFound });
    }

    internal static bool NameMatches(string typed, string onRecord)
    {
        static string[] Words(string value) => new string(value.ToLowerInvariant().Select(c => char.IsLetter(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var given = Words(typed).ToHashSet();
        var record = Words(onRecord);
        var required = record.Where(w => w.Length >= 3).ToList();
        return given.Count > 0 && required.Count > 0 && required.All(given.Contains) && given.All(record.Contains);
    }

    // The recipient's portable credential: Open Badges 3.0, as a signed JWT
    // file. Only while the certificate is Active and not expired — a
    // suspended or revoked certificate must not produce a fresh credential.
    [HttpGet("{tenantSlug}/{credentialId:guid}/credential")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadCredential(string tenantSlug, Guid credentialId, CancellationToken ct)
    {
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Credential not found." });

        await using var tenantDb = _tenantDbFactory.CreateForSchema(tenant.Id, tenant.Slug, tenant.SchemaName);
        var credential = await tenantDb.Credentials.AsNoTracking()
            .Include(c => c.Achievement)
            .FirstOrDefaultAsync(c => c.Id == credentialId &&
                (c.Status == CredentialStatus.Active || c.Status == CredentialStatus.Suspended || c.Status == CredentialStatus.Revoked)
                && c.PersonalDataErasedAt == null, ct);
        if (credential is null) return NotFound(new { error = "Credential not found." });

        var expired = credential.ExpiresOn is { } exp && exp < DateTimeOffset.UtcNow;
        if (credential.Status != CredentialStatus.Active || expired)
        {
            return Conflict(new { error = "A digital credential can only be downloaded while the certificate is active." });
        }
        if (credential.SignatureKeyId is null)
        {
            // Approved before Certyfi signed certificates; there's no
            // institution signature to carry into a portable credential.
            return Conflict(new { error = "This certificate was issued before digital signing was available, so it has no digital credential." });
        }

        var jwt = await _openBadges.IssueAsync(ApiBaseUrl(), SiteBaseUrl(), tenant, credential, ct);
        await RecordAsync(tenantDb, credential.Id, VerificationChannel.CredentialDownload, "Active", ct);
        return File(System.Text.Encoding.ASCII.GetBytes(jwt), "application/vc+ld+json+jwt",
            $"certificate-{VerificationCodes.Format(credential.VerificationCode)}.jwt");
    }

    // Every key the institution has signed with, as a JWK Set — what the
    // verify page caches to check QR codes and credential files offline.
    [HttpGet("{tenantSlug}/jwks")]
    [AllowAnonymous]
    public async Task<IActionResult> GetJwks(string tenantSlug, CancellationToken ct)
    {
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });

        var keys = await _platformDb.TenantSigningKeys.AsNoTracking()
            .Where(k => k.TenantId == tenant.Id)
            .ToListAsync(ct);
        return Ok(new
        {
            institution = new { slug = tenant.Slug, name = tenant.Name },
            keys = keys.Select(k => Jose.PublicJwk(k.PublicKeyPem, k.KeyId)),
        });
    }

    // One key as a JWK: the `kid` of every Open Badges credential file.
    [HttpGet("{tenantSlug}/jwks/{keyId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetJwk(string tenantSlug, string keyId, CancellationToken ct)
    {
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Signing key not found." });

        var key = await _platformDb.TenantSigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenant.Id && k.KeyId == keyId, ct);
        return key is null
            ? NotFound(new { error = "Signing key not found." })
            : Ok(Jose.PublicJwk(key.PublicKeyPem, key.KeyId));
    }

    // Open Badges issuer profile: the credential file's `iss`.
    [HttpGet("{tenantSlug}/issuer")]
    [AllowAnonymous]
    public async Task<IActionResult> GetIssuerProfile(string tenantSlug, CancellationToken ct)
    {
        var tenant = await FindActiveTenantAsync(tenantSlug, ct);
        if (tenant is null) return NotFound(new { error = "Institution not found." });
        return Ok(OpenBadgeCredentialIssuer.IssuerProfile(ApiBaseUrl(), SiteBaseUrl(), tenant));
    }

    // "Active" for verification purposes: a suspended institution's
    // certificates remain verifiable (see Tenant.SuspendedAt).
    private Task<Tenant?> FindActiveTenantAsync(string tenantSlug, CancellationToken ct) =>
        _platformDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug.Trim().ToLowerInvariant() && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended), ct);

    // A failure to record history must never fail the verification itself.
    private static async Task RecordAsync(TenantDbContext tenantDb, Guid? credentialId, string channel, string result, CancellationToken ct)
    {
        try
        {
            tenantDb.VerificationEvents.Add(new VerificationEvent { CredentialId = credentialId, Channel = channel, Result = result });
            await tenantDb.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            tenantDb.ChangeTracker.Clear();
        }
    }

    // Where the public site (the verify page) lives, and where this API is
    // reached. The same host in production; different locally
    // (localhost:4200 vs localhost:5080), hence PublicApp:BaseUrl.
    private string SiteBaseUrl() => _configuration["PublicApp:BaseUrl"]?.TrimEnd('/') ?? ApiBaseUrl();

    private string ApiBaseUrl() => $"{Request.Scheme}://{Request.Host}";
}
