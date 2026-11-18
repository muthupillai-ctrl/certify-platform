import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  AnchoringStatus,
  CheckpointDiagnosis,
  CheckpointFilter,
  CheckpointRow,
  ConnectivityReport,
} from '../models/anchoring.models';

// Platform-scoped (SuperAdmin only) — see AnchoringController on the API side.
@Injectable({ providedIn: 'root' })
export class AnchoringService {
  private readonly base = `${environment.apiUrl}/platform/anchoring`;

  constructor(private http: HttpClient) {}

  checkpointNow(): Observable<{ checkpointsCreated: number }> {
    return this.http.post<{ checkpointsCreated: number }>(`${this.base}/checkpoint-now`, {});
  }

  upgradeNow(): Observable<{ checkpointsConfirmed: number }> {
    return this.http.post<{ checkpointsConfirmed: number }>(`${this.base}/upgrade-now`, {});
  }

  status(): Observable<AnchoringStatus> {
    return this.http.get<AnchoringStatus>(`${this.base}/status`);
  }

  connectivity(): Observable<ConnectivityReport> {
    return this.http.get<ConnectivityReport>(`${this.base}/connectivity`);
  }

  checkpoints(filter: CheckpointFilter, tenantSlug: string | null): Observable<CheckpointRow[]> {
    let params = new HttpParams().set('status', filter);
    if (tenantSlug) params = params.set('tenant', tenantSlug);
    return this.http.get<CheckpointRow[]>(`${this.base}/checkpoints`, { params });
  }

  diagnose(row: CheckpointRow): Observable<CheckpointDiagnosis> {
    return this.http.get<CheckpointDiagnosis>(`${this.path(row)}/diagnose`);
  }

  checkOne(row: CheckpointRow): Observable<CheckpointRow> {
    return this.http.post<CheckpointRow>(`${this.path(row)}/check`, {});
  }

  reverify(row: CheckpointRow): Observable<CheckpointRow> {
    return this.http.post<CheckpointRow>(`${this.path(row)}/reverify`, {});
  }

  // A blob, not a plain link: the request needs the SuperAdmin bearer token.
  proof(row: CheckpointRow): Observable<Blob> {
    return this.http.get(`${this.path(row)}/proof`, { responseType: 'blob' });
  }

  private path(row: CheckpointRow): string {
    return `${this.base}/checkpoints/${encodeURIComponent(row.tenantSlug)}/${row.id}`;
  }
}
