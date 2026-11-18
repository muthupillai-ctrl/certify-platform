export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: string;
  // Signed in with a temporary password after an admin reset: the token only
  // works for changing the password (enforced by the API).
  passwordChangeRequired?: boolean;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

// Must match the API's PasswordPolicy.MinLength.
export const PASSWORD_MIN_LENGTH = 10;

// The backend issues role claims using .NET's ClaimTypes.Role, which
// serializes into the JWT with this long URI as the JSON key — NOT the short
// "role" you'd expect from a typical JWT — see JwtTokenService on the API
// side. Every role check on the client has to read this exact key.
export const ROLE_CLAIM_TYPE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

// Decoded from the JWT on the client for display/route-guard purposes only.
// The backend is the source of truth for every actual authorization
// decision — see TenantClaimGuardMiddleware and [Authorize(Roles=...)] on
// the API side. Never trust this client-side decode for anything sensitive.
export interface DecodedToken {
  sub: string;
  email: string;
  tenant_id?: string;
  tenant_slug?: string;
  tenant_schema?: string;
  exp: number;
  // Indexed rather than named for the role claim — its JSON key is the long
  // ROLE_CLAIM_TYPE URI above, not a plain identifier.
  [claim: string]: unknown;
}
