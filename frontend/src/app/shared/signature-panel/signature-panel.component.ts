import { Component, Input, OnChanges, SimpleChanges, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { VerificationService } from '../../core/services/verification.service';

type VerifyState = 'idle' | 'checking' | 'valid' | 'invalid' | 'error';

// A REAL verification panel, not a trust-me badge: it fetches the tenant's
// public key from the same anonymous endpoint a third party would use (see
// VerificationService.getSigningPublicKey), then performs the actual
// ECDSA/SHA-256 verification in the browser via the Web Crypto API —
// nothing here asks the viewer to trust this app's own say-so. Shown both
// on the public verify page and the admin Verification tab, since it's the
// same public data and the same check either way.
@Component({
  selector: 'app-signature-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './signature-panel.component.html',
})
export class SignaturePanelComponent implements OnChanges {
  @Input({ required: true }) tenantSlug!: string;
  @Input() canonicalHash: string | null = null;
  @Input() signature: string | null = null;
  @Input() signatureKeyId: string | null = null;

  readonly algorithm = signal<string | null>(null);
  readonly publicKeyPem = signal<string | null>(null);
  readonly keyFetchError = signal<string | null>(null);
  readonly state = signal<VerifyState>('idle');
  readonly showPublicKey = signal(false);
  readonly copied = signal(false);

  // The same check with OpenSSL, filled in with this certificate's values,
  // ready to paste into a macOS or Linux terminal (tested in bash and zsh).
  // The fingerprint must be saved exactly, with no line break at the end.
  readonly openSslCommands = computed(() => {
    const key = this.publicKeyPem()?.trim();
    if (!key || !this.canonicalHash || !this.signature) return null;
    return [
      "cat > key.pem <<'KEY'",
      key,
      'KEY',
      `printf '%s' '${this.canonicalHash}' > fingerprint.txt`,
      `printf '%s' '${this.signature}' | openssl base64 -d -A -out signature.der`,
      'openssl dgst -sha256 -verify key.pem -signature signature.der fingerprint.txt',
    ].join('\n');
  });

  constructor(private verification: VerificationService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['signatureKeyId'] || changes['tenantSlug']) && this.signatureKeyId) {
      this.fetchPublicKey();
    }
  }

  private fetchPublicKey(): void {
    this.keyFetchError.set(null);
    this.verification.getSigningPublicKey(this.tenantSlug, this.signatureKeyId!).subscribe({
      next: (key) => {
        this.algorithm.set(key.algorithm);
        this.publicKeyPem.set(key.publicKeyPem);
      },
      error: () => this.keyFetchError.set("Couldn't fetch the signing key."),
    });
  }

  async copyCommands(): Promise<void> {
    const commands = this.openSslCommands();
    if (!commands) return;
    try {
      await navigator.clipboard.writeText(commands + '\n');
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      /* clipboard blocked: the commands stay selectable on screen */
    }
  }

  // The three files OpenSSL needs, for systems without printf (e.g. Windows).
  download(file: 'key.pem' | 'signature.der' | 'fingerprint.txt'): void {
    const content =
      file === 'key.pem'
        ? new Blob([this.publicKeyPem() ?? ''], { type: 'application/x-pem-file' })
        : file === 'signature.der'
          ? new Blob([base64ToBytes(this.signature ?? '') as BlobPart], { type: 'application/octet-stream' })
          : new Blob([this.canonicalHash ?? ''], { type: 'text/plain' });
    const url = URL.createObjectURL(content);
    const a = document.createElement('a');
    a.href = url;
    a.download = file;
    a.click();
    URL.revokeObjectURL(url);
  }

  togglePublicKey(): void {
    this.showPublicKey.update((v) => !v);
  }

  async verifySignature(): Promise<void> {
    if (!this.canonicalHash || !this.signature || !this.publicKeyPem()) return;
    this.state.set('checking');
    try {
      const cryptoKey = await crypto.subtle.importKey(
        'spki',
        pemToArrayBuffer(this.publicKeyPem()!),
        { name: 'ECDSA', namedCurve: 'P-256' },
        false,
        ['verify'],
      );
      const rawSignature = derToRawEcdsaSignature(base64ToBytes(this.signature));
      const data = new TextEncoder().encode(this.canonicalHash);
      const valid = await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, cryptoKey, rawSignature, data);
      this.state.set(valid ? 'valid' : 'invalid');
    } catch {
      this.state.set('error');
    }
  }
}

function pemToArrayBuffer(pem: string): ArrayBuffer {
  const base64 = pem.replace(/-----BEGIN PUBLIC KEY-----/, '').replace(/-----END PUBLIC KEY-----/, '').replace(/\s+/g, '');
  return base64ToBytes(base64).buffer as ArrayBuffer;
}

function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

// The backend signs with DSASignatureFormat.Rfc3279DerSequence (DER —
// standard, interoperable with OpenSSL/Node/etc.) but the Web Crypto API's
// ECDSA verify specifically requires the raw (r || s, each 32 bytes for
// P-256) IEEE P1363 format instead — this converts between the two. See
// LocalTenantSigningKeyProvider's own comment on why DER was chosen over
// .NET's raw-format default.
function derToRawEcdsaSignature(der: Uint8Array): Uint8Array {
  let offset = 0;
  if (der[offset++] !== 0x30) throw new Error('Not a DER SEQUENCE.');
  let seqLen = der[offset++];
  if (seqLen & 0x80) {
    const lengthBytes = seqLen & 0x7f;
    seqLen = 0;
    for (let i = 0; i < lengthBytes; i++) seqLen = (seqLen << 8) | der[offset++];
  }

  function readInteger(): Uint8Array {
    if (der[offset++] !== 0x02) throw new Error('Not a DER INTEGER.');
    const len = der[offset++];
    let bytes = der.slice(offset, offset + len);
    offset += len;
    while (bytes.length > 1 && bytes[0] === 0x00) bytes = bytes.slice(1);
    const padded = new Uint8Array(32);
    padded.set(bytes, 32 - bytes.length);
    return padded;
  }

  const r = readInteger();
  const s = readInteger();
  const raw = new Uint8Array(64);
  raw.set(r, 0);
  raw.set(s, 32);
  return raw;
}
