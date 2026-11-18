export interface Tenant {
  id: string;
  name: string;
  slug: string;
  status: string;
  createdAt: string;
}

export interface CreateTenantRequest {
  name: string;
  slug: string;
  institutionAdminEmail: string;
  institutionAdminInitialPassword: string;
  // Two admins are required (final approval and safeguard changes need two people).
  institutionAdminFullName: string;
  secondAdminEmail: string;
  secondAdminFullName: string;
  secondAdminInitialPassword: string;
}

export interface TenantBranding {
  logoUrl: string | null;
  primaryColor: string | null;
  // The institution's name; returned by the API, never sent.
  name?: string | null;
}

// Institution profile (TenantProfileResponse / TenantProfileInput on the
// API). Public fields — legal name, website, registration, accreditation,
// about, city, country — are shown on verification pages; street address
// and contact details are only for the institution's admins and SuperAdmins.
export interface TenantProfile {
  name: string;
  legalName: string | null;
  websiteUrl: string | null;
  registrationNumber: string | null;
  accreditationBody: string | null;
  about: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  region: string | null;
  postalCode: string | null;
  country: string | null;
  contactEmail: string | null;
  contactPhone: string | null;
  profileUpdatedAt?: string | null;
}

export interface TenantDetail {
  id: string;
  name: string;
  slug: string;
  status: 'Active' | 'Suspended' | 'Provisioning' | 'Deprovisioned' | string;
  createdAt: string;
  activatedAt: string | null;
  suspendedAt: string | null;
  suspensionReason: string | null;
  profile: TenantProfile;
  userCount: number;
  activeUserCount: number;
  activeCertificateCount: number;
  activeApiKeyCount: number;
}

export interface PublicInstitutionProfile {
  legalName: string | null;
  websiteUrl: string | null;
  registrationNumber: string | null;
  accreditationBody: string | null;
  about: string | null;
  city: string | null;
  country: string | null;
}
