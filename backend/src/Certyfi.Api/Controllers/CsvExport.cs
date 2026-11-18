using System.Text;
using System.Text.Json;

namespace Certyfi.Api.Controllers;

// Minimal RFC 4180 CSV writer for the certificates export.
//
// Formula injection: a spreadsheet treats a cell starting with = + - @ (or
// tab / carriage return) as a formula. Recipient names and custom field
// values are typed by staff or partner systems, so such cells get a leading
// apostrophe, which Excel and Google Sheets show as plain text.
public class CsvExport
{
    private readonly StringBuilder _sb = new();

    public void Row(IEnumerable<string?> cells)
    {
        _sb.Append(string.Join(",", cells.Select(Escape))).Append("\r\n");
    }

    // UTF-8 with a byte-order mark so Excel reads accented names correctly.
    public byte[] ToUtf8WithBom() => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_sb.ToString())).ToArray();

    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    // (key, label) pairs from a credential's field-schema snapshot.
    public static IEnumerable<(string Key, string Label)> FieldLabels(string schemaJson)
    {
        using var doc = TryParse(schemaJson);
        if (doc?.RootElement.ValueKind != JsonValueKind.Array) yield break;
        foreach (var field in doc.RootElement.EnumerateArray())
        {
            if (!field.TryGetProperty("key", out var key) || key.GetString() is not { } k) continue;
            var label = field.TryGetProperty("label", out var l) && l.GetString() is { Length: > 0 } text ? text : k;
            yield return (k, label);
        }
    }

    // key → display text. Table fields (arrays of rows) become
    // "col: value; col: value | next row".
    public static Dictionary<string, string> FieldValues(string valuesJson)
    {
        var result = new Dictionary<string, string>();
        using var doc = TryParse(valuesJson);
        if (doc?.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(" | ", property.Value.EnumerateArray().Select(row =>
                    row.ValueKind == JsonValueKind.Object
                        ? string.Join("; ", row.EnumerateObject().Select(cell => $"{cell.Name}: {cell.Value}"))
                        : row.ToString())),
                JsonValueKind.Null => "",
                _ => property.Value.ToString(),
            };
        }
        return result;
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
