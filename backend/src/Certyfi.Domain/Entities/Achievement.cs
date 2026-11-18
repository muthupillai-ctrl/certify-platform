using Certyfi.Domain.Enums;

namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. A reusable template — Credentials
// reference an Achievement rather than carrying their own title/description,
// matching the capability note's Issuer+Achievement+Recipient+Credential
// model (4.5.1). Never deleted once a Credential references it; retire via
// IsActive instead.
public class Achievement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = default!;
    public string? Description { get; set; }
    public string? CriteriaNarrative { get; set; }

    // Months from issuance to expiry; null = never expires.
    public int? ValidityPeriodMonths { get; set; }

    public bool IsActive { get; set; } = true;

    // JSON array of field definitions this achievement's credentials
    // capture, beyond RecipientName/RecipientEmail — e.g. roll number,
    // grade, or (for a marks card) a repeating table of subjects:
    // [{ "key": "rollNumber", "label": "Roll Number", "type": "text" },
    //  { "key": "subjects", "label": "Subjects", "type": "table",
    //    "columns": [{ "key": "subject", "label": "Subject", "type": "text" }, ...] }]
    // type is text|number|date|table; a table field's columns are
    // themselves field definitions restricted to text|number|date (no
    // nested tables) — enforced server-side, see FieldSchemaValidator.
    // Default "[]" — every existing achievement stays valid with zero
    // custom fields.
    public string FieldSchemaJson { get; set; } = "[]";

    // The CertificateTemplate this achievement was created from, if any —
    // null for achievements created without picking a template, or from
    // before templates existed. Kept for traceability only; FieldSchemaJson
    // above and TemplateKind below are COPIED from the template at creation
    // time, not read live from it — see CertificateTemplate's own comment.
    public Guid? TemplateId { get; set; }
    public CertificateTemplate? Template { get; set; }

    // Copied from CertificateTemplate.Kind at creation time. Defaults to
    // Certificate (today's only layout) so every achievement created before
    // this feature existed keeps its current appearance with zero data
    // migration beyond adding this column.
    public TemplateKind TemplateKind { get; set; } = TemplateKind.Certificate;

    // Copied from the template's own visual design at creation time — same
    // "snapshot, not live reference" reasoning as TemplateKind just above.
    // Never touched by AchievementsController.Update (joins TemplateKind's
    // exclusion list), which is exactly what makes it safe for
    // CredentialResponse/VerifyCredentialResponse to read these live off
    // credential.Achievement rather than needing yet another copy on
    // Credential itself: an achievement's own snapshot of its template
    // never changes after creation, so every credential issued against it
    // renders identically regardless of what happens to the template later.
    public string TemplateTitleText { get; set; } = default!;
    public string? TemplateSubtitleText { get; set; }
    public string? TemplateAccentColor { get; set; }
    public string? TemplateFontFamily { get; set; }
    public string TemplateLayoutJson { get; set; } = default!;
    public string? TemplateBackgroundImageUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
