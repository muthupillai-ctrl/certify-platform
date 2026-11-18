# Certyfi Partner API — Integration Guide

For developers connecting another system (an LMS, a student information
system, an HR platform) to Certyfi so it can issue certificates
automatically. Interactive reference: `https://<certyfi host>/api/v1/docs`.

## 1. Get access

An **Institution Admin** creates a key in Certyfi under **API keys**:

| Setting | Meaning |
|---|---|
| Name | Which system uses the key, e.g. "Acme LMS production" |
| Permissions | Any of `templates:read`, `credentials:read`, `credentials:write`, `credentials:revoke` |
| Issue without approval | Off (default): each certificate waits for an Approver at the institution. On: certificates are signed and active immediately, with the admin who created the key recorded as the approver. Requires `credentials:write`. |
| Students pay before issue | On (default for new keys): each certificate starts as `AwaitingPayment` until the student pays in the Certyfi student portal, then goes on as above. Requires `credentials:write`. |

The full key (`cfy_…`) is shown **once**. Store it as a secret on your server;
never put it in browser or mobile code. If it's lost or leaked, the admin
revokes it and creates a new one. Revocation takes effect on the next request.

## 2. Every request

```
X-Tenant-Slug: <institution code>
X-Api-Key: cfy_<prefix>_<secret>
Content-Type: application/json
```

Base URL: `https://<certyfi host>/api/v1/partner`

A key only sees credentials **it** created. Anything else returns 404.

## 3. Endpoints

| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/templates` | `templates:read` | Active certificate templates and their fields |
| POST | `/templates/{id}/preview-link` | `templates:read` | A short-lived link to a preview of the template |
| POST | `/credentials` | `credentials:write` | Issue a credential |
| GET | `/credentials/{id}` | `credentials:read` | One credential |
| GET | `/credentials?externalReference=…&limit=50` | `credentials:read` | Credentials this key created, newest first (limit max 200) |
| POST | `/credentials/{id}/cancel` | `credentials:write` | Withdraw a credential that isn't issued yet (`AwaitingPayment` or `PendingApproval`) |
| POST | `/credentials/{id}/revoke` | `credentials:revoke` | Ask for an Active or Suspended credential to be revoked (a person approves it) |

### List templates

```http
GET /api/v1/partner/templates
```

Response `200 OK`: the institution's active templates.

```json
[
  {
    "id": "3f6c…",
    "name": "Course Certificate",
    "description": "Issued on completing a course",
    "kind": "Certificate",
    "fieldSchema": [
      { "key": "rollNumber", "label": "Roll Number", "type": "text", "required": true },
      { "key": "grade", "label": "Grade", "type": "text" },
      { "key": "subjects", "label": "Subjects", "type": "table",
        "columns": [ { "key": "subject", "label": "Subject", "type": "text" },
                     { "key": "marks", "label": "Marks", "type": "number" } ] }
    ]
  }
]
```

- `id` is the `templateId` you send when issuing.
- `fieldSchema` lists the values the certificate can carry: send them in
  `fieldValues` under each `key`. `type` is `text`, `number`, `date` or
  `table` (a table has `columns` and takes an array of rows). All values are
  sent as strings.
- `required: true` means the field must be filled in. A field without it is
  optional.
- `kind` is `Certificate` or `MarksCard` (a transcript-style layout).

### Preview a template

Let your users see what a certificate will look like before they pick a
template: call this when they click "Preview" next to your template list,
then open the returned URL in a new tab or an iframe/modal.

```http
POST /api/v1/partner/templates/{id}/preview-link
```

No body. Response `200 OK`:

```json
{
  "url": "https://certyfi.ahamsys.com/template-preview/acme.3f6c….1791357564.9a1f…",
  "expiresAt": "2026-10-07T10:30:00+00:00"
}
```

- The page is Certyfi's own drawing of the template, with the institution's
  name and branding and sample data ("Jane Doe", made-up field values). It is
  marked **Sample** and watermarked, so it can't pass for a real certificate.
- The link needs no key and no login, so it can be opened in the user's
  browser. It works for **15 minutes**: ask for a fresh one each time you show
  a preview; don't store it.
- An unknown or deactivated template gets **404**. A link whose template has
  since been deactivated shows "no longer available"; an expired one says so.
- iframe example:

```html
<iframe src="{url}" style="width:100%;height:80vh;border:0" title="Certificate preview"></iframe>
```

### Issue a credential

```http
POST /api/v1/partner/credentials
```
```json
{
  "templateId": "3f6c…",
  "title": "Fire Safety Level 1",
  "recipientName": "Jane Doe",
  "recipientEmail": "jane@example.com",
  "fieldValues": { "rollNumber": "R-001" },
  "externalReference": "enrolment-8841",
  "studentId": "STU-10023"
}
```

- `templateId`: from `GET /templates`.
- `title`: the achievement name printed on the certificate. Credentials with
  the same template and title are grouped as one achievement.
- `fieldValues`: optional JSON object keyed by the template's field `key`s.
  Values are strings; a `table` field takes an array of row objects of strings.
  A field the template marks `"required": true` (see `GET /templates`) must be
  sent and not blank (a required table needs at least one row); otherwise the
  answer is `400` with `"error": "'Roll Number' is required."`. Other fields
  are optional. The institution can mark a field required at any time, so read
  the flags from `GET /templates` rather than hard-coding them.
- `externalReference`: **strongly recommended**. Your own id for this record
  (max 200 characters). See idempotency below.
- `studentId`: **recommended**. Your own unique id for the student (max 100
  characters). See "Students and the student portal" below.

Response `201 Created`:

```json
{
  "id": "9b1e…",
  "status": "PendingApproval",
  "title": "Fire Safety Level 1",
  "recipientName": "Jane Doe",
  "recipientEmail": "jane@example.com",
  "externalReference": "enrolment-8841",
  "createdAt": "2026-09-26T10:15:00+00:00",
  "issuedAt": null,
  "expiresOn": null,
  "revokedAt": null,
  "verificationUrl": null,
  "verificationCode": "K7QM-3XPD-9TWA",
  "revocationRequested": false,
  "studentId": "STU-10023",
  "paidAt": null,
  "paymentDueAt": "2026-10-10T10:15:00+00:00",
  "paymentDays": 14,
  "cancelledForNonPayment": false,
  "cancellationReason": null,
  "nextStep": {
    "code": "check_email_to_pay",
    "message": "Check your email from Certyfi (no-reply@ahamsys.com) to pay for this certificate by 10 October 2026. It won't be issued until it's paid. If you can't find the email, look in your spam folder."
  }
}
```

Every certificate names its signatories (name and designation, chosen by the
institution on the template). If the template has none yet, the request is
refused with `400` and an `error` saying so, before anything is created; ask
the institution to choose a signatory on that template.

`status` is one of `AwaitingPayment`, `PendingApproval`, `Active`, `Rejected`,
`Cancelled`, `Suspended`, `Revoked` or `Expired`. `verificationUrl` is filled in once the credential is
publicly verifiable (Active, Suspended, Revoked or Expired). Send that link or a
QR code of it to the recipient.

### What to show the student: `nextStep`

Every credential in a response (create, `GET /credentials/{id}` and the
list) carries `nextStep`: what the student should do now. Certyfi has already
emailed them the link they need, so you never handle their sign-in link.
Show `message` to the student as it is (it's written for them), and use
`code` for your own logic, e.g. to style it as a task or as information.

| `code` | When | The student should |
|---|---|---|
| `check_email_to_pay` | `AwaitingPayment` | Find Certyfi's email and pay by the date in the message (the same as `paymentDueAt`). **Show this one prominently: nothing happens until they act.** |
| `awaiting_approval` | `PendingApproval` | Wait; the institution is reviewing it |
| `issued` | `Active` | Open the email's link, or `verificationUrl`, to view and share it |
| `rejected` | `Rejected` | Read Certyfi's email for the reason |
| `cancelled` | `Cancelled` | Nothing (`cancelledForNonPayment` says if it was for not paying) |
| `suspended`, `revoked` | `Suspended`, `Revoked` | Read Certyfi's email for details |
| `expired` | `Expired` | Nothing |

New codes may be added later; show `message` for any code you don't
recognise. The email comes from the address in the message; mention spam
folders if students say they didn't get it.

### Students and the student portal

Certyfi keeps a list of students for each institution, one per email address.
When you issue a certificate:

1. Certyfi looks for the student with your `studentId`, then by
   `recipientEmail`, and creates the student if neither exists. (A student
   found by email gets your `studentId` attached.)
   - **Changed details:** if your `studentId` is known but you send a
     different `recipientEmail` or `recipientName`, Certyfi updates the
     student: they sign in with the new email from then on (same password),
     and the old address gets a notice that it changed. Certificates already
     issued keep the details they were issued with.
   - **409, nothing created:** the email belongs to a different student (with
     another `studentId`, or the one you're moving yours to).
2. **Certyfi emails the student** (you don't need to): the certificate's name,
   what happens next, and a link to the Certyfi student portal. Until the
   student has set a password the link is a personal set-up link (valid 30
   days); after that it's the institution's sign-in page. The student gets a
   second email when the certificate is issued, with a link to it, or if an
   approver rejects it (with the approver's reason; if they had paid, they're
   told to contact the institution about a refund). After issue, the student
   is also emailed if the certificate is suspended, reactivated or revoked;
   those emails don't quote the institution's reason. A retried
   request (same `externalReference`) doesn't email again.
3. In the portal the student sees all their certificates from the
   institution. If the key requires payment, the certificate is
   `AwaitingPayment` and the student clicks **Pay now** (a test payment for
   now; no money is taken). It then becomes `PendingApproval` (or `Active` for
   a key that issues without approval) and `paidAt` is set.

Poll `GET /credentials/{id}` to follow `status` as with any certificate.
Students who forget their password use "Forgot your password?" on the
sign-in page; institution staff can also email them a link from **Students**
in Certyfi.

### Payment deadline

Each institution sets **days to pay** in Certyfi (Settings, default 14; 0 =
no deadline). A credential that starts as `AwaitingPayment` comes back with
`paymentDueAt` (when it must be paid by) and, on the create response,
`paymentDays` (the setting it came from). Certyfi emails the student a
reminder 3 days before (halfway, for periods under a week). If it still
isn't paid at `paymentDueAt`, **Certyfi cancels it**: `status` becomes
`Cancelled` with `cancelledForNonPayment: true` and `cancellationReason: "Not
paid by the due date"`, and the student is emailed. Show your learner the due
date, but take the cancellation from Certyfi's status (not your own clock): a
student who pays in the last minute keeps their certificate. After the due
date the portal refuses payment.

