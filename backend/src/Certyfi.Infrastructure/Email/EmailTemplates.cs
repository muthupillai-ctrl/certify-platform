using System.Net;
using System.Text;

namespace Certyfi.Infrastructure.Email;

// The emails Certyfi sends, as plain text plus a simple HTML version. Every
// value from data (names, titles) is HTML-encoded.
public static class EmailTemplates
{
    public const string CertificateRequested = "CertificateRequested";
    public const string CertificateIssued = "CertificateIssued";
    public const string CertificateCancelled = "CertificateCancelled";
    public const string CertificateRejected = "CertificateRejected";
    public const string CertificateSuspended = "CertificateSuspended";
    public const string CertificateReactivated = "CertificateReactivated";
    public const string CertificateRevoked = "CertificateRevoked";
    public const string PaymentReminder = "PaymentReminder";
    public const string StudentEmailChanged = "StudentEmailChanged";
    public const string PlatformPasswordChanged = "PlatformPasswordChanged";
    public const string PlatformEmailConfirm = "PlatformEmailConfirm";
    public const string PlatformEmailChanged = "PlatformEmailChanged";
    public const string PlatformPasswordReset = "PlatformPasswordReset";
    public const string StudentSignInLink = "StudentSignInLink";
    public const string StudentPasswordReset = "StudentPasswordReset";

    // isWelcome: the link sets up their account (no password yet) rather
    // than going to the sign-in page.
    public static EmailContent Requested(string name, string institution, string title, bool awaitingPayment, string portalUrl, bool isWelcome,
        DateTimeOffset? paymentDueAt = null)
    {
        // Payment needed: a task for the student, said so in the subject,
        // the text and the button.
        if (awaitingPayment)
        {
            var by = paymentDueAt is { } due ? $" by {Day(due)}" : "";
            return Build(
                subject: $"Action needed: pay for your certificate \"{title}\"{by}",
                greeting: name,
                paragraphs: new[]
                {
                    $"{institution} has requested your certificate \"{title}\".",
                    $"It won't be issued until it's paid. Please sign in to the student portal and complete the payment{by}; it's then issued."
                        + (paymentDueAt is not null ? " If it isn't paid by then, the request is cancelled." : ""),
                    AccessLine(isWelcome),
                },
                buttonText: isWelcome ? "Set up your account and pay" : "Sign in and pay",
                buttonUrl: portalUrl,
                institution: institution);
        }
        return Build(
            subject: $"Your certificate from {institution}: {title}",
            greeting: name,
            paragraphs: new[] { $"{institution} has requested your certificate \"{title}\".", "It will be issued once it's approved. You'll get another email then.", AccessLine(isWelcome) },
            buttonText: isWelcome ? "Set up your account" : "Sign in",
            buttonUrl: portalUrl,
            institution: institution);
    }

