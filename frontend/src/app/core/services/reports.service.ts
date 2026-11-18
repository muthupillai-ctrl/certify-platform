import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Period } from '../date-periods';
import {
  IssuanceReport,
  ReportKind,
  StaffActivityReport,
  StatusChangesReport,
  VerificationActivityPage,
  VerificationActivityQuery,
} from '../models/reports.models';

function params(values: object): HttpParams {
  let p = new HttpParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== null && value !== undefined && value !== '') p = p.set(key, String(value));
  }
  return p;
}

@Injectable({ providedIn: 'root' })
export class ReportsService {
  constructor(private http: HttpClient) {}

  verificationActivity(query: VerificationActivityQuery): Observable<VerificationActivityPage> {
    return this.http.get<VerificationActivityPage>(`${environment.apiUrl}/verification-activity`, { params: params(query) });
  }

  verificationActivityCsv(query: VerificationActivityQuery): Observable<Blob> {
    return this.http.get(`${environment.apiUrl}/verification-activity/export.csv`, { params: params(query), responseType: 'blob' });
  }

  issuance(period: Period): Observable<IssuanceReport> {
    return this.http.get<IssuanceReport>(`${environment.apiUrl}/reports/issuance`, { params: params(period) });
  }

  statusChanges(period: Period): Observable<StatusChangesReport> {
    return this.http.get<StatusChangesReport>(`${environment.apiUrl}/reports/status-changes`, { params: params(period) });
  }

  staffActivity(period: Period): Observable<StaffActivityReport> {
    return this.http.get<StaffActivityReport>(`${environment.apiUrl}/reports/staff-activity`, { params: params(period) });
  }

  // Every row as a spreadsheet (a blob: the request needs the bearer token).
  csv(kind: ReportKind, period: Period): Observable<Blob> {
    return this.http.get(`${environment.apiUrl}/reports/${kind}.csv`, { params: params(period), responseType: 'blob' });
  }
}
