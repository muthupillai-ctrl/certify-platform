import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CreateUserRequest, ResetPasswordResponse, TenantUser, TenantUserDetail, UpdateUserRequest } from '../models/user.models';

// Staff accounts. Two scopes share one shape: the signed-in Institution
// Admin's own institution (tenantId omitted — the interceptor sends the
// tenant), or any institution for a SuperAdmin (tenantId given, platform
// endpoints). The API applies the same safeguards to both.
@Injectable({ providedIn: 'root' })
export class UsersService {
  constructor(private http: HttpClient) {}

  list(tenantId?: string): Observable<TenantUserDetail[]> {
    return this.http.get<TenantUserDetail[]>(this.base(tenantId));
  }

  get(id: string): Observable<TenantUserDetail> {
    return this.http.get<TenantUserDetail>(`${environment.apiUrl}/users/${id}`);
  }

  create(request: CreateUserRequest): Observable<TenantUser> {
    return this.http.post<TenantUser>(`${environment.apiUrl}/users`, request);
  }

  update(id: string, request: UpdateUserRequest, tenantId?: string): Observable<TenantUserDetail> {
    return this.http.put<TenantUserDetail>(`${this.base(tenantId)}/${id}`, request);
  }

  setActive(id: string, active: boolean, tenantId?: string): Observable<TenantUserDetail> {
    return this.http.post<TenantUserDetail>(`${this.base(tenantId)}/${id}/${active ? 'reactivate' : 'deactivate'}`, {});
  }

  resetPassword(id: string, tenantId?: string): Observable<ResetPasswordResponse> {
    return this.http.post<ResetPasswordResponse>(`${this.base(tenantId)}/${id}/reset-password`, {});
  }

  private base(tenantId?: string): string {
    return tenantId ? `${environment.apiUrl}/platform/tenants/${tenantId}/users` : `${environment.apiUrl}/users`;
  }
}
