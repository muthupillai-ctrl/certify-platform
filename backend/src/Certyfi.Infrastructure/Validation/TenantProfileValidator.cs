using System.Net.Mail;
using Certyfi.Domain.Entities;

namespace Certyfi.Infrastructure.Validation;

// Institution profile fields, edited by the institution's own admin or a
// SuperAdmin. Values are trimmed and blank becomes null; limits match
// PlatformDbContext's column sizes.
public record TenantProfileInput(
    string? Name, string? LegalName, string? WebsiteUrl, string? RegistrationNumber, string? AccreditationBody, string? About,
    string? AddressLine1, string? AddressLine2, string? City, string? Region, string? PostalCode, string? Country,
    string? ContactEmail, string? ContactPhone);

public static class TenantProfileValidator
{
    // Applies `input` to `tenant` when valid; returns an error message otherwise
    // (and changes nothing).
    public static string? Apply(Tenant tenant, TenantProfileInput input)
    {
        var name = Clean(input.Name);
        if (name is null || name.Length > 200) return "Institution name is required (max 200 characters).";

        var fields = new (string Label, string? Value, int Max)[]
        {
            ("Legal name", Clean(input.LegalName), 300), ("Website", Clean(input.WebsiteUrl), 500),
            ("Registration number", Clean(input.RegistrationNumber), 100), ("Accrediting body", Clean(input.AccreditationBody), 200),
            ("About", Clean(input.About), 1000), ("Address line 1", Clean(input.AddressLine1), 200),
            ("Address line 2", Clean(input.AddressLine2), 200), ("City", Clean(input.City), 100), ("Region", Clean(input.Region), 100),
            ("Postal code", Clean(input.PostalCode), 20), ("Country", Clean(input.Country), 100),
            ("Contact email", Clean(input.ContactEmail), 320), ("Contact phone", Clean(input.ContactPhone), 40),
        };
        foreach (var (label, value, max) in fields)
        {
            if (value is { Length: var length } && length > max) return $"{label} must be at most {max} characters.";
        }

        var website = Clean(input.WebsiteUrl);
        if (website is not null && !(Uri.TryCreate(website, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            return "Website must be a full address starting with https:// or http://.";
        var email = Clean(input.ContactEmail);
        if (email is not null && !MailAddress.TryCreate(email, out _))
            return "Contact email is not a valid email address.";

        tenant.Name = name;
        tenant.LegalName = Clean(input.LegalName);
        tenant.WebsiteUrl = website;
        tenant.RegistrationNumber = Clean(input.RegistrationNumber);
        tenant.AccreditationBody = Clean(input.AccreditationBody);
        tenant.About = Clean(input.About);
        tenant.AddressLine1 = Clean(input.AddressLine1);
        tenant.AddressLine2 = Clean(input.AddressLine2);
        tenant.City = Clean(input.City);
        tenant.Region = Clean(input.Region);
        tenant.PostalCode = Clean(input.PostalCode);
        tenant.Country = Clean(input.Country);
        tenant.ContactEmail = email;
        tenant.ContactPhone = Clean(input.ContactPhone);
        tenant.ProfileUpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
