import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ChangePasswordRequest, LoginRequest, LoginResponse, DecodedToken, ROLE_CLAIM_TYPE } from '../models/auth.models';

const TOKEN_KEY = 'certyfi_access_token';
const TENANT_KEY = 'certyfi_tenant_slug';

@Injectable({ providedIn: 'root' })
export class AuthService {
  // Signal so route guards / nav bar can react to login state without
  // wiring up a separate observable subscription everywhere.
  readonly currentUser = signal<DecodedToken | null>(this.decodeStoredToken());

  constructor(private http: HttpClient) {}

  // Deployment is a single domain (certyfi.ahamsys.com) with no per-tenant
  // subdomain, so there is no way to derive which institution someone is
  // logging in as from the URL. The tenant slug therefore has to be known
  // BEFORE the login call — captured on the login form, not read off a
  // token that doesn't exist yet. Stored separately from the decoded JWT so
  // the interceptor still has it for the login request itself.
  //
  // If per-tenant subdomains are added later (technical note's wildcard
  // option), this field becomes redundant with the subdomain and can be
  // dropped, or kept as an override for institutions sharing infrastructure.
  setTenantSlug(slug: string): void {
    localStorage.setItem(TENANT_KEY, slug.trim().toLowerCase());
  }

  getTenantSlug(): string | null {
    return this.currentUser()?.tenant_slug ?? localStorage.getItem(TENANT_KEY) ?? environment.devTenantSlug ?? null;
  }

  login(tenantSlug: string, request: LoginRequest): Observable<LoginResponse> {
    this.setTenantSlug(tenantSlug);

    return this.http.post<LoginResponse>(`${environment.apiUrl}/auth/login`, request).pipe(
      tap((res) => {
        localStorage.setItem(TOKEN_KEY, res.accessToken);
        this.currentUser.set(this.decodeToken(res.accessToken));
      }),
    );
  }

  // Platform (SuperAdmin) login — not tenant-scoped, hits a separate backend
  // endpoint (PlatformAuthController) with no X-Tenant-Slug involved. The
  // interceptor still attaches whatever tenant slug happens to be in
  // localStorage on this and every later request, but that's harmless here:
  // TenantResolutionMiddleware exempts /api/v1/platform/* from tenant
  // resolution entirely, so the header is simply ignored server-side.
  platformLogin(request: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiUrl}/platform/auth/login`, request).pipe(
      tap((res) => {
        localStorage.setItem(TOKEN_KEY, res.accessToken);
        this.currentUser.set(this.decodeToken(res.accessToken));
      }),
    );
  }

  // Staff user changing their own password — also the only thing a
  // temporary-password session can do. Stores the fresh token it returns.
  changePassword(request: ChangePasswordRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiUrl}/auth/change-password`, request).pipe(
      tap((res) => {
        localStorage.setItem(TOKEN_KEY, res.accessToken);
        this.currentUser.set(this.decodeToken(res.accessToken));
      }),
    );
  }

  // Replaces the stored token (a platform password change returns a fresh one).
  useToken(token: string): void {
    localStorage.setItem(TOKEN_KEY, token);
    this.currentUser.set(this.decodeToken(token));
  }

  // True while signed in with a temporary password (the token's pwd_change
  // claim, see JwtTokenService on the API side).
  passwordChangeRequired(): boolean {
    return this.currentUser()?.['pwd_change'] === 'required';
  }

  logout(): void {
    localStorage.removeItem(TOKEN_KEY);
    // Deliberately NOT clearing TENANT_KEY on logout — the same institution
    // is almost always who logs in next on a shared device, and re-typing
    // an institution code on every logout is friction with no security
    // benefit (it's not a secret).
    this.currentUser.set(null);
  }

  getToken(): string | null {
    return localStorage.getItem(TOKEN_KEY);
  }

  hasRole(role: string): boolean {
    const roles = this.currentUser()?.[ROLE_CLAIM_TYPE] as string | string[] | undefined;
    if (!roles) return false;
    return Array.isArray(roles) ? roles.includes(role) : roles === role;
  }

  private decodeStoredToken(): DecodedToken | null {
    const token = this.getToken();
    return token ? this.decodeToken(token) : null;
  }

  private decodeToken(token: string): DecodedToken | null {
    try {
      const payload = token.split('.')[1];
      return JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/')));
    } catch {
      return null;
    }
  }
}
