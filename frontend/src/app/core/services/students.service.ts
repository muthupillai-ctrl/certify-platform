import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { StudentDetail, StudentInvitationPreview, StudentInvitationResult, StudentList, StudentSignInLink, UpdateStudentRequest } from '../models/student.models';

// Staff: the institution's students (StudentsController).
@Injectable({ providedIn: 'root' })
export class StudentsService {
  constructor(private http: HttpClient) {}

  list(q: string, page: number, pageSize: number): Observable<StudentList> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (q.trim()) params = params.set('q', q.trim());
    return this.http.get<StudentList>(`${environment.apiUrl}/students`, { params });
  }

  // Changing the email changes their sign-in; both addresses are emailed.
  update(id: string, request: UpdateStudentRequest): Observable<StudentDetail> {
    return this.http.put<StudentDetail>(`${environment.apiUrl}/students/${id}`, request);
  }

  invitationPreview(): Observable<StudentInvitationPreview> {
    return this.http.get<StudentInvitationPreview>(`${environment.apiUrl}/students/invitations/preview`);
  }

  // Emails a set-up link to everyone not signed up (skipping anyone invited recently).
  inviteAll(): Observable<StudentInvitationResult> {
    return this.http.post<StudentInvitationResult>(`${environment.apiUrl}/students/invitations`, {});
  }

  get(id: string): Observable<StudentDetail> {
    return this.http.get<StudentDetail>(`${environment.apiUrl}/students/${id}`);
  }

  // Emails the student their set-up (or sign-in) link. queued: false when
  // the server can't build links (no public URL configured).
  emailLink(id: string): Observable<{ queued: boolean }> {
    return this.http.post<{ queued: boolean }>(`${environment.apiUrl}/students/${id}/email-link`, {});
  }

  signInLink(id: string, reset: boolean): Observable<StudentSignInLink> {
    return this.http.post<StudentSignInLink>(`${environment.apiUrl}/students/${id}/sign-in-link`, { reset });
  }
}