### Webhooks: instant updates instead of polling

An Institution Admin can give an API key a **webhook** in Certyfi (API keys →
Webhook): your endpoint URL (`https://` only) and a signing secret you
generate. Certyfi then calls it whenever one of that key's credentials
changes: paid, approved, rejected, cancelled (by you, an approver or the
payment deadline), suspended, reactivated, revoked, a revocation request
rejected, and expired. Creating a credential doesn't call (you have the
response).

```http
POST {your URL}
Content-Type: application/json
X-Certyfi-Timestamp: 1760000000
X-Certyfi-Signature: v1=<hex>
```
```json
{
  "id": "evt_6f1c…",
  "type": "credential.updated",
  "createdAt": "2026-10-09T08:00:00+00:00",
  "data": { "credentialId": "9b1e…", "externalReference": "enrolment-8841", "status": "PendingApproval" }
}
```

- **Signature:** `v1=` + lowercase hex of HMAC-SHA256, key = the UTF-8
  secret, message = `{timestamp}.{raw body}`. Reject calls whose timestamp is
  more than 5 minutes off.
- **Treat a call as "go and look":** fetch `GET /credentials/{id}`; `status` in
  the call is only what it was at the time. Calls can arrive out of order or
  more than once.
- **Answer 2xx** quickly. `401` and other `4xx` (except `408`/`429`) stop that
  call and show it as failing on the key in Certyfi; `408`, `429`, `5xx`,
  timeouts (10 s) and connection errors are retried after 1 min, 5 min,
  30 min, 2 h, 6 h and 24 h, each signed with a fresh timestamp.
- **Send test** on the key's webhook window sends `{"type": "ping"}`.
- Keep polling occasionally (or when a user opens a record) as a safety net.

#### Checking the signature

Verify every call before trusting it. Use the **raw body exactly as received**
(before any JSON parsing), compare in constant time, and refuse timestamps
more than 5 minutes away. Several signatures may arrive comma-separated
(`v1=…,v1=…`) while a secret is being changed; one match is enough.

Test your code with this example. Secret `whsec_test_secret`, timestamp
`1760000000` and body
`{"id":"evt_1","type":"credential.updated","data":{"credentialId":"9b1e0000-0000-0000-0000-000000000001"}}`
must give `v1=07b3231847d2bee3d8ec09544d2b8db61bd4f04ae846da0e08715c58282549ba`.
(Skip the 5-minute check while testing with it.)

