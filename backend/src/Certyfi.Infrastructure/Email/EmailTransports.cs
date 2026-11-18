using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Email;

public record OutgoingEmail(Guid Id, string To, string? ToName, string Subject, string Html, string Text);

// Delivered: the provider accepted it. False with no exception: there's no
// provider (Skipped). An exception: try again later.
public record EmailSendResult(bool Delivered, string? ProviderMessageId = null);

public interface IEmailTransport
{
    Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct);
}

// Email:Provider = None.
public class NoEmailTransport : IEmailTransport
{
    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct) => Task.FromResult(new EmailSendResult(false));
}

// Email:Provider = Smtp: Mailpit in development (localhost:1025, view at
// http://localhost:8025), or any SMTP server.
public class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailOptions _options;

    public SmtpEmailTransport(IOptions<EmailOptions> options) => _options = options.Value;

    public async Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_options.From),
            Subject = email.Subject,
            Body = email.Text,
        };
        message.To.Add(new MailAddress(email.To, email.ToName));
        if (!string.IsNullOrWhiteSpace(_options.ReplyTo)) message.ReplyToList.Add(_options.ReplyTo);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(email.Html, null, "text/html"));

        using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port) { EnableSsl = _options.Smtp.EnableSsl };
        if (!string.IsNullOrEmpty(_options.Smtp.Username))
        {
            client.Credentials = new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password);
        }
        await client.SendMailAsync(message, ct);
        return new EmailSendResult(true);
    }
}

// Email:Provider = Resend (https://resend.com/docs/api-reference/emails/send-email).
// The outbox id is the idempotency key, so a retry after a timeout can't
// send the same email twice.
public class ResendEmailTransport : IEmailTransport
{
    private readonly HttpClient _http;
    private readonly EmailOptions _options;

    public ResendEmailTransport(HttpClient http, IOptions<EmailOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Resend.ApiKey)) throw new InvalidOperationException("Email:Resend:ApiKey is not set.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.Resend.BaseUrl.TrimEnd('/')}/emails")
        {
            Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["from"] = _options.From,
                ["to"] = new[] { email.ToName is { Length: > 0 } name ? $"{name.Replace("\"", "")} <{email.To}>" : email.To },
                ["subject"] = email.Subject,
                ["html"] = email.Html,
                ["text"] = email.Text,
                ["reply_to"] = string.IsNullOrWhiteSpace(_options.ReplyTo) ? null : _options.ReplyTo,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Resend.ApiKey);
        request.Headers.Add("Idempotency-Key", email.Id.ToString());

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Resend returned {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
        string? id = null;
        try
        {
            id = JsonDocument.Parse(body).RootElement.TryGetProperty("id", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            // Accepted; just no id to keep.
        }
        return new EmailSendResult(true, id);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

// Email:Provider = Memory: integration tests read what was "sent".
public class MemoryEmailTransport : IEmailTransport
{
    public ConcurrentQueue<OutgoingEmail> Sent { get; } = new();

    public Task<EmailSendResult> SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        Sent.Enqueue(email);
        return Task.FromResult(new EmailSendResult(true, $"memory-{email.Id:N}"));
    }
}
