import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { LoginResponse } from '../models/auth.models';
import { AuthService } from './auth.service';

export interface PlatformAccountChange {
  changedAt: string;
  changedByName: string;
  field: string;
  oldValue: string | null;
  newValue: string | null;
  changedByAdminId: string;
}

export interface PlatformAccount {
  id: string;
  email: string;
  fullName: string;
  createdAt: string;
  lastLoginAt: string | null;
  passwordChangedAt: string | null;
  mustChangePassword: boolean;
  changes: PlatformAccountChange[];
}

export interface PlatformAdmin {
  id: string;
  email: string;
  fullName: string;
  isActive: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt: string | null;
  isYou: boolean;
}

export interface PlatformAdminWithPassword {
  admin: PlatformAdmin;
  temporaryPassword: string;
}

// The signed-in platform admin's profile, and the Platform users page
// (PlatformAccountController / PlatformAdminsController).
@Injectable({ providedIn: 'root' })
export class PlatformAccountService {
  private readonly base = `${environment.apiUrl}/platform`;

  constructor(private http: HttpClient, private auth: AuthService) {}

  get(): Observable<PlatformAccount> {
    return this.http.get<PlatformAccount>(`${this.base}/account`);
  }

  updateName(fullName: string): Observable<PlatformAccount> {
    return this.http.put<PlatformAccount>(`${this.base}/account/name`, { fullName });
  }

  // Ends every other session; keeps this one with the fresh token.
  changePassword(currentPassword: string, newPassword: string): Observable<LoginResponse> {
    return this.http
      .post<LoginResponse>(`${this.base}/account/change-password`, { currentPassword, newPassword })
      .pipe(tap((r) => this.auth.useToken(r.accessToken)));
  }

  changeEmail(newEmail: string, currentPassword: string): Observable<{ sentTo: string }> {
    return this.http.post<{ sentTo: string }>(`${this.base}/account/change-email`, { newEmail, currentPassword });
  }

  confirmEmail(token: string): Observable<{ email: string }> {
    return this.http.post<{ email: string }>(`${this.base}/account/confirm-email`, { token });
  }

  // Always succeeds (202), whether or not the email has an account.
  forgotPassword(email: string): Observable<void> {
    return this.http.post<void>(`${this.base}/auth/forgot-password`, { email });
  }

  resetPassword(token: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.base}/auth/reset-password`, { token, newPassword });
  }

  listAdmins(): Observable<PlatformAdmin[]> {
    return this.http.get<PlatformAdmin[]>(`${this.base}/admins`);
  }

  createAdmin(email: string, fullName: string): Observable<PlatformAdminWithPassword> {
    return this.http.post<PlatformAdminWithPassword>(`${this.base}/admins`, { email, fullName });
  }

  resetAdmin(id: string): Observable<PlatformAdminWithPassword> {
    return this.http.post<PlatformAdminWithPassword>(`${this.base}/admins/${id}/reset-password`, {});
  }

  setAdminActive(id: string, active: boolean): Observable<PlatformAdmin> {
    return this.http.post<PlatformAdmin>(`${this.base}/admins/${id}/${active ? 'reactivate' : 'deactivate'}`, {});
  }

  adminHistory(id: string): Observable<PlatformAccountChange[]> {
    return this.http.get<PlatformAccountChange[]>(`${this.base}/admins/${id}/history`);
  }
}
