import { Component, Input, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { UsersService } from '../../core/services/users.service';
import { ASSIGNABLE_ROLES, TenantUserDetail } from '../../core/models/user.models';
import { ModalComponent } from '../modal/modal.component';

// An institution's staff list with a detail panel: view, edit (name, email,
// roles), deactivate/reactivate, and reset the password to a temporary one.
// Used by the Institution Admin's Users page (tenantId unset) and the
// SuperAdmin institution page (tenantId set). The API enforces the rules
// (e.g. an institution always keeps an active admin); this shows its messages.
@Component({
  selector: 'app-user-manager',
  standalone: true,
  imports: [CommonModule, FormsModule, ModalComponent],
  templateUrl: './user-manager.component.html',
})
export class UserManagerComponent implements OnInit {
  @Input() tenantId?: string;
  // The signed-in user's id, so their own row can't be deactivated or reset.
  @Input() currentUserId: string | null = null;

  readonly users = signal<TenantUserDetail[]>([]);
  readonly loading = signal(true);
  readonly listError = signal<string | null>(null);

  readonly selected = signal<TenantUserDetail | null>(null);
  readonly editing = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly temporaryPassword = signal<string | null>(null);
  readonly copied = signal(false);
  // Institution admins are managed only by Ahamsys (the platform view,
  // tenantId set); the institution's own view can't grant or change them.
  get roles(): readonly string[] {
    return this.tenantId ? ASSIGNABLE_ROLES : ASSIGNABLE_ROLES.filter((r) => r !== 'InstitutionAdmin');
  }

  // An admin account seen from the institution's own Users page: read-only.
  lockedAdmin(user: TenantUserDetail): boolean {
    return !this.tenantId && user.roles.includes('InstitutionAdmin');
  }

  editName = '';
  editEmail = '';
  editRoles: Record<string, boolean> = {};

  constructor(private usersService: UsersService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.usersService.list(this.tenantId).subscribe({
      next: (users) => {
        this.users.set(users);
        this.loading.set(false);
      },
      error: () => {
        this.listError.set('Failed to load users.');
        this.loading.set(false);
      },
    });
  }

  open(user: TenantUserDetail): void {
    this.selected.set(user);
    this.editing.set(false);
    this.error.set(null);
    this.temporaryPassword.set(null);
  }

  close(): void {
    this.selected.set(null);
    this.temporaryPassword.set(null);
  }

  isSelf(user: TenantUserDetail): boolean {
    return !!this.currentUserId && user.id === this.currentUserId;
  }

  startEdit(user: TenantUserDetail): void {
    this.editName = user.fullName;
    this.editEmail = user.email;
    this.editRoles = Object.fromEntries(this.roles.map((r) => [r, user.roles.includes(r)]));
    this.error.set(null);
    this.editing.set(true);
  }

  get chosenRoles(): string[] {
    return this.roles.filter((r) => this.editRoles[r]);
  }

  saveEdit(user: TenantUserDetail): void {
    this.run(this.usersService.update(user.id, { fullName: this.editName, email: this.editEmail, roles: this.chosenRoles }, this.tenantId), () =>
      this.editing.set(false),
    );
  }

  setActive(user: TenantUserDetail, active: boolean): void {
    if (!active && !confirm(`Deactivate ${user.fullName}? They won't be able to sign in until reactivated.`)) return;
    this.run(this.usersService.setActive(user.id, active, this.tenantId));
  }

  resetPassword(user: TenantUserDetail): void {
    if (!confirm(`Reset ${user.fullName}'s password? Their current password stops working immediately.`)) return;
    this.busy.set(true);
    this.error.set(null);
    this.usersService.resetPassword(user.id, this.tenantId).subscribe({
      next: (res) => {
        this.busy.set(false);
        this.copied.set(false);
        this.temporaryPassword.set(res.temporaryPassword);
        this.replace(res.user);
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err.error?.error ?? 'Failed to reset the password.');
      },
    });
  }

  copyTemporary(): void {
    const value = this.temporaryPassword();
    if (value) navigator.clipboard.writeText(value).then(() => this.copied.set(true));
  }

  private run(request: ReturnType<UsersService['update']>, after?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    request.subscribe({
      next: (user) => {
        this.busy.set(false);
        this.replace(user);
        after?.();
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err.error?.error ?? 'The change could not be saved.');
      },
    });
  }

  private replace(user: TenantUserDetail): void {
    this.users.update((list) => list.map((u) => (u.id === user.id ? user : u)));
    this.selected.set(user);
  }
}
