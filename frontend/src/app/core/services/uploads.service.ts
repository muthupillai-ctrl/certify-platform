import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface UploadResponse {
  url: string;
}

// Backs the "import a professionally designed certificate" file-upload
// path (see UploadsController on the backend) — local-disk storage, no
// cloud object storage. Kept separate from CertificateTemplatesService
// since uploads are a generic capability a future feature (e.g. a
// directly-uploaded institution logo) could reuse.
@Injectable({ providedIn: 'root' })
export class UploadsService {
  constructor(private http: HttpClient) {}

  uploadBackgroundImage(file: File): Observable<UploadResponse> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<UploadResponse>(`${environment.apiUrl}/uploads/background-image`, formData);
  }

  // An image placed on a certificate (seal, stamp, second logo) or a portal banner.
  uploadImage(file: File): Observable<UploadResponse> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<UploadResponse>(`${environment.apiUrl}/uploads/image`, formData);
  }
}
