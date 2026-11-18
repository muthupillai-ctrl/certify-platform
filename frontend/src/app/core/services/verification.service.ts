import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable, firstValueFrom, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { VerifyCredentialResponse, FieldValues, TenantSigningPublicKey, VerificationLookupResponse } from '../models/credential.models';
import { PublicJwk, cacheInstitutionKeys } from '../offline-verification';
import { FieldDefinition } from '../models/achievement.models';
import { TemplateLayout } from '../models/certificate-preview.models';

// Wire shape exactly as ASP.NET serializes VerifyCredentialResponse —
// FieldSchemaJson/FieldValuesJson/LayoutJson travel as JSON-encoded
// strings. This is the only place that (de)serializes them; the component
// only ever sees typed FieldDefinition[]/FieldValues/TemplateLayout.
interface VerifyCredentialResponseWire extends Omit<VerifyCredentialResponse, 'fieldSchema' | 'fieldValues' | 'layout'> {
  fieldSchemaJson: string;
  fieldValuesJson: string;
  layoutJson: string;
}

function fromWire(wire: VerifyCredentialResponseWire): VerifyCredentialResponse {
  let fieldSchema: FieldDefinition[];
  let fieldValues: FieldValues;
  let layout: TemplateLayout | null;
  try {
    fieldSchema = JSON.parse(wire.fieldSchemaJson);
  } catch {
    fieldSchema = [];
  }
  try {
    fieldValues = JSON.parse(wire.fieldValuesJson);
  } catch {
    fieldValues = {};
  }
  try {
    layout = JSON.parse(wire.layoutJson);
  } catch {
    layout = null;
  }
  const { fieldSchemaJson: _s, fieldValuesJson: _v, layoutJson: _l, ...rest } = wire;
  return { ...rest, fieldSchema, fieldValues, layout };
}

// Public — no tenant/auth context needed. Backend resolves the tenant from
// the URL path itself (see VerificationController).
@Injectable({ providedIn: 'root' })
export class VerificationService {
  constructor(private http: HttpClient) {}

  // Marked as the verify page so the institution's verification history
  // counts it as a page visit rather than a direct API call.
  verify(tenantSlug: string, credentialId: string): Observable<VerifyCredentialResponse> {
    return this.http
      .get<VerifyCredentialResponseWire>(`${environment.apiUrl}/verify/${tenantSlug}/${credentialId}`, {
        headers: new HttpHeaders({ 'X-Certyfi-Client': 'verify-page' }),
      })
      .pipe(map(fromWire));
  }

  lookup(tenantSlug: string, code: string): Observable<VerificationLookupResponse> {
    return this.http.get<VerificationLookupResponse>(
      `${environment.apiUrl}/verify/${encodeURIComponent(tenantSlug.trim().toLowerCase())}/lookup/${encodeURIComponent(code.trim())}`,
    );
  }

  // A digitised paper certificate: its old printed number plus the
  // recipient's name as printed (old numbers are easy to guess).
  lookupPaper(tenantSlug: string, number: string, name: string): Observable<VerificationLookupResponse> {
    return this.http.get<VerificationLookupResponse>(
      `${environment.apiUrl}/verify/${encodeURIComponent(tenantSlug.trim().toLowerCase())}/lookup-paper`,
      { params: { number: number.trim(), name: name.trim() } },
    );
  }

  credentialDownloadUrl(tenantSlug: string, credentialId: string): string {
    return `${environment.apiUrl}/verify/${tenantSlug}/${credentialId}/credential`;
  }

  // Saves the institution's public keys on this device, so its
  // certificates can be checked offline later. Called after every
  // successful online verification; failures are ignored.
  async cacheKeys(tenantSlug: string): Promise<void> {
    try {
      const jwks = await firstValueFrom(
        this.http.get<{ institution: { slug: string; name: string }; keys: PublicJwk[] }>(`${environment.apiUrl}/verify/${tenantSlug}/jwks`),
      );
      cacheInstitutionKeys({ slug: jwks.institution.slug, name: jwks.institution.name, fetchedAt: new Date().toISOString(), keys: jwks.keys });
    } catch {
      /* offline or unavailable: keep whatever is cached */
    }
  }

  // The key a credential file's `kid` points to. Only Certyfi key URLs on
  // this deployment's API are fetched — never an arbitrary URL from a file.
  async fetchJwk(kid: string): Promise<PublicJwk | null> {
    const apiBase = new URL(environment.apiUrl, window.location.origin).href.replace(/\/$/, '');
    if (!kid.startsWith(apiBase + '/verify/')) return null;
    return firstValueFrom(this.http.get<PublicJwk>(kid));
  }

  // Same anonymous, no-tenant-header endpoint a real third-party verifier
  // would call directly — this service doesn't do anything the browser
  // devtools couldn't already show, which is the point.
  getSigningPublicKey(tenantSlug: string, keyId: string): Observable<TenantSigningPublicKey> {
    return this.http.get<TenantSigningPublicKey>(`${environment.apiUrl}/verify/${tenantSlug}/signing-keys/${keyId}`);
  }

  anchorProofUrl(tenantSlug: string, credentialId: string): string {
    return `${environment.apiUrl}/verify/${tenantSlug}/${credentialId}/anchor-proof`;
  }
}
