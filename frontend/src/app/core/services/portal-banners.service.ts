import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface PortalBanner {
  id: string;
  title: string;
  imageUrl: string;
  linkUrl: string | null;
  side: 'Left' | 'Right';
  sortOrder: number;
  startsOn: string | null;
  endsOn: string | null;
  isActive: boolean;
  clickCount: number;
  createdAt: string;
}

export interface SavePortalBanner {
  title: string;
  imageUrl: string;
  linkUrl: string | null;
  side: 'Left' | 'Right';
  startsOn: string | null;
  endsOn: string | null;
  isActive: boolean;
}

// Banners beside the student portal (PortalBannersController).
@Injectable({ providedIn: 'root' })
export class PortalBannersService {
  private readonly base = `${environment.apiUrl}/portal-banners`;

  constructor(private http: HttpClient) {}

  list(): Observable<PortalBanner[]> {
    return this.http.get<PortalBanner[]>(this.base);
  }

  create(banner: SavePortalBanner): Observable<PortalBanner> {
    return this.http.post<PortalBanner>(this.base, banner);
  }

  update(id: string, banner: SavePortalBanner): Observable<PortalBanner> {
    return this.http.put<PortalBanner>(`${this.base}/${id}`, banner);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  reorder(left: string[], right: string[]): Observable<PortalBanner[]> {
    return this.http.put<PortalBanner[]>(`${this.base}/order`, { left, right });
  }

  uploadImage(file: File): Observable<{ url: string }> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<{ url: string }>(`${environment.apiUrl}/uploads/image`, form);
  }
}