    public static EmailContent Issued(string name, string institution, string title, string verifyUrl, string portalUrl, bool isWelcome) =>
        Build(
            subject: $"Your certificate is ready: {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"{institution} has issued your certificate \"{title}\".",
                $"Anyone can check it is genuine at: {verifyUrl}",
                "You'll find all your certificates in the student portal. " + AccessLine(isWelcome),
            },
            buttonText: "View your certificate",
            buttonUrl: verifyUrl,
            institution: institution,
            secondaryText: isWelcome ? "Set up your account" : "Sign in to the student portal",
            secondaryUrl: portalUrl);

    public static EmailContent Reminder(string name, string institution, string title, DateTimeOffset dueAt, string portalUrl, bool isWelcome) =>
        Build(
            subject: $"Reminder: pay for your certificate by {Day(dueAt)}",
            greeting: name,
            paragraphs: new[]
            {
                $"Your certificate \"{title}\" from {institution} is waiting for payment.",
                $"Please pay by {Day(dueAt)}. If it isn't paid by then, the request is cancelled and the certificate won't be issued.",
                AccessLine(isWelcome),
            },
            buttonText: isWelcome ? "Set up your account and pay" : "Sign in and pay",
            buttonUrl: portalUrl,
            institution: institution);

    public static EmailContent CancelledUnpaid(string name, string institution, string title, DateTimeOffset dueAt) =>
        Build(
            subject: $"Certificate request cancelled (not paid): {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"Your certificate \"{title}\" from {institution} wasn't paid for by {Day(dueAt)}, so the request has been cancelled and it won't be issued.",
                $"If you still want the certificate, contact {institution}.",
            },
            buttonText: null,
            buttonUrl: null,
            institution: institution);

    // To the OLD address after a change, so a change the student didn't make
    // doesn't go unnoticed. The new address is masked.
    public static EmailContent EmailChanged(string name, string institution, string maskedNewEmail) =>
        Build(
            subject: $"Your {institution} student sign-in email was changed",
            greeting: name,
            paragraphs: new[]
            {
                $"The email address for your {institution} student portal account was changed to {maskedNewEmail}. From now on you sign in with that address, and emails about your certificates go there.",
                $"If you didn't expect this, contact {institution} straight away.",
            },
            buttonText: null,
            buttonUrl: null,
            institution: institution);

    // ---------- Platform (operator) accounts ----------
    private const string PlatformName = "Certyfi platform";

    public static EmailContent PlatformPasswordChangedNotice(string name) =>
        Build(
            subject: "Your Certyfi platform password was changed",
            greeting: name,
            paragraphs: new[]
            {
                "The password for your Certyfi platform account was just changed, and any other signed-in sessions were ended.",
                "If this wasn't you, contact another Certyfi platform administrator straight away.",
            },
            buttonText: null, buttonUrl: null, institution: PlatformName);

    public static EmailContent PlatformPasswordResetLink(string name, string resetUrl) =>
        Build(
            subject: "Reset your Certyfi platform password",
            greeting: name,
            paragraphs: new[]
            {
                "Someone asked to reset the password for your Certyfi platform account.",
                "Use the link below within one hour to choose a new one. If it wasn't you, ignore this email; your password stays the same.",
            },
            buttonText: "Choose a new password", buttonUrl: resetUrl, institution: PlatformName);

    public static EmailContent PlatformEmailConfirmation(string name, string confirmUrl) =>
        Build(
            subject: "Confirm your new Certyfi platform email",
            greeting: name,
            paragraphs: new[]
            {
                "You asked to use this address to sign in to your Certyfi platform account.",
                "Confirm it with the link below within 24 hours. Until you do, nothing changes. If you didn't ask for this, ignore this email.",
            },
            buttonText: "Confirm this email", buttonUrl: confirmUrl, institution: PlatformName);

    public static EmailContent PlatformEmailChangedNotice(string name, string maskedNewEmail) =>
        Build(
            subject: "Your Certyfi platform sign-in email was changed",
            greeting: name,
            paragraphs: new[]
            {
                $"The sign-in email for your Certyfi platform account was changed to {maskedNewEmail}.",
                "If this wasn't you, contact another Certyfi platform administrator straight away.",
            },
            buttonText: null, buttonUrl: null, institution: PlatformName);

    // "n•••@example.com"
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? email : $"{email[0]}•••{email[at..]}";
    }

    // "23 October 2026" (UTC).
    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    // No link: there's nothing to do. paid: they'd already paid (a test
    // payment today; with real payments this is where a refund is mentioned).
    public static EmailContent Cancelled(string name, string institution, string title, bool paid) =>
        Build(
            subject: $"Certificate request cancelled: {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"{institution} has cancelled the request for your certificate \"{title}\". It won't be issued, and there's nothing you need to do.",
                paid
                    ? $"You had already paid for it. Contact {institution} about the payment."
                    : "No payment is needed for it any more.",
                $"If you think this is a mistake, contact {institution}.",
            },
            buttonText: null,
            buttonUrl: null,
            institution: institution);

    // paid: Certyfi doesn't refund (payments are a test today); the
    // institution sorts it out, so the student is pointed to them.
    public static EmailContent Rejected(string name, string institution, string title, string? reason, bool paid, string portalUrl, bool isWelcome) =>
        Build(
            subject: $"Your certificate wasn't approved: {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"{institution} has reviewed your certificate \"{title}\" and didn't approve it, so it won't be issued.",
                string.IsNullOrWhiteSpace(reason) ? "No reason was given." : $"Reason: {reason}",
                paid
                    ? $"You had already paid for it. Contact {institution} about a refund."
                    : $"If you have questions, contact {institution}.",
            },
            buttonText: isWelcome ? "Set up your account" : "See your certificates",
            buttonUrl: portalUrl,
            institution: institution);

    // Suspension and revocation reasons are staff notes (and can name
    // misconduct), so these emails never quote them: the student is pointed
    // to the institution instead.
    public static EmailContent Suspended(string name, string institution, string title, string verifyUrl) =>
        Build(
            subject: $"Your certificate has been suspended: {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"{institution} has suspended your certificate \"{title}\". While it's suspended, anyone checking it is told it isn't currently valid.",
                $"A suspension can be lifted. Contact {institution} for the reason and what happens next.",
            },
            buttonText: "See its current status",
            buttonUrl: verifyUrl,
            institution: institution);

    public static EmailContent Reactivated(string name, string institution, string title, string verifyUrl) =>
        Build(
            subject: $"Your certificate is valid again: {title}",
            greeting: name,
            paragraphs: new[] { $"{institution} has lifted the suspension on your certificate \"{title}\". It shows as valid again to anyone who checks it." },
            buttonText: "View your certificate",
            buttonUrl: verifyUrl,
            institution: institution);

    public static EmailContent Revoked(string name, string institution, string title, string verifyUrl) =>
        Build(
            subject: $"Your certificate has been revoked: {title}",
            greeting: name,
            paragraphs: new[]
            {
                $"{institution} has revoked your certificate \"{title}\". It is no longer valid, and anyone checking it is told so. This can't be undone.",
                $"Contact {institution} for the reason, or if you think this is a mistake.",
            },
            buttonText: "See its current status",
            buttonUrl: verifyUrl,
            institution: institution);

    public static EmailContent SignInLink(string name, string institution, string portalUrl, bool isWelcome) =>
        Build(
            subject: isWelcome ? $"Set up your {institution} student account" : $"Your {institution} student portal",
            greeting: name,
            paragraphs: new[] { $"{institution} uses Certyfi to issue your certificates.", AccessLine(isWelcome) },
            buttonText: isWelcome ? "Set up your account" : "Sign in",
            buttonUrl: portalUrl,
            institution: institution);

    public static EmailContent PasswordReset(string name, string institution, string resetUrl) =>
        Build(
            subject: $"Reset your {institution} student portal password",
            greeting: name,
            paragraphs: new[]
            {
                "Someone asked to reset the password for your student portal account.",
                "Use the link below within one hour to choose a new password. If it wasn't you, ignore this email; your password stays the same.",
            },
            buttonText: "Choose a new password",
            buttonUrl: resetUrl,
            institution: institution);

    private static string AccessLine(bool isWelcome) => isWelcome
        ? "Use the link below to choose a password (it works for 30 days). After that, sign in with this email address."
        : "Sign in with this email address and your password.";

    private static EmailContent Build(string subject, string greeting, IEnumerable<string> paragraphs, string? buttonText, string? buttonUrl,
        string institution, string? secondaryText = null, string? secondaryUrl = null)
    {
        var text = new StringBuilder();
        text.Append("Hello ").Append(greeting).Append(",\n\n");
        foreach (var p in paragraphs) text.Append(p).Append("\n\n");
        if (buttonText is not null) text.Append(buttonText).Append(": ").Append(buttonUrl).Append('\n');
        if (secondaryText is not null) text.Append(secondaryText).Append(": ").Append(secondaryUrl).Append('\n');
        text.Append("\n").Append(institution).Append(" via Certyfi\n");

        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><body style=\"margin:0;padding:24px;background:#f3f4f6;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#111827\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\">");
        html.Append("<table role=\"presentation\" width=\"100%\" style=\"max-width:560px;background:#ffffff;border:1px solid #e5e7eb;border-radius:8px\" cellpadding=\"0\" cellspacing=\"0\"><tr><td style=\"padding:28px\">");
        html.Append("<p style=\"margin:0 0 16px;font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#6b7280\">").Append(E(institution)).Append(" · Certyfi</p>");
        html.Append("<p style=\"margin:0 0 16px;font-size:15px\">Hello ").Append(E(greeting)).Append(",</p>");
        foreach (var p in paragraphs) html.Append("<p style=\"margin:0 0 16px;font-size:15px;line-height:1.5\">").Append(E(p)).Append("</p>");
        if (buttonText is not null)
        {
            html.Append("<p style=\"margin:24px 0\"><a href=\"").Append(E(buttonUrl!)).Append("\" style=\"display:inline-block;padding:10px 18px;background:#111827;color:#ffffff;text-decoration:none;border-radius:6px;font-size:14px;font-weight:600\">")
                .Append(E(buttonText)).Append("</a></p>");
        }
        if (secondaryText is not null)
        {
            html.Append("<p style=\"margin:0 0 16px;font-size:14px\"><a href=\"").Append(E(secondaryUrl!)).Append("\" style=\"color:#111827\">").Append(E(secondaryText)).Append("</a></p>");
        }
        if (buttonUrl is not null)
        {
            html.Append("<p style=\"margin:24px 0 0;font-size:12px;color:#6b7280\">If the button doesn't work, copy this link into your browser:<br>")
                .Append(E(buttonUrl)).Append("</p>");
        }
        html.Append("</td></tr></table></td></tr></table></body></html>");
        return new EmailContent(subject, html.ToString(), text.ToString());
    }
}
