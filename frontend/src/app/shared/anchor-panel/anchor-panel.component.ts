import { Component, Input, OnChanges, SimpleChanges, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { VerificationService } from '../../core/services/verification.service';
import { AnchorStatus } from '../../core/models/credential.models';

// Shows whether this credential's ledger entries have been anchored to
// Bitcoin via OpenTimestamps (see backend LedgerCheckpointService). Fetches
// its own status from the public verify endpoint so it works unchanged on
// the public verify page and the admin Verification tab.
@Component({
  selector: 'app-anchor-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './anchor-panel.component.html',
})
export class AnchorPanelComponent implements OnChanges {
  @Input({ required: true }) tenantSlug!: string;
  @Input({ required: true }) credentialId!: string;

  readonly status = signal<AnchorStatus | null>(null);
  readonly blockHeight = signal<number | null>(null);
  readonly merkleRootVerified = signal(false);
  readonly loading = signal(true);

  constructor(private verification: VerificationService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['tenantSlug'] || changes['credentialId']) && this.tenantSlug && this.credentialId) {
      this.load();
    }
  }

  proofUrl(): string {
    return this.verification.anchorProofUrl(this.tenantSlug, this.credentialId);
  }

  blockUrl(): string {
    return `https://blockstream.info/block-height/${this.blockHeight()}`;
  }

  private load(): void {
    this.loading.set(true);
    this.verification.verify(this.tenantSlug, this.credentialId).subscribe({
      next: (c) => {
        this.status.set(c.anchorStatus);
        this.blockHeight.set(c.anchorBlockHeight);
        this.merkleRootVerified.set(c.anchorMerkleRootVerified);
        this.loading.set(false);
      },
      error: () => {
        this.status.set(null);
        this.loading.set(false);
      },
    });
  }
}
