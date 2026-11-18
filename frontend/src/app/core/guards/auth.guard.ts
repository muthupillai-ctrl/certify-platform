import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const platform = state.url.startsWith('/platform');
  if (!auth.currentUser()) {
    router.navigate([platform ? '/platform/login' : '/login']);
    return false;
  }
  // A temporary-password session can only reach the change-password page
  // (the API enforces the same thing on every request); for platform
  // accounts that's their profile page.
  const changePage = auth.hasRole('SuperAdmin') ? '/platform/profile' : '/change-password';
  if (auth.passwordChangeRequired() && !state.url.startsWith(changePage)) {
    router.navigate([changePage]);
    return false;
  }
  return true;
};
