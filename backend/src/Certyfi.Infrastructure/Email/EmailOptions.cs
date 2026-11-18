namespace Certyfi.Infrastructure.Email;

// "Email" configuration section.
//   Provider: None (default: emails are queued and marked Skipped, never
//   sent), Smtp (Mailpit locally), Resend (production) or Memory (tests).
public class EmailOptions
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "None";
    // "Name <address>" or just an address. With Resend, the address must be
    // on a domain verified in Resend.
    public string From { get; set; } = "Certyfi <no-reply@certyfi.local>";
    public string? ReplyTo { get; set; }

    public SmtpOptions Smtp { get; set; } = new();
    public ResendOptions Resend { get; set; } = new();

    // How often the background dispatcher looks for due emails; 0 turns
    // the background loop off (tests send explicitly).
    public int DispatchIntervalSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 8;

    public class SmtpOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 1025;
        public bool EnableSsl { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public class ResendOptions
    {
        public string? ApiKey { get; set; }
        public string BaseUrl { get; set; } = "https://api.resend.com";
    }
}
