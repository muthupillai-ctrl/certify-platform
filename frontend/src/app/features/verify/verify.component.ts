import { Component, ElementRef, OnInit, ViewChild, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { VerificationService } from '../../core/services/verification.service';
import { VerifyCredentialResponse } from '../../core/models/credential.models';
import { CertificatePreviewComponent } from '../../shared/certificate-preview/certificate-preview.component';
import { AnchorPanelComponent } from '../../shared/anchor-panel/anchor-panel.component';
import { SignaturePanelComponent } from '../../shared/signature-panel/signature-panel.component';
import { OfflineCheck, checkOfflineProof, offlineProofFromHash } from '../../core/offline-verification';
import { downloadCertificatePdf } from '../../core/certificate-pdf';
import { ModalComponent } from '../../shared/modal/modal.component';
import { QrCodeComponent } from '../../shared/qr-code/qr-code.component';

type ViewState = 'loading' | 'result' | 'offline' | 'not-found' | 'error';

@Component({
  selector: 'app-verify',
  standalone: true,
  imports: [CommonModule, RouterLink, CertificatePreviewComponent, SignaturePanelComponent, AnchorPanelComponent, ModalComponent, QrCodeComponent],
  templateUrl: './verify.component.html',
})
export class VerifyComponent implements OnInit {
  readonly state = signal<ViewState>('loading');
  readonly credential = signal<VerifyCredentialResponse | null>(null);
  readonly offlineCheck = signal<OfflineCheck | null>(null);
  readonly downloadingPdf = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly showQr = signal(false);

  // This page's own canonical URL. The QR code adds the signed offline
  // summary as a fragment (#o=...), which browsers never send to a server,
  // so scanning it online still just opens this page.
  pageUrl = '';
  tenantSlug = '';
  private credentialId = '';
  private offlineProof: string | null = null;

  @ViewChild('certificate') private certificateRef?: ElementRef<HTMLElement>;

  constructor(
    private route: ActivatedRoute,
    private verification: VerificationService,
  ) {}

  ngOnInit(): void {
    const tenantSlug = this.route.snapshot.paramMap.get('tenantSlug');
    const credentialId = this.route.snapshot.paramMap.get('credentialId');
    if (!tenantSlug || !credentialId) {
      this.state.set('not-found');
      return;
    }

    this.pageUrl = `${window.location.origin}/verify/${tenantSlug}/${credentialId}`;
    this.tenantSlug = tenantSlug;
    this.credentialId = credentialId;
    this.offlineProof = offlineProofFromHash(window.location.hash);

    this.verification.verify(tenantSlug, credentialId).subscribe({
      next: (credential) => {
        this.credential.set(credential);
        this.state.set('result');
        void this.verification.cacheKeys(tenantSlug);
      },
      error: (err) => {
        if (err.status === 404) {
          this.state.set('not-found');
        } else if (err.status === 0) {
          // No connection: fall back to the signed data in the QR code.
          void this.checkOffline();
        } else {
          this.state.set('error');
        }
      },
    });
  }

  qrData(c: VerifyCredentialResponse): string {
    return c.offlineProof ? `${this.pageUrl}#o=${c.offlineProof}` : this.pageUrl;
  }

  // Only active, signed certificates (ones approved before signing existed
  // have no institution signature to carry into a digital credential).
  canDownloadCredential(c: VerifyCredentialResponse): boolean {
    return c.status === 'Active' && !!c.signatureKeyId;
  }

  credentialDownloadUrl(): string {
    return this.verification.credentialDownloadUrl(this.tenantSlug, this.credentialId);
  }

  // LinkedIn's "Add licence or certification" form, pre-filled. The link
  // lets a recipient show the certificate on their profile; LinkedIn itself
  // doesn't verify anything, the link back to this page does.
  linkedInUrl(c: VerifyCredentialResponse): string {
    const params = new URLSearchParams({
      startTask: 'CERTIFICATION_NAME',
      name: c.achievementTitle,
      organizationName: c.institutionName,
      certUrl: this.pageUrl,
      certId: c.verificationCode,
    });
    // A digitised paper certificate was earned on its paper date.
    const awarded = c.originalIssueDate ?? c.issuedAt;
    if (awarded) {
      const issued = new Date(awarded);
      params.set('issueYear', String(issued.getFullYear()));
      params.set('issueMonth', String(issued.getMonth() + 1));
    }
    if (c.expiresOn) {
      const expires = new Date(c.expiresOn);
      params.set('expirationYear', String(expires.getFullYear()));
      params.set('expirationMonth', String(expires.getMonth() + 1));
    }
    return `https://www.linkedin.com/profile/add?${params.toString()}`;
  }

  async downloadPdf(c: VerifyCredentialResponse): Promise<void> {
    if (!this.certificateRef) return;
    this.downloadingPdf.set(true);
    this.actionError.set(null);
    try {
      await downloadCertificatePdf(this.certificateRef.nativeElement, `certificate-${c.verificationCode}.pdf`);
    } catch {
      this.actionError.set("Couldn't create the PDF in this browser. Use Print and choose \"Save as PDF\" instead.");
    } finally {
      this.downloadingPdf.set(false);
    }
  }

  location(city: string | null, country: string | null): string {
    return [city, country].filter((part) => !!part).join(', ');
  }

  print(): void {
    window.print();
  }

  private async checkOffline(): Promise<void> {
    if (!this.offlineProof) {
      this.offlineCheck.set({
        outcome: 'no-key',
        reason: "You're offline, and this link has no signed data to check offline. Connect to the internet to verify it.",
      });
    } else {
      this.offlineCheck.set(await checkOfflineProof(this.tenantSlug, this.credentialId, this.offlineProof));
    }
    this.state.set('offline');
  }
}
