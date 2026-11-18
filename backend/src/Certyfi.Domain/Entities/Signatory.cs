namespace Certyfi.Domain.Entities;

// A person whose authority a certificate carries ("Dr. A. Kumar,
// Principal"). Lives inside each tenant's schema. Templates name one to
// three (CertificateTemplate.SignatoryIds); each certificate keeps its own
// copy of their names and designations when it's created
// (Credential.SignatoriesJson), so changing or replacing a signatory later
// never alters a certificate already made. Printed name and designation
// only: no signature images.
public class Signatory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Designation { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
