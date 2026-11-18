# Verifier guide

For employers, universities, embassies and anyone checking a certificate
issued through Certyfi. No account is needed.

## Check a certificate

### With the QR code or link

Scan the QR code printed on the certificate, or open the link you were
sent. Certyfi shows the certificate as the institution issued it, with its
status at the top.

![A verified certificate](/assets/help/img/verify.png)

### With the certificate code

Open **Verify a certificate** on Certyfi's home page and enter the
institution and the certificate code printed on the certificate (like
`K7QM-3XPD-9TWA`).

### With a digital credential file

If you were sent a credential file (Open Badges), open **Verify a
certificate** and choose to check a file. Certyfi checks its signature.

## What the result means

| You see | Meaning |
|---|---|
| **Valid certificate** | Genuine, and currently valid |
| **Suspended** | Temporarily not valid; ask the institution |
| **Revoked** | Withdrawn by the institution; not valid |
| **Expired** | It was valid until the date shown |
| **Not a valid certificate** | Certyfi has no such certificate: treat it as not genuine |

Certificates that were first issued on paper and digitised later say
**Originally issued on paper**, with the paper's date and number.

## Why you can trust it

- **Signed by the institution**: each certificate is digitally signed with
  the institution's own key. The page shows the signature and lets you
  check it yourself in the browser, or with OpenSSL.
- **Tamper-evident record**: every change (issued, suspended, revoked) is
  recorded in an audit trail that can't be rewritten quietly.
- **Timestamped on Bitcoin**: records are anchored to the Bitcoin
  blockchain, proving they existed at that time.
- **About the issuer**: the page shows the institution's registration,
  accreditation and contact details, as provided by the institution.

## Questions

**The page says "Not a valid certificate".**
Check the code was typed exactly. If it still fails, the document wasn't
issued through Certyfi: contact the institution named on it.

**Can I check a certificate offline?**
The QR code on Certyfi certificates carries a signed copy of the key
details, so a basic check works even without a connection; the full
status needs one.
