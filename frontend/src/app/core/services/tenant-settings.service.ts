import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { TenantBranding, TenantProfile } from '../models/tenant.models';

@Injectable({ providedIn: 'root' })
export class TenantSettingsService {
  constructor(private http: HttpClient) {}

  getBranding(): Observable<TenantBranding> {
    return this.http.get<TenantBranding>(`${environment.apiUrl}/tenant-settings/branding`);
  }

  updateBranding(branding: TenantBranding): Observable<TenantBranding> {
    return this.http.put<TenantBranding>(`${environment.apiUrl}/tenant-settings/branding`, branding);
  }

  getProfile(): Observable<TenantProfile> {
    return this.http.get<TenantProfile>(`${environment.apiUrl}/tenant-settings/profile`);
  }

  updateProfile(profile: TenantProfile): Observable<TenantProfile> {
    return this.http.put<TenantProfile>(`${environment.apiUrl}/tenant-settings/profile`, profile);
  }

  // Days a student has to pay before an unpaid certificate is cancelled (0 = never).
  getPayments(): Observable<{ paymentDays: number }> {
    return this.http.get<{ paymentDays: number }>(`${environment.apiUrl}/tenant-settings/payments`);
  }

  updatePayments(paymentDays: number): Observable<{ paymentDays: number }> {
    return this.http.put<{ paymentDays: number }>(`${environment.apiUrl}/tenant-settings/payments`, { paymentDays });
  }
}
