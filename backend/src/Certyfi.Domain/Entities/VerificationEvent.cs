namespace Certyfi.Domain.Entities;

// Lives inside each tenant's schema. One public verification of one of the
// institution's certificates — shown to staff as the certificate's
// verification history. Deliberately records no IP address or other
// verifier identity: the institution learns THAT and HOW a certificate was
// checked, not WHO checked it. Offline checks can't be recorded (nothing
// reaches the server). Not part of the hash-chained ledger — verifications
// are observations, not changes to the credential.
public class VerificationEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Null for a code look-up that matched nothing.
    public Guid? CredentialId { get; set; }

    // See VerificationChannel.
    public string Channel { get; set; } = default!;

    // The status the verifier was shown ("Active", "Revoked", "Expired", ...)
    // or "NotFound".
    public string Result { get; set; } = default!;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
