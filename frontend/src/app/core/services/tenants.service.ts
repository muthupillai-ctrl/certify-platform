import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CreateTenantRequest, Tenant, TenantDetail, TenantProfile } from '../models/tenant.models';

// Platform-scoped (SuperAdmin only) — see TenantsController on the API side.
@Injectable({ providedIn: 'root' })
export class TenantsService {
  constructor(private http: HttpClient) {}

  list(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${environment.apiUrl}/platform/tenants`);
  }

  create(request: CreateTenantRequest): Observable<Tenant> {
    return this.http.post<Tenant>(`${environment.apiUrl}/platform/tenants`, request);
  }

  get(id: string): Observable<TenantDetail> {
    return this.http.get<TenantDetail>(`${environment.apiUrl}/platform/tenants/${id}`);
  }

  updateProfile(id: string, profile: TenantProfile): Observable<TenantDetail> {
    return this.http.put<TenantDetail>(`${environment.apiUrl}/platform/tenants/${id}/profile`, profile);
  }

  suspend(id: string, reason: string): Observable<TenantDetail> {
    return this.http.post<TenantDetail>(`${environment.apiUrl}/platform/tenants/${id}/suspend`, { reason });
  }

  reactivate(id: string): Observable<TenantDetail> {
    return this.http.post<TenantDetail>(`${environment.apiUrl}/platform/tenants/${id}/reactivate`, {});
  }

  // Only Ahamsys adds institution admins; they choose their own password at first sign-in.
  addAdmin(id: string, admin: { email: string; fullName: string; initialPassword: string }): Observable<unknown> {
    return this.http.post(`${environment.apiUrl}/platform/tenants/${id}/admins`, admin);
  }
}
