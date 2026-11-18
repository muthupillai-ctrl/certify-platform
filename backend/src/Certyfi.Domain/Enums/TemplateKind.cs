namespace Certyfi.Domain.Enums;

// Decides which certificate layout the public verify page renders for a
// credential issued against an achievement derived from a
// CertificateTemplate of this kind — a marks card is a transcript/
// mark-sheet, not a decorative certificate scroll. Trivially extensible
// later (e.g. Badge) without a schema change beyond adding a case here and
// a verify-page branch. Same "real enum, stored as int" convention as
// CredentialStatus.
public enum TemplateKind
{
    Certificate,
    MarksCard
}
