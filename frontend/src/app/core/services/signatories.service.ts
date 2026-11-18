import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface Signatory {
  id: string;
  name: string;
  designation: string;
  // Names of the templates that print this signatory.
  usedByTemplates: string[];
}

// The institution's signatories (SignatoriesController).
@Injectable({ providedIn: 'root' })
export class SignatoriesService {
  private readonly base = `${environment.apiUrl}/signatories`;

  constructor(private http: HttpClient) {}

  list(): Observable<Signatory[]> {
    return this.http.get<Signatory[]>(this.base);
  }

  create(name: string, designation: string): Observable<Signatory> {
    return this.http.post<Signatory>(this.base, { name, designation });
  }

  update(id: string, name: string, designation: string): Observable<Signatory> {
    return this.http.put<Signatory>(`${this.base}/${id}`, { name, designation });
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }
}
