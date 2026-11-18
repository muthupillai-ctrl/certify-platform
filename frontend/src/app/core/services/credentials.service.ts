import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  Credential,
  CreateCredentialFromTemplateRequest,
  CredentialFilterOptions,
  CredentialListQuery,
  CredentialPage,
  CredentialSummaryCounts,
  BulkActionRequest,
  BulkActionResponse,
  BulkOperation,
  FieldValues,
  CredentialVerificationHistory,
} from '../models/credential.models';
import { FieldDefinition } from '../models/achievement.models';
import { TemplateLayout } from '../models/certificate-preview.models';

// Wire shape exactly as ASP.NET serializes CredentialResponse —
// FieldValuesJson/FieldSchemaJson/LayoutJson travel as JSON-encoded
// strings. This is the only place that (de)serializes them; every
// component above this service only ever sees typed
// FieldValues/FieldDefinition[]/TemplateLayout.
interface CredentialWire extends Omit<Credential, 'fieldValues' | 'fieldSchema' | 'layout'> {
  fieldValuesJson: string;
  fieldSchemaJson: string;
  layoutJson: string;
}

interface CreateCredentialFromTemplateRequestWire {
  templateId: string;
  title: string;
  recipientName: string;
  recipientEmail: string;
  fieldValuesJson: string;
}

function fromWire(wire: CredentialWire): Credential {
  let fieldValues: FieldValues;
  let fieldSchema: FieldDefinition[];
  let layout: TemplateLayout | null;
  try {
    fieldValues = JSON.parse(wire.fieldValuesJson);
  } catch {
    fieldValues = {};
  }
  try {
    fieldSchema = JSON.parse(wire.fieldSchemaJson);
  } catch {
    fieldSchema = [];
  }
  try {
    layout = JSON.parse(wire.layoutJson);
  } catch {
    layout = null;
  }
  const { fieldValuesJson: _fieldValuesJson, fieldSchemaJson: _fieldSchemaJson, layoutJson: _layoutJson, ...rest } = wire;
  return { ...rest, fieldValues, fieldSchema, layout };
}

function toParams(query: CredentialListQuery): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') params = params.set(key, String(value));
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class CredentialsService {
  constructor(private http: HttpClient) {}

  // One page of summaries, filtered and sorted on the server.
  list(query: CredentialListQuery): Observable<CredentialPage> {
    return this.http.get<CredentialPage>(`${environment.apiUrl}/credentials`, { params: toParams(query) });
  }

  // The full record (layout, fields, signature) for the detail panel.
  get(id: string): Observable<Credential> {
    return this.http.get<CredentialWire>(`${environment.apiUrl}/credentials/${id}`).pipe(map(fromWire));
  }

  bulk(operation: BulkOperation, request: BulkActionRequest): Observable<BulkActionResponse> {
    return this.http.post<BulkActionResponse>(`${environment.apiUrl}/credentials/bulk/${operation}`, request);
  }

  // The current filters (all pages) as CSV. A blob, because the request
  // needs the bearer token (a plain link wouldn't send it).
  exportCsv(query: CredentialListQuery): Observable<Blob> {
    return this.http.get(`${environment.apiUrl}/credentials/export.csv`, { params: toParams(query), responseType: 'blob' });
  }

  summary(): Observable<CredentialSummaryCounts> {
    return this.http.get<CredentialSummaryCounts>(`${environment.apiUrl}/credentials/summary`);
  }

  filterOptions(): Observable<CredentialFilterOptions> {
    return this.http.get<CredentialFilterOptions>(`${environment.apiUrl}/credentials/filter-options`);
  }

  createFromTemplate(request: CreateCredentialFromTemplateRequest): Observable<Credential> {
    const wireRequest: CreateCredentialFromTemplateRequestWire = {
      templateId: request.templateId,
      title: request.title,
      recipientName: request.recipientName,
      recipientEmail: request.recipientEmail,
      fieldValuesJson: JSON.stringify(request.fieldValues),
    };
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/from-template`, wireRequest)
      .pipe(map(fromWire));
  }

  approve(id: string): Observable<Credential> {
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/${id}/approve`, {})
      .pipe(map(fromWire));
  }

  reject(id: string, reason: string): Observable<Credential> {
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/${id}/reject`, { reason })
      .pipe(map(fromWire));
  }

  // Suspend / reactivate: one person, from the Revoke & suspend page, with
  // the certificate's code typed to confirm. Revoking is a two-person
  // request (RevocationsService).
  suspend(id: string, reason: string, confirmCode: string): Observable<Credential> {
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/${id}/suspend`, { reason, confirmCode })
      .pipe(map(fromWire));
  }

  // Data-protection erasure (admins; revoked or rejected certificates only).
  erasePersonalData(id: string, confirmCode: string): Observable<Credential> {
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/${id}/erase-personal-data`, { confirmCode })
      .pipe(map(fromWire));
  }

  reactivate(id: string, note: string, confirmCode: string): Observable<Credential> {
    return this.http
      .post<CredentialWire>(`${environment.apiUrl}/credentials/${id}/reactivate`, { note, confirmCode })
      .pipe(map(fromWire));
  }

  // Public verification history of one certificate (channels and times only).
  verifications(id: string): Observable<CredentialVerificationHistory> {
    return this.http.get<CredentialVerificationHistory>(`${environment.apiUrl}/credentials/${id}/verifications`);
  }
}
