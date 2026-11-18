import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { RevocationReasonCategory, RevocationRequest } from '../models/revocation.models';

@Injectable({ providedIn: 'root' })
export class RevocationsService {
  private readonly base = `${environment.apiUrl}/revocation-requests`;

  constructor(private http: HttpClient) {}

  // Waiting requests, oldest first.
  pending(): Observable<RevocationRequest[]> {
    return this.http.get<RevocationRequest[]>(this.base);
  }

  // The latest decided requests (approved, rejected, withdrawn).
  decided(): Observable<RevocationRequest[]> {
    return this.http.get<RevocationRequest[]>(this.base, { params: { state: 'decided' } });
  }

  request(credentialId: string, reasonCategory: RevocationReasonCategory, note: string, confirmCode: string): Observable<RevocationRequest> {
    return this.http.post<RevocationRequest>(this.base, { credentialId, reasonCategory, note, confirmCode });
  }

  approve(id: string, confirmCode: string): Observable<RevocationRequest> {
    return this.http.post<RevocationRequest>(`${this.base}/${id}/approve`, { confirmCode });
  }

  reject(id: string, note: string): Observable<RevocationRequest> {
    return this.http.post<RevocationRequest>(`${this.base}/${id}/reject`, { note });
  }

  withdraw(id: string): Observable<RevocationRequest> {
    return this.http.post<RevocationRequest>(`${this.base}/${id}/withdraw`, {});
  }
}
