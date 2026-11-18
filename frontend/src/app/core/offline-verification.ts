// Offline certificate checks, entirely in the browser (Web Crypto):
//
// 1. The signed summary in a certificate's QR code — the link's fragment,
//    "#o=<payload>.<signature>", which browsers never send to a server.
//    Signed ES256 over "<slug>/<credentialId>#<payload>" (see the API's
//    OfflineProofSigner).
// 2. An Open Badges 3.0 credential file (an ES256 JWT, see the API's
//    OpenBadgeCredentialIssuer).
//
// Both are checked against the institution's public keys, which this device
// caches (localStorage) whenever it verifies a certificate online. That is
// the trust anchor: a device that has never been online for an institution
// can't check its certificates offline — and says so, rather than trusting
// a key that arrived alongside the certificate.
//
// An offline check proves the certificate was issued as shown. It can't
// show a later revocation or suspension; callers must say so.

const KEY_CACHE_PREFIX = 'certyfi_keys_';

// A published signing key (RFC 7517); `kid` matches the key id in proofs.
export type PublicJwk = JsonWebKey & { kid?: string };

export interface CachedInstitutionKeys {
  slug: string;
  name: string;
  fetchedAt: string;
  keys: PublicJwk[];
}

export interface OfflineSummary {
  code: string;
  recipientName: string;
  achievementTitle: string;
  issuedOn: string; // yyyy-mm-dd
  expiresOn: string | null;
  // A digitised paper certificate: the paper's own date (yyyy-mm-dd).
  originalIssueOn: string | null;
  keyId: string;
}

export type OfflineCheck =
  | { outcome: 'valid'; summary: OfflineSummary; institution: CachedInstitutionKeys }
  | { outcome: 'invalid'; reason: string }
  | { outcome: 'no-key'; reason: string };

export function cacheInstitutionKeys(entry: CachedInstitutionKeys): void {
  try {
    localStorage.setItem(KEY_CACHE_PREFIX + entry.slug, JSON.stringify(entry));
  } catch {
    /* Storage full or blocked (private browsing): offline checks just won't be available. */
  }
}

export function cachedInstitutionKeys(slug: string): CachedInstitutionKeys | null {
  try {
    const raw = localStorage.getItem(KEY_CACHE_PREFIX + slug);
    return raw ? (JSON.parse(raw) as CachedInstitutionKeys) : null;
  } catch {
    return null;
  }
}

export function allCachedInstitutions(): CachedInstitutionKeys[] {
  const result: CachedInstitutionKeys[] = [];
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(KEY_CACHE_PREFIX)) {
        const entry = cachedInstitutionKeys(key.slice(KEY_CACHE_PREFIX.length));
        if (entry) result.push(entry);
      }
    }
  } catch {
    /* ignore */
  }
  return result.sort((a, b) => a.name.localeCompare(b.name));
}

