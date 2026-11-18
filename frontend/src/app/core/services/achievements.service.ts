import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Achievement, CreateAchievementRequest, UpdateAchievementRequest, FieldDefinition, TemplateKind } from '../models/achievement.models';

// Wire shapes exactly as ASP.NET serializes/expects them — FieldSchemaJson
// travels as a JSON-encoded string, not a nested object. This is the only
// place that (de)serializes it; every component above this service only
// ever sees a typed FieldDefinition[].
interface AchievementWire {
  id: string;
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  isActive: boolean;
  fieldSchemaJson: string;
  templateKind: TemplateKind;
}

interface CreateAchievementRequestWire {
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  fieldSchemaJson: string;
  templateId: string | null;
}

interface UpdateAchievementRequestWire {
  title: string;
  description: string | null;
  criteriaNarrative: string | null;
  validityPeriodMonths: number | null;
  fieldSchemaJson: string;
}

function fromWire(wire: AchievementWire): Achievement {
  let fieldSchema: FieldDefinition[];
  try {
    fieldSchema = JSON.parse(wire.fieldSchemaJson);
  } catch {
    fieldSchema = [];
  }
  return {
    id: wire.id,
    title: wire.title,
    description: wire.description,
    criteriaNarrative: wire.criteriaNarrative,
    validityPeriodMonths: wire.validityPeriodMonths,
    isActive: wire.isActive,
    fieldSchema,
    templateKind: wire.templateKind,
  };
}

@Injectable({ providedIn: 'root' })
export class AchievementsService {
  constructor(private http: HttpClient) {}

  list(): Observable<Achievement[]> {
    return this.http
      .get<AchievementWire[]>(`${environment.apiUrl}/achievements`)
      .pipe(map((wires) => wires.map(fromWire)));
  }

  create(request: CreateAchievementRequest): Observable<Achievement> {
    const wireRequest: CreateAchievementRequestWire = {
      title: request.title,
      description: request.description,
      criteriaNarrative: request.criteriaNarrative,
      validityPeriodMonths: request.validityPeriodMonths,
      fieldSchemaJson: JSON.stringify(request.fieldSchema),
      templateId: request.templateId,
    };
    return this.http
      .post<AchievementWire>(`${environment.apiUrl}/achievements`, wireRequest)
      .pipe(map(fromWire));
  }

  update(id: string, request: UpdateAchievementRequest): Observable<Achievement> {
    const wireRequest: UpdateAchievementRequestWire = {
      title: request.title,
      description: request.description,
      criteriaNarrative: request.criteriaNarrative,
      validityPeriodMonths: request.validityPeriodMonths,
      fieldSchemaJson: JSON.stringify(request.fieldSchema),
    };
    return this.http
      .put<AchievementWire>(`${environment.apiUrl}/achievements/${id}`, wireRequest)
      .pipe(map(fromWire));
  }
}
