namespace Certyfi.Domain.Enums;

// Permissions an ApiClient can be granted. Checked per endpoint by the
// "partner:<scope>" authorization policies (see Program.cs).
public static class PartnerScope
{
    public const string TemplatesRead = "templates:read";
    public const string CredentialsRead = "credentials:read";
    public const string CredentialsWrite = "credentials:write";
    public const string CredentialsRevoke = "credentials:revoke";

    public static readonly string[] All =
    {
        TemplatesRead, CredentialsRead, CredentialsWrite, CredentialsRevoke
    };
}
