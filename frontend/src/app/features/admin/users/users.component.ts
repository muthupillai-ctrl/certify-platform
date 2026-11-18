import { Component, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { UsersService } from '../../../core/services/users.service';
import { ASSIGNABLE_ROLES } from '../../../core/models/user.models';
import { AuthService } from '../../../core/services/auth.service';
import { UserManagerComponent } from '../../../shared/user-manager/user-manager.component';

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [CommonModule, FormsModule, UserManagerComponent],
  templateUrl: './users.component.html',
})
export class UsersComponent {
  // The list, detail and edit panel live in the shared UserManagerComponent
  // (also used by the SuperAdmin institution page); this page adds creation.
  @ViewChild('manager') private manager?: UserManagerComponent;
  readonly errorMessage = signal<string | null>(null);
  readonly showForm = signal(false);
  readonly submitting = signal(false);
  // Institution admins are added only by Ahamsys.
  readonly roles = ASSIGNABLE_ROLES.filter((r) => r !== 'InstitutionAdmin');

  email = '';
  fullName = '';
  initialPassword = '';
  role: string = ASSIGNABLE_ROLES.find((r) => r !== 'InstitutionAdmin')!;

  constructor(
    private usersService: UsersService,
    private auth: AuthService,
  ) {}

  get currentUserId(): string | null {
    return this.auth.currentUser()?.sub ?? null;
  }

  toggleForm(): void {
    this.errorMessage.set(null);
    this.showForm.set(!this.showForm());
  }

  submit(): void {
    this.errorMessage.set(null);
    this.submitting.set(true);

    this.usersService
      .create({
        email: this.email,
        fullName: this.fullName,
        initialPassword: this.initialPassword,
        role: this.role,
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.showForm.set(false);
          this.resetForm();
          this.manager?.load();
        },
        error: (err) => {
          this.submitting.set(false);
          this.errorMessage.set(err.error?.error ?? 'Failed to create user.');
        },
      });
  }

  private resetForm(): void {
    this.email = '';
    this.fullName = '';
    this.initialPassword = '';
    this.role = this.roles[0];
  }
}
