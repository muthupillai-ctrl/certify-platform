import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { LedgerEntry, LedgerIntegrityResponse } from '../models/ledger.models';

@Injectable({ providedIn: 'root' })
export class LedgerService {
  constructor(private http: HttpClient) {}

  getLedger(credentialId: string): Observable<LedgerEntry[]> {
    return this.http.get<LedgerEntry[]>(`${environment.apiUrl}/credentials/${credentialId}/ledger`);
  }

  // InstitutionAdmin only — server enforces this, this is just the call.
  checkIntegrity(): Observable<LedgerIntegrityResponse> {
    return this.http.get<LedgerIntegrityResponse>(`${environment.apiUrl}/ledger/integrity`);
  }
}
