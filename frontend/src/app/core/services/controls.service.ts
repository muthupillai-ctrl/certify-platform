import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

// A safeguard change one institution admin asked for and another must
// confirm (ControlRequestsController).
export interface ControlRequest {
  id: string;
  kind: 'DisableFinalApproval' | 'EnableAutoIssue';
  targetId: string;
  targetName: string;
  requestedByName: string;
  requestedAt: string;
  status: 'Pending' | 'Confirmed' | 'Refused' | 'Withdrawn';
  decidedByName: string | null;
  decidedAt: string | null;
  note: string | null;
  canDecide: boolean;
  isMine: boolean;
}

export interface AuditEntry {
  id: string;
  at: string;
  actorKind: 'Staff' | 'Platform';
  actorName: string;
  action: string;
  subject: string;
  details: string | null;
}

export interface AuditPage {
  items: AuditEntry[];
  total: number;
  page: number;
  pageSize: number;
}

@Injectable({ providedIn: 'root' })
export class ControlsService {
  private readonly base = environment.apiUrl;

  constructor(private http: HttpClient) {}

  requests(pendingOnly = false): Observable<ControlRequest[]> {
    return this.http.get<ControlRequest[]>(`${this.base}/control-requests`, { params: { pendingOnly } });
  }

  confirm(id: string): Observable<ControlRequest> {
    return this.http.post<ControlRequest>(`${this.base}/control-requests/${id}/confirm`, {});
  }

  refuse(id: string, note: string | null): Observable<ControlRequest> {
    return this.http.post<ControlRequest>(`${this.base}/control-requests/${id}/refuse`, { note });
  }

  withdraw(id: string): Observable<ControlRequest> {
    return this.http.post<ControlRequest>(`${this.base}/control-requests/${id}/withdraw`, {});
  }

  auditLog(page: number, pageSize = 50): Observable<AuditPage> {
    return this.http.get<AuditPage>(`${this.base}/audit-log`, { params: { page, pageSize } });
  }

  auditCsv(): Observable<Blob> {
    return this.http.get(`${this.base}/audit-log/export.csv`, { responseType: 'blob' });
  }
}
