import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

// Route-level UX guard only — hides/redirects nav the user shouldn't see.
// The API enforces the real authorization decision independently
// ([Authorize(Roles=...)] + TenantClaimGuardMiddleware); this guard existing
// is not a substitute for that and must never be treated as one.
export const roleGuard = (allowedRoles: string[]): CanActivateFn => {
  return () => {
    const auth = inject(AuthService);
    const router = inject(Router);

    const ok = allowedRoles.some((r) => auth.hasRole(r));
    if (ok) return true;

    router.navigate(['/login']);
    return false;
  };
};
