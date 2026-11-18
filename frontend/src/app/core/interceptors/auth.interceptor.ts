import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from '../services/auth.service';

// Attaches the bearer token AND the tenant header on every request,
// including the login request itself. With a single domain (no
// per-tenant subdomain), the header is the ONLY way the backend knows
// which tenant this is — see AuthService.getTenantSlug for where it comes
// from before vs. after login.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // The student portal sends its own token and institution (see
  // StudentSessionService); a staff session in the same browser must not
  // be attached to it.
  if (req.url.includes('/student-portal/') || req.url.includes('/student-sign-in')) return next(req);

  const auth = inject(AuthService);
  const token = auth.getToken();
  const tenantSlug = auth.getTenantSlug();

  let headers = req.headers;
  if (token) headers = headers.set('Authorization', `Bearer ${token}`);
  if (tenantSlug) headers = headers.set('X-Tenant-Slug', tenantSlug);

  return next(req.clone({ headers }));
};
