import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { HomeAccessService } from '../services/home-access.service';

// The home page is private: without a pass, go to the mobile-number page.
// The reverse proxy enforces the same on the server (this covers in-app
// navigation, which never reaches it).
export const homeAccessGuard: CanActivateFn = () => {
  const router = inject(Router);
  return inject(HomeAccessService).hasAccess().pipe(map((ok) => (ok ? true : router.createUrlTree(['/access']))));
};
