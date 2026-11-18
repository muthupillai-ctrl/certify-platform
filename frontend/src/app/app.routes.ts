import { homeAccessGuard } from './core/guards/home-access.guard';
import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { roleGuard } from './core/guards/role.guard';

export const routes: Routes = [
  // Public landing page: what Certyfi is, and the ways in.
  {
    path: '',
    pathMatch: 'full',
    title: 'Certyfi: digital certificates anyone can verify',
    loadComponent: () => import('./features/landing/landing.component').then((m) => m.LandingComponent),
  },
  // The presentation, private behind the mobile-number gate.
  {
    path: 'presentation',
    title: 'Certyfi presentation',
    canActivate: [homeAccessGuard],
    loadComponent: () => import('./features/presentation/presentation.component').then((m) => m.PresentationComponent),
  },
  // The presentation's mobile-number gate.
  {
    path: 'access',
    title: 'Certyfi',
    loadComponent: () => import('./features/access/access.component').then((m) => m.AccessComponent),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Own password; also the only page a temporary-password session can reach.
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/auth/change-password/change-password.component').then((m) => m.ChangePasswordComponent),
  },
  {
    path: 'admin',
    // Registrar/Approver/Learner need this shell for Credentials/
    // Achievements, but NOT for Users — that child route below carries its
    // own narrower guard. Default redirect target is 'credentials', not
    // 'users' — a non-InstitutionAdmin landing on /admin would otherwise be
    // redirected into the InstitutionAdmin-only 'users' child, fail THAT
    // guard, and bounce to /login on their very first, fully-authorized
    // navigation.
    canActivate: [authGuard, roleGuard(['InstitutionAdmin', 'Registrar', 'Approver', 'Learner'])],
    loadComponent: () =>
      import('./features/admin/admin-shell/admin-shell.component').then((m) => m.AdminShellComponent),
    children: [
      { path: '', redirectTo: 'certificates', pathMatch: 'full' },
      // Safety redirect for anything bookmarked under the old name/path.
      { path: 'credentials', redirectTo: 'certificates', pathMatch: 'full' },
      {
        path: 'users',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () => import('./features/admin/users/users.component').then((m) => m.UsersComponent),
      },
      {
        path: 'students',
        canActivate: [roleGuard(['InstitutionAdmin', 'Registrar'])],
        loadComponent: () => import('./features/admin/students/students.component').then((m) => m.StudentsComponent),
      },
      {
        path: 'audit',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () => import('./features/admin/audit/audit.component').then((m) => m.AuditComponent),
      },
      {
        path: 'signatories',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () => import('./features/admin/signatories/signatories.component').then((m) => m.SignatoriesComponent),
      },
      {
        path: 'portal-banners',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () => import('./features/admin/portal-banners/portal-banners.component').then((m) => m.PortalBannersComponent),
      },
      {
        path: 'api-keys',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () => import('./features/admin/api-keys/api-keys.component').then((m) => m.ApiKeysComponent),
      },
      {
        path: 'achievements',
        loadComponent: () =>
          import('./features/admin/achievements/achievements.component').then((m) => m.AchievementsComponent),
      },
      {
        path: 'certificates',
        loadComponent: () =>
          import('./features/admin/credentials/credentials.component').then((m) => m.CredentialsComponent),
      },
      {
        path: 'imports',
        canActivate: [roleGuard(['InstitutionAdmin', 'Registrar', 'Approver'])],
        loadComponent: () => import('./features/admin/imports/imports.component').then((m) => m.ImportsComponent),
      },
      {
        path: 'revoke-suspend',
        canActivate: [roleGuard(['InstitutionAdmin', 'Approver'])],
        loadComponent: () =>
          import('./features/admin/revoke-suspend/revoke-suspend.component').then((m) => m.RevokeSuspendComponent),
      },
      {
        path: 'settings',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () =>
          import('./features/admin/tenant-settings/tenant-settings.component').then((m) => m.TenantSettingsComponent),
      },
      {
        path: 'templates',
        // Management (create/deactivate) is InstitutionAdmin-only — a
        // Registrar gets template access only through the achievement-
        // creation dropdown, not this page.
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () =>
          import('./features/admin/certificate-templates/certificate-templates.component').then(
            (m) => m.CertificateTemplatesComponent,
          ),
      },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./features/admin/dashboard/dashboard.component').then((m) => m.DashboardComponent),
      },
      {
        path: 'verification',
        canActivate: [roleGuard(['InstitutionAdmin', 'Registrar', 'Approver'])],
        loadComponent: () =>
          import('./features/admin/verification/verification.component').then((m) => m.VerificationComponent),
      },
      {
        path: 'reports',
        canActivate: [roleGuard(['InstitutionAdmin'])],
        loadComponent: () =>
          import('./features/admin/reports/reports.component').then((m) => m.ReportsComponent),
      },
    ],
  },
  {
    // Deliberately OUTSIDE the 'admin' shell's children — the canvas editor
    // is a real design surface that needs the sidebar/topbar chrome out of
    // its way, not another panel competing with it for width. Same guards
    // the shell would otherwise have provided, applied directly since there
    // is no parent route to inherit them from here.
    path: 'admin/templates/new',
    canActivate: [authGuard, roleGuard(['InstitutionAdmin'])],
    loadComponent: () =>
      import('./features/admin/template-editor/template-editor.component').then((m) => m.TemplateEditorComponent),
  },
  {
    path: 'admin/templates/:id/edit',
    canActivate: [authGuard, roleGuard(['InstitutionAdmin'])],
    loadComponent: () =>
      import('./features/admin/template-editor/template-editor.component').then((m) => m.TemplateEditorComponent),
  },
  {
    path: 'platform/login',
    loadComponent: () =>
      import('./features/platform/login/platform-login.component').then((m) => m.PlatformLoginComponent),
  },
  {
    // Public: opened from the platform "forgot password" email.
    path: 'platform/reset-password/:token',
    loadComponent: () =>
      import('./features/platform/reset-password/platform-reset-password.component').then((m) => m.PlatformResetPasswordComponent),
  },
  {
    // Public: opened from the email-change confirmation link.
    path: 'platform/confirm-email/:token',
    loadComponent: () =>
      import('./features/platform/confirm-email/platform-confirm-email.component').then((m) => m.PlatformConfirmEmailComponent),
  },
  {
    path: 'platform',
    canActivate: [authGuard, roleGuard(['SuperAdmin'])],
    loadComponent: () =>
      import('./features/platform/platform-shell/platform-shell.component').then((m) => m.PlatformShellComponent),
    children: [
      { path: '', redirectTo: 'tenants', pathMatch: 'full' },
      {
        path: 'tenants',
        loadComponent: () =>
          import('./features/platform/tenants/tenants.component').then((m) => m.TenantsComponent),
      },
      {
        path: 'tenants/:id',
        loadComponent: () =>
          import('./features/platform/tenant-detail/tenant-detail.component').then((m) => m.TenantDetailComponent),
      },
      {
        path: 'anchoring',
        loadComponent: () =>
          import('./features/platform/anchoring/anchoring.component').then((m) => m.AnchoringComponent),
      },
      {
        path: 'profile',
        loadComponent: () => import('./features/platform/profile/platform-profile.component').then((m) => m.PlatformProfileComponent),
      },
      {
        path: 'users',
        loadComponent: () => import('./features/platform/users/platform-users.component').then((m) => m.PlatformUsersComponent),
      },
      {
        path: 'home-access',
        loadComponent: () =>
          import('./features/platform/home-access/home-access.component').then((m) => m.HomeAccessComponent),
      },
    ],
  },
  {
    // Public: the user guides (docs/user-guide), and one guide.
    path: 'help',
    loadComponent: () => import('./features/help/help.component').then((m) => m.HelpComponent),
  },
  {
    path: 'help/:guide',
    loadComponent: () => import('./features/help/help.component').then((m) => m.HelpComponent),
  },
  {
    // Public: the Partner API guide for developers at other companies.
    path: 'developers',
    loadComponent: () => import('./features/developers/developers.component').then((m) => m.DevelopersComponent),
  },
  {
    // Public look-up by institution code + certificate code.
    path: 'verify',
    pathMatch: 'full',
    loadComponent: () => import('./features/verify/lookup/verify-lookup.component').then((m) => m.VerifyLookupComponent),
  },
  {
    // Public check of a downloaded Open Badges credential file (works offline).
    path: 'verify/file',
    loadComponent: () => import('./features/verify/file/verify-file.component').then((m) => m.VerifyFileComponent),
  },
  {
    path: 'verify/:tenantSlug/:credentialId',
    // No guard — intentionally public. Still passes through the global
    // authInterceptor, which may attach a stale Authorization/X-Tenant-Slug
    // header if one happens to be in localStorage; harmless, since
    // TenantResolutionMiddleware exempts /api/v1/verify from tenant
    // resolution and the controller ignores auth entirely (see
    // VerificationController on the API side).
    loadComponent: () => import('./features/verify/verify.component').then((m) => m.VerifyComponent),
  },
  // Student portal: students (not staff) see their own certificates. The
  // portal component sends them to sign-in when they have no session.
  {
    // Student sign-in for any institution (asks for the code).
    path: 'student/login',
    loadComponent: () => import('./features/student/login/student-login.component').then((m) => m.StudentLoginComponent),
  },
  {
    path: 'student/welcome/:token',
    loadComponent: () => import('./features/student/welcome/student-welcome.component').then((m) => m.StudentWelcomeComponent),
  },
  {
    path: 'student/:tenantSlug/login',
    loadComponent: () => import('./features/student/login/student-login.component').then((m) => m.StudentLoginComponent),
  },
  {
    path: 'student/:tenantSlug',
    loadComponent: () => import('./features/student/portal/student-portal.component').then((m) => m.StudentPortalComponent),
  },
  {
    // Public, no guard: a partner app's signed, short-lived link to a
    // template drawn with sample data (see TemplatePreviewController).
    path: 'template-preview/:token',
    loadComponent: () => import('./features/template-preview/template-preview.component').then((m) => m.TemplatePreviewComponent),
  },
  { path: '**', redirectTo: 'login' },
];