<!-- tabs -->
```javascript
// Node.js with Express. express.raw keeps the exact bytes Certyfi sent.
import crypto from 'node:crypto';
import express from 'express';

const app = express();
app.post('/webhooks/certyfi', express.raw({ type: 'application/json' }), (req, res) => {
  const timestamp = req.get('X-Certyfi-Timestamp') ?? '';
  const signatures = (req.get('X-Certyfi-Signature') ?? '').split(',').map((s) => s.trim());
  if (!/^\d+$/.test(timestamp) || Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return res.sendStatus(401);

  const expected = 'v1=' + crypto.createHmac('sha256', process.env.CERTYFI_WEBHOOK_SECRET)
    .update(`${timestamp}.`).update(req.body).digest('hex');
  const valid = signatures.some((s) => s.length === expected.length && crypto.timingSafeEqual(Buffer.from(s), Buffer.from(expected)));
  if (!valid) return res.sendStatus(401);

  const event = JSON.parse(req.body.toString('utf8'));
  res.sendStatus(200); // answer quickly, then fetch GET /credentials/{event.data.credentialId}
});
```

```python
# Python with Flask
import hashlib
import hmac
import os
import time

from flask import Flask, abort, request

app = Flask(__name__)
SECRET = os.environ["CERTYFI_WEBHOOK_SECRET"].encode()

@app.post("/webhooks/certyfi")
def certyfi_webhook():
    timestamp = request.headers.get("X-Certyfi-Timestamp", "")
    signatures = [s.strip() for s in request.headers.get("X-Certyfi-Signature", "").split(",")]
    if not timestamp.isdigit() or abs(time.time() - int(timestamp)) > 300:
        abort(401)

    body = request.get_data()  # raw bytes
    expected = "v1=" + hmac.new(SECRET, timestamp.encode() + b"." + body, hashlib.sha256).hexdigest()
    if not any(hmac.compare_digest(s, expected) for s in signatures):
        abort(401)

    event = request.get_json()
    return "", 200  # then fetch GET /credentials/{event["data"]["credentialId"]}
```

```php
<?php
// PHP: read the raw body from php://input
$timestamp = $_SERVER['HTTP_X_CERTYFI_TIMESTAMP'] ?? '';
$signatures = array_map('trim', explode(',', $_SERVER['HTTP_X_CERTYFI_SIGNATURE'] ?? ''));
$body = file_get_contents('php://input');
if (!ctype_digit($timestamp) || abs(time() - (int) $timestamp) > 300) {
    http_response_code(401);
    exit;
}

$expected = 'v1=' . hash_hmac('sha256', $timestamp . '.' . $body, getenv('CERTYFI_WEBHOOK_SECRET'));
$valid = false;
foreach ($signatures as $signature) {
    if (hash_equals($expected, $signature)) {
        $valid = true;
    }
}
if (!$valid) {
    http_response_code(401);
    exit;
}

$event = json_decode($body, true);
http_response_code(200); // then fetch GET /credentials/{$event['data']['credentialId']}
```

```csharp
// C# (ASP.NET Core minimal API)
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

app.MapPost("/webhooks/certyfi", async (HttpRequest request) =>
{
    var timestamp = request.Headers["X-Certyfi-Timestamp"].ToString();
    var signatures = request.Headers["X-Certyfi-Signature"].ToString().Split(',').Select(s => s.Trim());
    if (!long.TryParse(timestamp, out var seconds) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 300)
        return Results.Unauthorized();

    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();
    var key = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("CERTYFI_WEBHOOK_SECRET")!);
    var message = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
    var expected = Encoding.UTF8.GetBytes("v1=" + Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant());
    if (!signatures.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s), expected)))
        return Results.Unauthorized();

    using var evt = JsonDocument.Parse(body);
    return Results.Ok(); // then fetch GET /credentials/{data.credentialId}
});
```

```java
// Java 17 or later. With Spring: @RequestBody byte[] body, @RequestHeader for the two headers.
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.Arrays;
import java.util.HexFormat;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public final class CertyfiWebhook {
    public static boolean isValid(String secret, String timestamp, String signatureHeader, byte[] body) throws Exception {
        if (timestamp == null || !timestamp.matches("\\d+")
            || Math.abs(Instant.now().getEpochSecond() - Long.parseLong(timestamp)) > 300) return false;

        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
        mac.update((timestamp + ".").getBytes(StandardCharsets.UTF_8));
        byte[] expected = ("v1=" + HexFormat.of().formatHex(mac.doFinal(body))).getBytes(StandardCharsets.UTF_8);

        return signatureHeader != null && Arrays.stream(signatureHeader.split(","))
            .anyMatch(s -> MessageDigest.isEqual(s.trim().getBytes(StandardCharsets.UTF_8), expected));
    }
}
```
<!-- /tabs -->

