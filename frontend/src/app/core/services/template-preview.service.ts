import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { CertificateTemplate } from '../models/certificate-template.models';
import { TemplateKind } from '../models/achievement.models';
import { SignatoryName } from '../models/certificate-preview.models';
import { fromWire } from './certificate-templates.service';

interface TemplatePreviewWire {
  templateName: string;
  kind: TemplateKind;
  fieldSchemaJson: string;
  titleText: string;
  subtitleText: string | null;
  accentColor: string | null;
  fontFamily: string | null;
  layoutJson: string;
  backgroundImageUrl: string | null;
  institutionName: string;
  institutionLogoUrl: string | null;
  institutionPrimaryColor: string | null;
  expiresAt: string;
  signatories?: SignatoryName[] | null;
}

export interface TemplatePreview {
  template: CertificateTemplate;
  institution: { name: string; logoUrl: string | null; primaryColor: string | null };
  expiresAt: string;
}

// The public, signed template-preview link a partner app opens (see
// TemplatePreviewController on the API side).
@Injectable({ providedIn: 'root' })
export class TemplatePreviewService {
  constructor(private http: HttpClient) {}

  get(token: string): Observable<TemplatePreview> {
    return this.http.get<TemplatePreviewWire>(`${environment.apiUrl}/template-preview/${encodeURIComponent(token)}`).pipe(
      map((w) => ({
        template: fromWire({
          id: '', name: w.templateName, description: null, kind: w.kind, fieldSchemaJson: w.fieldSchemaJson, isActive: true,
          titleText: w.titleText, subtitleText: w.subtitleText, accentColor: w.accentColor, fontFamily: w.fontFamily,
          layoutJson: w.layoutJson, backgroundImageUrl: w.backgroundImageUrl, signatoryIds: [], signatories: w.signatories ?? [],
        }),
        institution: { name: w.institutionName, logoUrl: w.institutionLogoUrl, primaryColor: w.institutionPrimaryColor },
        expiresAt: w.expiresAt,
      })),
    );
  }
}
