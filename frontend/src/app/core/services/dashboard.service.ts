import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Dashboard } from '../models/dashboard.models';

@Injectable({ providedIn: 'root' })
export class DashboardService {
  constructor(private http: HttpClient) {}

  // Months in the browser's time zone, so "this month" is the user's month.
  get(): Observable<Dashboard> {
    const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    return this.http.get<Dashboard>(`${environment.apiUrl}/dashboard`, { params: { tz } });
  }
}
