using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Certyfi.Api.Controllers;
using Certyfi.Infrastructure.Validation;

namespace Certyfi.Api.Imports;

// Checks one row of a paper-certificate import and turns it into what a
// certificate needs. Used by both the dry run (check) and the real import,
// so what the check says is exactly what the import does.
public static partial class ImportRowValidator
{
    public const int MaxNameLength = 200;
    public const int MaxTitleLength = 300;
    public const int MaxLegacyNumberLength = 100;

    public record Valid(string RecipientName, string RecipientEmail, string Title, DateOnly OriginalIssueDate,
        string? LegacyNumber, string? LegacyNumberKey, DateTimeOffset? ExpiresOn, string FieldValuesJson);

    // Errors stop the row being imported; the check reports them per row.
    public static (Valid? Row, List<string> Errors) Validate(ImportRow row, string? batchTitle, string dateFormat, string fieldSchemaJson)
    {
        var errors = new List<string>();

        var name = Clean(row.RecipientName);
        if (name.Length == 0) errors.Add("Recipient name is missing.");
        else if (name.Length > MaxNameLength) errors.Add($"Recipient name is longer than {MaxNameLength} characters.");

        var email = Clean(row.RecipientEmail).ToLowerInvariant();
        if (email.Length > 0 && (email.Length > 320 || !EmailPattern().IsMatch(email))) errors.Add($"\"{email}\" is not a valid email address.");

        var title = Clean(row.AchievementTitle);
        if (title.Length == 0) title = Clean(batchTitle);
        if (title.Length == 0) errors.Add("Achievement is missing.");
        else if (title.Length > MaxTitleLength) errors.Add($"Achievement is longer than {MaxTitleLength} characters.");

        DateOnly original = default;
        var originalText = Clean(row.OriginalIssueDate);
        if (originalText.Length == 0) errors.Add("Original issue date is missing.");
        else if (!TryParseDate(originalText, dateFormat, out original)) errors.Add($"Original issue date \"{originalText}\" isn't a date in the chosen format.");
        else if (original > DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))) errors.Add("Original issue date is in the future.");
        else if (original.Year < 1900) errors.Add("Original issue date is before 1900.");

        DateTimeOffset? expires = null;
        var expiresText = Clean(row.ExpiresOn);
        if (expiresText.Length > 0)
        {
            if (!TryParseDate(expiresText, dateFormat, out var e)) errors.Add($"Expiry date \"{expiresText}\" isn't a date in the chosen format.");
            else if (originalText.Length > 0 && e < original) errors.Add("Expiry date is before the original issue date.");
            else expires = new DateTimeOffset(e.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var legacy = Clean(row.LegacyNumber);
        string? legacyKey = null;
        if (legacy.Length > MaxLegacyNumberLength) errors.Add($"Paper certificate number is longer than {MaxLegacyNumberLength} characters.");
        else if (legacy.Length > 0) legacyKey = LegacyKey(legacy);

        var fieldValuesJson = FieldValues(row.FieldValues, fieldSchemaJson, dateFormat, errors);

        return errors.Count > 0
            ? (null, errors)
            : (new Valid(name, email, title, original, legacy.Length > 0 ? legacy : null, legacyKey, expires, fieldValuesJson), errors);
    }

    // Upper case, no spaces: "acme / 2015 / 42" and "ACME/2015/42" are the same number.
    public static string LegacyKey(string number) => new string(number.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    // The template's own fields (text/number/date), as the strings certificates
    // store; table fields can't come from a spreadsheet row and are left empty.
    private static string FieldValues(Dictionary<string, string?>? values, string schemaJson, string dateFormat, List<string> errors)
    {
        var result = new Dictionary<string, string?>();
        using var schema = JsonDocument.Parse(schemaJson);
        foreach (var field in schema.RootElement.EnumerateArray())
        {
            var key = field.GetProperty("key").GetString()!;
            var type = field.TryGetProperty("type", out var t) ? t.GetString() : "text";
            var label = field.TryGetProperty("label", out var l) && l.GetString() is { Length: > 0 } text ? text : key;
            if (type == "table") continue;
            var value = Clean(values?.GetValueOrDefault(key));
            if (value.Length == 0) continue;
            if (type == "date")
            {
                if (!TryParseDate(value, dateFormat, out var d))
                {
                    errors.Add($"{label}: \"{value}\" isn't a date in the chosen format.");
                    continue;
                }
                value = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            result[key] = value;
        }
        var json = JsonSerializer.Serialize(result);
        if (!FieldSchemaValidator.TryValidateValues(json, schemaJson, out var error)) errors.Add(error ?? "Custom fields are not valid.");
        return json;
    }

    // yyyy-mm-dd always; otherwise day/month/year in the order chosen (DMY,
    // MDY or YMD) with / . - or space between; or a month name ("12 Mar 2015",
    // "March 12, 2015"). Four-digit years only, so 1/2/15 is never guessed.
    public static bool TryParseDate(string text, string dateFormat, out DateOnly date)
    {
        date = default;
        text = text.Trim();
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;

        var parts = NumericDate().Match(text);
        if (parts.Success)
        {
            var (a, b, c) = (int.Parse(parts.Groups[1].Value), int.Parse(parts.Groups[2].Value), int.Parse(parts.Groups[3].Value));
            var (y, m, d) = dateFormat switch
            {
                "MDY" => (c, a, b),
                "YMD" => (a, b, c),
                _ => (c, b, a),
            };
            if (y < 1000 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), m)) return false;
            date = new DateOnly(y, m, d);
            return true;
        }
        string[] named = ["d MMM yyyy", "d MMMM yyyy", "d-MMM-yyyy", "MMM d, yyyy", "MMMM d, yyyy", "d MMM, yyyy"];
        return DateOnly.TryParseExact(text, named, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static string Clean(string? value) => (value ?? "").Trim();

    [GeneratedRegex(@"^(\d{1,4})[/.\- ](\d{1,2})[/.\- ](\d{1,4})$")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
