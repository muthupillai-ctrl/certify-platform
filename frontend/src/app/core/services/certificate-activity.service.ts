import { Injectable, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { CredentialsService } from './credentials.service';
import { CredentialSummaryCounts } from '../models/credential.models';

// Counts shared by the sidebar badge ("N waiting for your approval") and
// the Certificates page's quick views. Refreshed by the admin shell every
// minute and by the Certificates page after any change.
@Injectable({ providedIn: 'root' })
export class CertificateActivityService {
  readonly summary = signal<CredentialSummaryCounts | null>(null);

  constructor(private credentialsService: CredentialsService) {}

  refresh(): Observable<CredentialSummaryCounts> {
    return this.credentialsService.summary().pipe(tap((s) => this.summary.set(s)));
  }
}