### Payments and refunds

Payment is a test step for now: no money moves. Certyfi records when the
student paid (`paidAt`) but never refunds. If a paid certificate is rejected
or cancelled, the student is emailed to contact the institution, and the
institution handles any refund itself. When real payments are added, refunds
will follow from that provider.

### Idempotency: safe retries

If a request times out, send it again with the same `externalReference`. If
this key already created a credential with that reference, Certyfi returns it
with **200 OK** and creates nothing new. Without an `externalReference`, every
POST creates a new credential.

### Waiting for approval

For keys without auto-issue, poll `GET /credentials/{id}` (or look up by
`externalReference`) until `status` leaves `PendingApproval`. Polling every few
minutes is plenty; approvals are done by people.

### Cancelling before it's issued

If the student shouldn't get the certificate after all (enrolment cancelled,
duplicate request) and it is still `AwaitingPayment` or `PendingApproval`,
cancel it. Don't just delete your own record: the credential would stay
open in Certyfi and the student could still pay for it.

```http
POST /api/v1/partner/credentials/{id}/cancel
```
```json
{ "reason": "Enrolment cancelled" }
```

Returns **200** with `"status": "Cancelled"` and `cancelledAt`. It takes effect
at once (no approval needed, since nothing was issued): the student can no
longer pay for it, an approver can no longer approve it, and Certyfi emails
the student that it was cancelled (mentioning their payment if they'd already
paid). Cancelling an already-cancelled credential returns it again (200). A
credential that has been issued gets **409**: revoke it instead (below). If the
student pays or an approver approves at the same moment, only one of the two
happens; if yours lost, you get **409** and `GET /credentials/{id}` shows the
current status.

Revoking a credential that isn't issued yet gets **409** pointing here.

### Revoking: a person approves

Revoking is permanent, so Certyfi never lets an API key do it alone.

```http
POST /api/v1/partner/credentials/{id}/revoke
```
```json
{ "reason": "Enrolment cancelled" }
```

This records a revocation request and returns **202 Accepted** with the
credential, `"revocationRequested": true` and its status unchanged. Staff at the
institution see the request on their Revoke & suspend page and approve or
reject it. Once approved, `status` becomes `Revoked`; if rejected,
`revocationRequested` goes back to `false` and the credential stays as it was.
Poll `GET /credentials/{id}` to follow it. Sending the same request again while
one is waiting returns the same 202. A credential that isn't Active or
Suspended gets **409**.

## 4. Errors

| Status | Meaning |
|---|---|
| 400 | Invalid request; `error` explains which field |
| 401 | Missing, malformed, wrong or revoked key, or key used with the wrong `X-Tenant-Slug` |
| 403 | Key lacks the permission this endpoint needs |
| 404 | Not found, or created by a different key |
| 409 | Not allowed in the credential's current state (e.g. revoking a pending credential), or `recipientEmail` belongs to a different student than `studentId` |
| 429 | Rate limit exceeded; retry after the `Retry-After` header (seconds) |

Errors have the body `{ "error": "..." }`.

## 5. Limits

120 requests per minute per key. Over that you get `429`; wait for the number
of seconds in `Retry-After` and try again.

## 6. Examples

Issue a credential. The key comes from an environment variable: keep it on
your server, never in code that runs in a browser or app. Each example
stops on an error status and prints the new credential's `id` and `status`.

<!-- tabs -->
```bash
curl -s https://certyfi.ahamsys.com/api/v1/partner/credentials \
  -H "X-Tenant-Slug: acme" \
  -H "X-Api-Key: $CERTYFI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"templateId":"3f6c…","title":"Fire Safety Level 1","recipientName":"Jane Doe",
       "recipientEmail":"jane@example.com","fieldValues":{"rollNumber":"R-001"},
       "externalReference":"enrolment-8841"}'
```

