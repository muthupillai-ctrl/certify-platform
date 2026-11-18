import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ApiClient, CreateApiClientRequest, CreateApiClientResponse, WebhookDelivery, WebhookTestResult } from '../models/api-client.models';

// Tenant-scoped, InstitutionAdmin only (see ApiClientsController).
@Injectable({ providedIn: 'root' })
export class ApiClientsService {
  constructor(private http: HttpClient) {}

  list(): Observable<ApiClient[]> {
    return this.http.get<ApiClient[]>(`${environment.apiUrl}/api-clients`);
  }

  create(request: CreateApiClientRequest): Observable<CreateApiClientResponse> {
    return this.http.post<CreateApiClientResponse>(`${environment.apiUrl}/api-clients`, request);
  }

  // secret: null keeps the current one (when only the URL changes).
  setWebhook(id: string, url: string, secret: string | null): Observable<ApiClient> {
    return this.http.put<ApiClient>(`${environment.apiUrl}/api-clients/${id}/webhook`, { url, secret });
  }

  removeWebhook(id: string): Observable<ApiClient> {
    return this.http.delete<ApiClient>(`${environment.apiUrl}/api-clients/${id}/webhook`);
  }

  testWebhook(id: string): Observable<WebhookTestResult> {
    return this.http.post<WebhookTestResult>(`${environment.apiUrl}/api-clients/${id}/webhook/test`, {});
  }

  webhookDeliveries(id: string): Observable<WebhookDelivery[]> {
    return this.http.get<WebhookDelivery[]>(`${environment.apiUrl}/api-clients/${id}/webhook/deliveries`);
  }

  revoke(id: string): Observable<void> {
    return this.http.post<void>(`${environment.apiUrl}/api-clients/${id}/revoke`, null);
  }
}
