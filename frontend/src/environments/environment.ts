export const environment = {
  production: false,
  apiUrl: 'http://localhost:5080/api/v1',
  // Local dev has no real subdomain-per-tenant DNS, so the tenant is sent
  // explicitly via header instead — see auth.interceptor.ts and
  // backend TenantResolutionMiddleware's X-Tenant-Slug path.
  devTenantSlug: 'demo',
};
