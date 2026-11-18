import { Injectable, computed, signal } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { StudentCertificate, StudentLoginResponse, StudentPortalBanner, StudentPortalInstitution, StudentProfile, StudentSignInResponse, StudentWelcome } from '../models/student.models';

interface StoredSession {
  token: string;
  expiresAtUtc: string;
  student: StudentProfile;
}

const STORAGE_KEY = 'certyfi_student_session';
// The institution last signed in to: /student/login goes straight there
// while that session lasts.
const LAST_SLUG_KEY = 'certyfi_student_institution';

// The student portal's own sign-in, kept apart from the staff session
// (AuthService): its own storage key, and it sets its own Authorization and
// X-Tenant-Slug headers (the staff interceptor skips /student-portal/).
@Injectable({ providedIn: 'root' })
export class StudentSessionService {
  private readonly session = signal<StoredSession | null>(this.restore());
  readonly student = computed(() => this.session()?.student ?? null);

  constructor(private http: HttpClient) {}

  // Signed in to this institution, with a token that hasn't expired.
  isSignedIn(tenantSlug: string): boolean {
    const s = this.session();
    if (!s || s.student.institutionSlug !== tenantSlug) return false;
    if (new Date(s.expiresAtUtc).getTime() <= Date.now()) {
      this.signOut();
      return false;
    }
    return true;
  }

  // Public: the institution's name and branding, and its banners.
  institution(tenantSlug: string): Observable<StudentPortalInstitution> {
    return this.http.get<StudentPortalInstitution>(`${this.base}/institution`, { headers: this.headers(tenantSlug) });
  }

  banners(tenantSlug: string): Observable<StudentPortalBanner[]> {
    return this.http.get<StudentPortalBanner[]>(`${this.base}/banners`, { headers: this.headers(tenantSlug) });
  }

  bannerClicked(tenantSlug: string, id: string): void {
    this.http.post(`${this.base}/banners/${id}/click`, {}, { headers: this.headers(tenantSlug) }).subscribe({ error: () => undefined });
  }

  lastInstitution(): string {
    try {
      return localStorage.getItem(LAST_SLUG_KEY) ?? '';
    } catch {
      return '';
    }
  }

  welcome(token: string): Observable<StudentWelcome> {
    return this.http.get<StudentWelcome>(`${this.base}/welcome/${encodeURIComponent(token)}`, { headers: this.headers(slugOf(token)) });
  }

  setUp(token: string, password: string): Observable<StudentLoginResponse> {
    return this.http
      .post<StudentLoginResponse>(`${this.base}/setup`, { token, password }, { headers: this.headers(slugOf(token)) })
      .pipe(tap((r) => this.store(r)));
  }

  login(tenantSlug: string, email: string, password: string): Observable<StudentLoginResponse> {
    return this.http
      .post<StudentLoginResponse>(`${this.base}/login`, { email, password }, { headers: this.headers(tenantSlug) })
      .pipe(tap((r) => this.store(r)));
  }

  // Without an institution code: the API finds the institution(s) where
  // this email is a student and checks the password at each.
  signInAnywhere(email: string, password: string): Observable<StudentSignInResponse> {
    return this.http
      .post<StudentSignInResponse>(`${environment.apiUrl}/student-sign-in`, { email, password })
      .pipe(tap((r) => r.signedIn && this.store(r.signedIn)));
  }

  // A reset link from each institution where the email is a student
  // (always 202).
  forgotPasswordAnywhere(email: string): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/student-sign-in/forgot-password`, { email });
  }

  // Always succeeds (202), whether or not the email is a student here.
  forgotPassword(tenantSlug: string, email: string): Observable<void> {
    return this.http.post<void>(`${this.base}/forgot-password`, { email }, { headers: this.headers(tenantSlug) });
  }

  certificates(): Observable<StudentCertificate[]> {
    return this.http.get<StudentCertificate[]>(`${this.base}/certificates`, { headers: this.authHeaders() });
  }

  pay(id: string): Observable<StudentCertificate> {
    return this.http.post<StudentCertificate>(`${this.base}/certificates/${id}/pay`, {}, { headers: this.authHeaders() });
  }

  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${this.base}/change-password`, { currentPassword, newPassword }, { headers: this.authHeaders() });
  }

  signOut(): void {
    this.session.set(null);
    try {
      localStorage.removeItem(STORAGE_KEY);
    } catch {
      // Storage unavailable (private mode): the session just isn't kept.
    }
  }

  private get base(): string {
    return `${environment.apiUrl}/student-portal`;
  }

  private headers(tenantSlug: string): HttpHeaders {
    return new HttpHeaders({ 'X-Tenant-Slug': tenantSlug });
  }

  private authHeaders(): HttpHeaders {
    const s = this.session();
    return new HttpHeaders({ 'X-Tenant-Slug': s?.student.institutionSlug ?? '', Authorization: `Bearer ${s?.token ?? ''}` });
  }

  private store(r: StudentLoginResponse): void {
    const s: StoredSession = { token: r.accessToken, expiresAtUtc: r.expiresAtUtc, student: r.student };
    this.session.set(s);
    try {
      localStorage.setItem(LAST_SLUG_KEY, r.student.institutionSlug);
      localStorage.setItem(STORAGE_KEY, JSON.stringify(s));
    } catch {
      // Storage unavailable: signed in for this tab only.
    }
  }

  private restore(): StoredSession | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as StoredSession) : null;
    } catch {
      return null;
    }
  }
}

// A portal link's token starts with the institution code ("<slug>.<...>").
export function slugOf(token: string): string {
  return token.split('.')[0] ?? '';
}
