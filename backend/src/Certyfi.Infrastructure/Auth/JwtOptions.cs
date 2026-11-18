namespace Certyfi.Infrastructure.Auth;

// Bound from configuration (appsettings / user-secrets / env vars).
// SigningKey MUST come from a secret store in any real environment — see
// README "Secrets" section. This is a symmetric HMAC key for the scaffold;
// moving to per-institution asymmetric signing (technical note section 4.6)
// is separate from this API-auth token and covers CREDENTIAL signing, not
// login sessions — don't conflate the two.
public class JwtOptions
{
    public string SigningKey { get; set; } = default!;
    public string Issuer { get; set; } = "certyfi-platform";
    public string Audience { get; set; } = "certyfi-clients";
    public int AccessTokenMinutes { get; set; } = 30;
}