```javascript
// Node.js 18 or later (built-in fetch)
const res = await fetch('https://certyfi.ahamsys.com/api/v1/partner/credentials', {
  method: 'POST',
  headers: {
    'X-Tenant-Slug': 'acme',
    'X-Api-Key': process.env.CERTYFI_API_KEY,
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({
    templateId: '3f6c…',
    title: 'Fire Safety Level 1',
    recipientName: 'Jane Doe',
    recipientEmail: 'jane@example.com',
    fieldValues: { rollNumber: 'R-001' },
    externalReference: 'enrolment-8841',
  }),
});
if (!res.ok) throw new Error(`${res.status}: ${await res.text()}`);
const credential = await res.json();
console.log(credential.id, credential.status);
```

```python
# Python 3 with requests (pip install requests)
import os
import requests

res = requests.post(
    "https://certyfi.ahamsys.com/api/v1/partner/credentials",
    headers={"X-Tenant-Slug": "acme", "X-Api-Key": os.environ["CERTYFI_API_KEY"]},
    json={
        "templateId": "3f6c…",
        "title": "Fire Safety Level 1",
        "recipientName": "Jane Doe",
        "recipientEmail": "jane@example.com",
        "fieldValues": {"rollNumber": "R-001"},
        "externalReference": "enrolment-8841",
    },
    timeout=30,
)
if not res.ok:
    raise RuntimeError(f"{res.status_code}: {res.text}")
credential = res.json()
print(credential["id"], credential["status"])
```

```php
<?php
// PHP 7.4 or later with the curl extension
$ch = curl_init('https://certyfi.ahamsys.com/api/v1/partner/credentials');
curl_setopt_array($ch, [
    CURLOPT_POST => true,
    CURLOPT_RETURNTRANSFER => true,
    CURLOPT_TIMEOUT => 30,
    CURLOPT_HTTPHEADER => [
        'X-Tenant-Slug: acme',
        'X-Api-Key: ' . getenv('CERTYFI_API_KEY'),
        'Content-Type: application/json',
    ],
    CURLOPT_POSTFIELDS => json_encode([
        'templateId' => '3f6c…',
        'title' => 'Fire Safety Level 1',
        'recipientName' => 'Jane Doe',
        'recipientEmail' => 'jane@example.com',
        'fieldValues' => ['rollNumber' => 'R-001'],
        'externalReference' => 'enrolment-8841',
    ]),
]);
$body = curl_exec($ch);
$status = curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
curl_close($ch);
if ($status < 200 || $status >= 300) {
    throw new RuntimeException("$status: $body");
}
$credential = json_decode($body, true);
echo $credential['id'], ' ', $credential['status'], PHP_EOL;
```

```csharp
// C# (.NET 6 or later)
using System.Net.Http.Json;
using System.Text.Json;

using var http = new HttpClient { BaseAddress = new Uri("https://certyfi.ahamsys.com/api/v1/partner/") };
http.DefaultRequestHeaders.Add("X-Tenant-Slug", "acme");
http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable("CERTYFI_API_KEY"));

var res = await http.PostAsJsonAsync("credentials", new
{
    templateId = "3f6c…",
    title = "Fire Safety Level 1",
    recipientName = "Jane Doe",
    recipientEmail = "jane@example.com",
    fieldValues = new { rollNumber = "R-001" },
    externalReference = "enrolment-8841",
});
if (!res.IsSuccessStatusCode)
    throw new Exception($"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
var credential = await res.Content.ReadFromJsonAsync<JsonElement>();
Console.WriteLine($"{credential.GetProperty("id")} {credential.GetProperty("status")}");
```

```java
// Java 15 or later (java.net.http); parse the JSON with Jackson or Gson
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;

public class IssueCredential {
    public static void main(String[] args) throws Exception {
        String json = """
            {"templateId":"3f6c…","title":"Fire Safety Level 1","recipientName":"Jane Doe",
             "recipientEmail":"jane@example.com","fieldValues":{"rollNumber":"R-001"},
             "externalReference":"enrolment-8841"}
            """;
        HttpRequest request = HttpRequest.newBuilder(URI.create("https://certyfi.ahamsys.com/api/v1/partner/credentials"))
            .header("X-Tenant-Slug", "acme")
            .header("X-Api-Key", System.getenv("CERTYFI_API_KEY"))
            .header("Content-Type", "application/json")
            .POST(HttpRequest.BodyPublishers.ofString(json))
            .build();
        HttpResponse<String> response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
        if (response.statusCode() < 200 || response.statusCode() >= 300)
            throw new RuntimeException(response.statusCode() + ": " + response.body());
        System.out.println(response.body());
    }
}
```
<!-- /tabs -->
