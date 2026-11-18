import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CertificateTemplate, CreateCertificateTemplateRequest, UpdateCertificateTemplateRequest } from '../models/certificate-template.models';
import { FieldDefinition, TemplateKind } from '../models/achievement.models';
import { SignatoryName, TemplateLayout } from '../models/certificate-preview.models';

// Wire shapes exactly as ASP.NET serializes/expects them — FieldSchemaJson/
// LayoutJson travel as JSON-encoded strings, matching achievements.service.ts's
// existing fromWire pattern. This is the only place that (de)serializes them.
export interface CertificateTemplateWire {
  id: string;
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchemaJson: string;
  isActive: boolean;
  titleText: string;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  layoutJson: string;
  backgroundImageUrl: string | null;
  signatoryIds: string[];
  signatories: SignatoryName[];
  requiresFinalApproval?: boolean;
  finalApprovalOffPending?: boolean;
}

interface CreateCertificateTemplateRequestWire {
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchemaJson: string;
  titleText?: string;
  subtitleText?: string | null;
  accentColor?: string | null;
  fontFamily?: string | null;
  layoutJson?: string;
  backgroundImageUrl?: string | null;
  signatoryIds?: string[];
  requiresFinalApproval?: boolean;
}

interface UpdateCertificateTemplateRequestWire {
  name: string;
  description: string | null;
  kind: TemplateKind;
  fieldSchemaJson: string;
  titleText?: string;
  subtitleText?: string | null;
  accentColor?: string | null;
  fontFamily?: string | null;
  layoutJson?: string;
  backgroundImageUrl?: string | null;
  signatoryIds?: string[];
  requiresFinalApproval?: boolean;
}

export function fromWire(wire: CertificateTemplateWire): CertificateTemplate {
  let fieldSchema: FieldDefinition[];
  try {
    fieldSchema = JSON.parse(wire.fieldSchemaJson);
  } catch {
    fieldSchema = [];
  }
  let layout: TemplateLayout;
  try {
    layout = JSON.parse(wire.layoutJson);
  } catch {
    layout = { canvasWidth: 700, canvasHeight: 600, elements: [] };
  }
  return {
    id: wire.id,
    name: wire.name,
    description: wire.description,
    kind: wire.kind,
    fieldSchema,
    isActive: wire.isActive,
    titleText: wire.titleText,
    subtitleText: wire.subtitleText,
    accentColor: wire.accentColor,
    fontFamily: wire.fontFamily,
    layout,
    backgroundImageUrl: wire.backgroundImageUrl,
    signatoryIds: wire.signatoryIds ?? [],
    signatories: wire.signatories ?? [],
    requiresFinalApproval: wire.requiresFinalApproval ?? false,
    finalApprovalOffPending: wire.finalApprovalOffPending ?? false,
  };
}

@Injectable({ providedIn: 'root' })
export class CertificateTemplatesService {
  constructor(private http: HttpClient) {}

  list(): Observable<CertificateTemplate[]> {
    return this.http
      .get<CertificateTemplateWire[]>(`${environment.apiUrl}/certificate-templates`)
      .pipe(map((wires) => wires.map(fromWire)));
  }

  create(request: CreateCertificateTemplateRequest): Observable<CertificateTemplate> {
    const wireRequest: CreateCertificateTemplateRequestWire = {
      name: request.name,
      description: request.description,
      kind: request.kind,
      fieldSchemaJson: JSON.stringify(request.fieldSchema),
      titleText: request.titleText,
      subtitleText: request.subtitleText,
      accentColor: request.accentColor,
      fontFamily: request.fontFamily,
      layoutJson: request.layout ? JSON.stringify(request.layout) : undefined,
      backgroundImageUrl: request.backgroundImageUrl,
      signatoryIds: request.signatoryIds,
      requiresFinalApproval: request.requiresFinalApproval,
    };
    return this.http
      .post<CertificateTemplateWire>(`${environment.apiUrl}/certificate-templates`, wireRequest)
      .pipe(map(fromWire));
  }

  deactivate(id: string): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/certificate-templates/${id}/deactivate`, {});
  }

  update(id: string, request: UpdateCertificateTemplateRequest): Observable<CertificateTemplate> {
    const wireRequest: UpdateCertificateTemplateRequestWire = {
      name: request.name,
      description: request.description,
      kind: request.kind,
      fieldSchemaJson: JSON.stringify(request.fieldSchema),
      titleText: request.titleText,
      subtitleText: request.subtitleText,
      accentColor: request.accentColor,
      fontFamily: request.fontFamily,
      layoutJson: request.layout ? JSON.stringify(request.layout) : undefined,
      backgroundImageUrl: request.backgroundImageUrl,
      signatoryIds: request.signatoryIds,
      requiresFinalApproval: request.requiresFinalApproval,
    };
    return this.http
      .put<CertificateTemplateWire>(`${environment.apiUrl}/certificate-templates/${id}`, wireRequest)
      .pipe(map(fromWire));
  }

  // Permanent — the backend rejects it with 409 when the template has been
  // used to create an achievement; deactivate() is the only option then.
  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/certificate-templates/${id}`);
  }
}