// "o=<payload>.<sig>" from a location hash (with or without the leading #).
export function offlineProofFromHash(hash: string): string | null {
  const params = new URLSearchParams(hash.replace(/^#/, ''));
  const proof = params.get('o');
  return proof && /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(proof) ? proof : null;
}

export async function checkOfflineProof(tenantSlug: string, credentialId: string, proof: string): Promise<OfflineCheck> {
  const [payload, signature] = proof.split('.');
  let summary: OfflineSummary;
  try {
    const raw = JSON.parse(new TextDecoder().decode(base64UrlDecode(payload)));
    summary = {
      code: String(raw.c),
      recipientName: String(raw.n),
      achievementTitle: String(raw.a),
      issuedOn: compactDate(String(raw.d)),
      expiresOn: raw.e ? compactDate(String(raw.e)) : null,
      originalIssueOn: raw.o ? compactDate(String(raw.o)) : null,
      keyId: String(raw.k),
    };
  } catch {
    return { outcome: 'invalid', reason: 'The QR code data is damaged or not a Certyfi certificate.' };
  }

  const institution = cachedInstitutionKeys(tenantSlug);
  const jwk = institution?.keys.find((k) => k.kid === summary.keyId);
  if (!institution || !jwk) {
    return {
      outcome: 'no-key',
      reason:
        "This device doesn't have this institution's signing key yet. Verify any of its certificates once while online, then offline checks will work.",
    };
  }

  const ok = await verifyEs256(jwk, new TextEncoder().encode(`${tenantSlug}/${credentialId}#${payload}`), base64UrlDecode(signature));
  return ok
    ? { outcome: 'valid', summary, institution }
    : { outcome: 'invalid', reason: 'The signature does not match. This certificate may have been altered or forged.' };
}

export interface CredentialFileCheck {
  signatureValid: boolean;
  keySource: 'online' | 'cached' | 'none';
  header: Record<string, unknown>;
  claims: Record<string, any>;
  // From the credential's id (its verification page), when it is a Certyfi one.
  tenantSlug: string | null;
  credentialId: string | null;
  institutionName: string | null;
  problem: string | null;
}

// Checks an Open Badges VC-JWT. The key is the one at the JWT's `kid`
// (fetched when online, else from this device's cache for that institution).
export async function checkCredentialFile(jwt: string, fetchJwk: (url: string) => Promise<PublicJwk | null>): Promise<CredentialFileCheck> {
  const parts = jwt.trim().split('.');
  const empty: CredentialFileCheck = {
    signatureValid: false, keySource: 'none', header: {}, claims: {}, tenantSlug: null, credentialId: null, institutionName: null, problem: null,
  };
  if (parts.length !== 3) return { ...empty, problem: 'This is not a signed credential file (expected a JWT).' };

  let header: Record<string, any>;
  let claims: Record<string, any>;
  try {
    header = JSON.parse(new TextDecoder().decode(base64UrlDecode(parts[0])));
    claims = JSON.parse(new TextDecoder().decode(base64UrlDecode(parts[1])));
  } catch {
    return { ...empty, problem: 'The file is damaged and could not be read.' };
  }
  if (header['alg'] !== 'ES256' || typeof header['kid'] !== 'string') {
    return { ...empty, header, claims, problem: 'Unsupported signature type; Certyfi credentials use ES256.' };
  }

  const kid = header['kid'] as string;
  const match = /\/api\/v1\/verify\/([^/]+)\/jwks\/([^/?#]+)$/.exec(kid);
  const tenantSlug = match ? decodeURIComponent(match[1]) : null;
  const keyId = match ? decodeURIComponent(match[2]) : null;
  const idMatch = /\/verify\/([^/]+)\/([0-9a-f-]{36})$/i.exec(String(claims['vc']?.['id'] ?? claims['jti'] ?? ''));
  const base = { ...empty, header, claims, tenantSlug, credentialId: idMatch ? idMatch[2] : null };

  let jwk: PublicJwk | null = null;
  let keySource: CredentialFileCheck['keySource'] = 'none';
  try {
    jwk = await fetchJwk(kid);
    if (jwk) keySource = 'online';
  } catch {
    jwk = null;
  }
  const cached = tenantSlug ? cachedInstitutionKeys(tenantSlug) : null;
  if (!jwk && cached && keyId) {
    jwk = cached.keys.find((k) => k.kid === keyId) ?? null;
    if (jwk) keySource = 'cached';
  }
  if (!jwk) {
    return {
      ...base,
      problem: tenantSlug
        ? "Couldn't get the issuer's signing key: you're offline and this device hasn't verified this institution before."
        : "Couldn't get the issuer's signing key from the credential.",
    };
  }

  const signatureValid = await verifyEs256(jwk, new TextEncoder().encode(`${parts[0]}.${parts[1]}`), base64UrlDecode(parts[2]));
  return {
    ...base,
    signatureValid,
    keySource,
    institutionName: cached?.name ?? claims['vc']?.['issuer']?.['name'] ?? null,
    problem: signatureValid ? null : 'The signature does not match. This file may have been altered.',
  };
}

async function verifyEs256(jwk: PublicJwk, data: Uint8Array, signature: Uint8Array): Promise<boolean> {
  try {
    const { kty, crv, x, y } = jwk;
    const key = await crypto.subtle.importKey('jwk', { kty, crv, x, y }, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
    return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, signature, data);
  } catch {
    return false;
  }
}

export function base64UrlDecode(value: string): Uint8Array {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}

function compactDate(yyyymmdd: string): string {
  return /^\d{8}$/.test(yyyymmdd) ? `${yyyymmdd.slice(0, 4)}-${yyyymmdd.slice(4, 6)}-${yyyymmdd.slice(6, 8)}` : yyyymmdd;
}
