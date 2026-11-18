import { Injectable, signal } from '@angular/core';

// The admin header's search box. Shared state so the header (AdminShell)
// and the Certificates page read the same text; the Certificates page
// applies it along with its other filters.
@Injectable({ providedIn: 'root' })
export class CertificateSearchService {
  readonly query = signal('');
}
