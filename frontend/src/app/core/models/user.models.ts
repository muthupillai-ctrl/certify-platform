export interface TenantUser {
  id: string;
  email: string;
  fullName: string;
  isActive: boolean;
  roles: string[];
}

// A staff account in full (TenantUserDetail on the API).
export interface TenantUserDetail extends TenantUser {
  createdAt: string;
  lastLoginAt: string | null;
  mustChangePassword: boolean;
  passwordChangedAt: string | null;
  certificatesRequested: number;
  certificatesDecided: number;
}

export interface UpdateUserRequest {
  fullName: string;
  email: string;
  roles: string[];
}

// temporaryPassword is returned once, for the admin to hand over.
export interface ResetPasswordResponse {
  user: TenantUserDetail;
  temporaryPassword: string;
}

export interface CreateUserRequest {
  email: string;
  fullName: string;
  initialPassword: string;
  role: string;
}

// Must mirror backend SystemRole.TenantAssignable exactly — deliberately
// excludes SuperAdmin. That exclusion is enforced server-side (see
// UsersController + TenantClaimGuardMiddleware); this list only controls
// what the form offers, it is not itself a security boundary.
export const ASSIGNABLE_ROLES = [
  'InstitutionAdmin',
  'Registrar',
  'Approver',
  'Learner',
  'Verifier',
  'Developer',
];
