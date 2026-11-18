import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { VerificationService } from '../../../core/services/verification.service';
import { CredentialFileCheck, allCachedInstitutions, checkCredentialFile } from '../../../core/offline-verification';

// Checks an Open Badges credential file in the browser. The file never
// leaves the device: the signature is checked locally against the issuer's
// key (fetched when online, or this device's saved copy when offline), and,
// when online, the certificate's current status is looked up by its id.
@Component({
  selector: 'app-verify-file',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './verify-file.component.html',
})
export class VerifyFileComponent {
  readonly result = signal<CredentialFileCheck | null>(null);
  readonly checking = signal(false);
  readonly fileName = signal<string | null>(null);
  readonly currentStatus = signal<string | null>(null);
  readonly dragging = signal(false);
  readonly savedInstitutions = allCachedInstitutions();

  constructor(private verification: VerificationService) {}

  onFileInput(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) void this.check(file);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) void this.check(file);
  }

  vc(): Record<string, any> {
    return this.result()?.claims['vc'] ?? {};
  }

  recipientName(): string | null {
    const ids: any[] = this.vc()['credentialSubject']?.['identifier'] ?? [];
    return ids.find((i) => i?.identityType === 'name' && !i?.hashed)?.identityHash ?? null;
  }

  private async check(file: File): Promise<void> {
    this.checking.set(true);
    this.result.set(null);
    this.currentStatus.set(null);
    this.fileName.set(file.name);
    if (file.size > 100_000) {
      this.result.set({
        signatureValid: false, keySource: 'none', header: {}, claims: {}, tenantSlug: null, credentialId: null, institutionName: null,
        problem: 'That file is too large to be a Certyfi credential.',
      });
      this.checking.set(false);
      return;
    }

    const result = await checkCredentialFile(await file.text(), (kid) => this.verification.fetchJwk(kid));
    this.result.set(result);
    this.checking.set(false);

    if (result.signatureValid && result.tenantSlug && result.credentialId) {
      this.verification.verify(result.tenantSlug, result.credentialId).subscribe({
        next: (c) => this.currentStatus.set(c.status),
        error: (err) => this.currentStatus.set(err.status === 404 ? 'NotFound' : 'Unknown'),
      });
    }
  }
}
