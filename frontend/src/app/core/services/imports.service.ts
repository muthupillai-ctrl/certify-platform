import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  DateFormat,
  ImportBatch,
  ImportBatchDetail,
  ImportCheckResult,
  ImportParseResult,
  ImportProgress,
  ImportRow,
  ImportRowsResult,
} from '../models/import.models';

@Injectable({ providedIn: 'root' })
export class ImportsService {
  private readonly base = `${environment.apiUrl}/imports`;

  constructor(private http: HttpClient) {}

  // The spreadsheet's headers and rows as text (nothing is stored).
  parse(file: File): Observable<ImportParseResult> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ImportParseResult>(`${this.base}/parse`, form);
  }

  sampleCsv(templateId: string): Observable<Blob> {
    return this.http.get(`${this.base}/template.csv`, { params: { templateId }, responseType: 'blob' });
  }

  check(templateId: string, title: string | null, dateFormat: DateFormat, rows: ImportRow[]): Observable<ImportCheckResult> {
    return this.http.post<ImportCheckResult>(`${this.base}/check`, { templateId, title, dateFormat, rows });
  }

  create(templateId: string, fileName: string, expectedRows: number): Observable<ImportBatch> {
    return this.http.post<ImportBatch>(this.base, { templateId, fileName, expectedRows });
  }

  addRows(id: string, title: string | null, dateFormat: DateFormat, skipLikelyDuplicates: boolean, rows: ImportRow[], firstRowNumber: number): Observable<ImportRowsResult> {
    return this.http.post<ImportRowsResult>(`${this.base}/${id}/rows`, { title, dateFormat, skipLikelyDuplicates, rows, firstRowNumber });
  }

  finish(id: string): Observable<ImportBatch> {
    return this.http.post<ImportBatch>(`${this.base}/${id}/finish`, {});
  }

  list(): Observable<ImportBatch[]> {
    return this.http.get<ImportBatch[]>(this.base);
  }

  get(id: string): Observable<ImportBatchDetail> {
    return this.http.get<ImportBatchDetail>(`${this.base}/${id}`);
  }

  // Each call signs the next chunk; call again until remaining is 0.
  approve(id: string, confirmCount: number | null): Observable<ImportProgress> {
    return this.http.post<ImportProgress>(`${this.base}/${id}/approve`, { confirmCount });
  }

  // An institution admin gives final approval (template's rule); chunked like approve.
  finalApprove(id: string, confirmCount: number | null): Observable<ImportProgress> {
    return this.http.post<ImportProgress>(`${this.base}/${id}/final-approve`, { confirmCount });
  }

  // An approver rejects (note required) or the importer withdraws; chunked like approve.
  reject(id: string, note: string | null): Observable<ImportProgress> {
    return this.http.post<ImportProgress>(`${this.base}/${id}/reject`, { note });
  }
}
