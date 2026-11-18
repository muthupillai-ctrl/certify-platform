namespace Certyfi.Domain.Enums;

public static class VerificationChannel
{
    public const string VerifyPage = "VerifyPage";           // someone opened the link or scanned the QR
    public const string CodeLookup = "CodeLookup";           // institution code + certificate code on the look-up page
    public const string CredentialDownload = "CredentialDownload"; // Open Badges credential file downloaded
    public const string Api = "Api";                         // the public JSON endpoint called directly
}
