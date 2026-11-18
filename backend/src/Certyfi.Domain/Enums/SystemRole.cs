namespace Certyfi.Domain.Enums;

// Baseline roles from the platform's actor model. Institution-defined custom
// roles can be layered on later; these cover the core RBAC scaffold.
public static class SystemRole
{
    public const string SuperAdmin = "SuperAdmin";       // platform operator, not tenant-scoped
    public const string InstitutionAdmin = "InstitutionAdmin";
    public const string Registrar = "Registrar";
    public const string Approver = "Approver";            // maker-checker sign-off on issuance
    public const string Learner = "Learner";
    public const string Verifier = "Verifier";             // external, mostly anonymous/no-login in practice
    public const string Developer = "Developer";           // partner/API integrator
    // Student-portal sign-ins (Student entity, not ApplicationUser). Never
    // seeded or assignable as a staff role, so it's in neither list below.
    public const string Student = "Student";

    public static readonly string[] All =
    {
        SuperAdmin, InstitutionAdmin, Registrar, Approver, Learner, Verifier, Developer
    };

    // Roles that may be seeded into, and assigned within, a TENANT schema.
    // Deliberately excludes SuperAdmin — a tenant user must never be able to
    // hold it. This isn't just a naming nicety: TenantProvisioningService
    // used to seed SuperAdmin into every tenant via SystemRole.All, and
    // UsersController validated new-user roles against SystemRole.All too,
    // meaning an InstitutionAdmin could create a same-tenant user with role
    // "SuperAdmin" and log in as them — a real privilege-escalation path,
    // not a theoretical one (see TenantClaimGuardMiddleware for the other
    // half of the fix). Both seeding and role-assignment validation must use
    // THIS list, never All.
    public static readonly string[] TenantAssignable =
    {
        InstitutionAdmin, Registrar, Approver, Learner, Verifier, Developer
    };
}
