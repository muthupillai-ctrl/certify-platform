using Certyfi.Domain.Enums;

namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. A reusable starting point for creating
// Achievements — picking a template pre-fills the achievement's field
// schema and tags it with the template's Kind, which decides which
// certificate layout the public verify page renders. Achievements COPY a
// template's schema/kind at creation time rather than holding a live
// reference (see Achievement.TemplateKind) — editing or deactivating a
// template later must never change how an already-created achievement (or
// credentials issued against it) looks, the same "snapshot, not live
// reference" philosophy already used for Credential.FieldValuesJson. Never
// hard-deleted once created — retire via IsActive, matching Achievement's
// own convention.
public class CertificateTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = default!;
    public string? Description { get; set; }

    public TemplateKind Kind { get; set; }

    // Same shape/validation rules as Achievement.FieldSchemaJson — see
    // FieldSchemaValidator. Default "[]" — a template need not define any
    // custom fields.
    public string FieldSchemaJson { get; set; } = "[]";

    // The certificate's own visual design — what used to be a hardcoded
    // heading per Kind ("Certificate of Achievement" / "Official
    // Transcript") is now template-owned text, plus the position/size/
    // color/alignment of every element on the certificate (LayoutJson —
    // see LayoutValidator for its exact shape). TitleText defaults to Name
    // and LayoutJson to a Kind-appropriate generated default when a create/
    // update request omits them (CertificateTemplatesController), so
    // callers that only ever set Name/Description/Kind/FieldSchemaJson
    // (e.g. existing tests, or a future raw API caller) keep working.
    public string TitleText { get; set; } = default!;
    public string? SubtitleText { get; set; }
    public string? AccentColor { get; set; }
    public string? FontFamily { get; set; }
    public string LayoutJson { get; set; } = default!;

    // An externally-hosted (http/https) image used as the certificate's
    // backdrop — the "import a professionally designed certificate" path:
    // a tenant hosts a designer-made background wherever they like (their
    // own site, S3, Cloudinary, ...) and pastes the URL here, then places
    // the usual layout elements (recipient name, QR, fields, ...) on top of
    // it via the canvas editor. Same URL-only, no-upload approach already
    // used for Tenant.BrandingLogoUrl — this app has no file storage of its
    // own yet.
    public string? BackgroundImageUrl { get; set; }

    public bool IsActive { get; set; } = true;

    // One to three signatories (see Signatory), in the order they're printed.
    // Read live when a certificate is created, unlike the design above,
    // which is copied onto the achievement: a new principal applies to every
    // certificate made from then on.
    public List<Guid> SignatoryIds { get; set; } = new();

    // Certificates from this template need a third person: after an
    // Approver, an Institution admin gives final approval before it's
    // issued. Switching it on is immediate; switching it off needs a second
    // admin to confirm (ControlRequest).
    public bool RequiresFinalApproval { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
