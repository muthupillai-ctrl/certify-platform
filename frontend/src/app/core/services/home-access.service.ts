import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface HomeAccessNumber {
  id: string;
  number: string;
  label: string;
  createdAt: string;
  lastUsedAt: string | null;
}

// The private home page's gate (AccessController) and, for the platform
// operator, the list of allowed mobile numbers. The pass is an HttpOnly
// cookie, so requests go withCredentials (the API is another origin locally).
@Injectable({ providedIn: 'root' })
export class HomeAccessService {
  constructor(private http: HttpClient) {}

  hasAccess(): Observable<boolean> {
    return this.http.get(`${environment.apiUrl}/access`, { withCredentials: true, observe: 'response' }).pipe(
      map(() => true),
      catchError(() => of(false)),
    );
  }

  enter(mobile: string): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/access`, { mobile }, { withCredentials: true });
  }

  list(): Observable<HomeAccessNumber[]> {
    return this.http.get<HomeAccessNumber[]>(`${environment.apiUrl}/platform/home-access`);
  }

  add(number: string, label: string): Observable<HomeAccessNumber> {
    return this.http.post<HomeAccessNumber>(`${environment.apiUrl}/platform/home-access`, { number, label });
  }

  remove(id: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/platform/home-access/${id}`);
  }
}
