using System.Text.Json;
using System.Text.RegularExpressions;

namespace Certyfi.Infrastructure.Validation;

// Validates Achievement.FieldSchemaJson and Credential.FieldValuesJson.
// FieldValuesJson is caller-supplied (via CreateCredentialRequest), unlike
// server-generated JSON elsewhere in this codebase (Credential.CanonicalHash,
// LedgerEntry.EventDataJson) — a different trust boundary. Shape validation
// here is required, not optional: an unvalidated table-type field value that
// isn't actually an array would make the public verify page's @for over it
// throw at render time — a public-facing crash on the platform's own trust
// surface, not just messy data. Deep type-correctness (e.g. confirming a
// "number" field's string actually parses as a number) is deliberately left
// unchecked — a data-quality issue, not a crash or security one.
public static class FieldSchemaValidator
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase) { "__proto__", "constructor", "prototype" };
    private static readonly HashSet<string> FieldTypes = new() { "text", "number", "date", "table" };
    private static readonly HashSet<string> ColumnTypes = new() { "text", "number", "date" };

    public static bool TryValidateSchema(string json, out string? error)
    {
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "Field schema must be valid JSON.";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Field schema must be a JSON array.";
                return false;
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!TryValidateFieldDefinition(element, FieldTypes, allowTable: true, out error))
                {
                    return false;
                }

                var key = element.GetProperty("key").GetString()!;
                if (!seenKeys.Add(key))
                {
                    error = $"Duplicate field key '{key}'.";
                    return false;
                }
            }
        }

        return true;
    }

    // A field marked "required": true in the schema must have a value: a
    // non-blank string, or a table with at least one row. alsoRequired adds
    // keys required by the template now (a reused achievement keeps the
    // schema it was created with; see CredentialIssuanceService.RequiredFieldKeysAsync).
    public static bool TryValidateValues(string valuesJson, string schemaJson, out string? error, IReadOnlySet<string>? alsoRequired = null)
    {
        error = null;
        JsonDocument valuesDoc;
        try
        {
            valuesDoc = JsonDocument.Parse(valuesJson);
        }
        catch (JsonException)
        {
            error = "Field values must be valid JSON.";
            return false;
        }

        using (valuesDoc)
        {
            if (valuesDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Field values must be a JSON object.";
                return false;
            }

            using var schemaDoc = JsonDocument.Parse(schemaJson);
            foreach (var fieldDef in schemaDoc.RootElement.EnumerateArray())
            {
                var key = fieldDef.GetProperty("key").GetString()!;
                var type = fieldDef.GetProperty("type").GetString()!;
                var required = IsRequired(fieldDef) || alsoRequired?.Contains(key) == true;

                if (!valuesDoc.RootElement.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null
                    || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                    || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
                {
                    if (required)
                    {
                        error = $"'{fieldDef.GetProperty("label").GetString()}' is required.";
                        return false;
                    }
                    continue; // Optional field left empty.
                }

                if (type == "table")
                {
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        error = $"Field '{key}' must be an array of rows.";
                        return false;
                    }
                    foreach (var row in value.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object)
                        {
                            error = $"Field '{key}' rows must be objects.";
                            return false;
                        }
                        foreach (var cell in row.EnumerateObject())
                        {
                            if (cell.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            {
                                error = $"Field '{key}' row values must be strings.";
                                return false;
                            }
                        }
                    }
                }
                else if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error = $"Field '{key}' must be a string.";
                    return false;
                }
            }
        }

        return true;
    }

    public static bool IsRequired(JsonElement fieldDefinition) =>
        fieldDefinition.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;

    // Keys of the top-level fields marked required.
    public static HashSet<string> RequiredKeys(string schemaJson)
    {
        var keys = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return keys;
            foreach (var field in doc.RootElement.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && IsRequired(field) && field.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                {
                    keys.Add(k.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
        }
        return keys;
    }

    private static bool TryValidateFieldDefinition(JsonElement element, HashSet<string> allowedTypes, bool allowTable, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each field definition must be an object.";
            return false;
        }

        if (!element.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String)
        {
            error = "Each field definition needs a string 'key'.";
            return false;
        }
        var key = keyEl.GetString()!;
        if (!KeyPattern.IsMatch(key))
        {
            error = $"Field key '{key}' must start with a letter and contain only letters, numbers, or underscores.";
            return false;
        }
        if (ReservedKeys.Contains(key))
        {
            error = $"Field key '{key}' is reserved and cannot be used.";
            return false;
        }

        if (!element.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String || labelEl.GetString()!.Length == 0)
        {
            error = "Each field definition needs a non-empty string 'label'.";
            return false;
        }

        if (!element.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            error = "Each field definition needs a string 'type'.";
            return false;
        }
        var type = typeEl.GetString()!;
        if (!allowedTypes.Contains(type))
        {
            error = $"Field type '{type}' is not recognized.";
            return false;
        }
        if (element.TryGetProperty("required", out var requiredEl) && requiredEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"Field '{key}': 'required' must be true or false.";
            return false;
        }

        if (type == "table")
        {
            if (!allowTable)
            {
                error = "Table columns cannot themselves be of type 'table' (no nested tables).";
                return false;
            }
            if (!element.TryGetProperty("columns", out var columnsEl) || columnsEl.ValueKind != JsonValueKind.Array || columnsEl.GetArrayLength() == 0)
            {
                error = "A 'table' field needs a non-empty 'columns' array.";
                return false;
            }

            var seenColumnKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columnsEl.EnumerateArray())
            {
                if (!TryValidateFieldDefinition(column, ColumnTypes, allowTable: false, out error))
                {
                    return false;
                }
                var columnKey = column.GetProperty("key").GetString()!;
                if (!seenColumnKeys.Add(columnKey))
                {
                    error = $"Duplicate column key '{columnKey}'.";
                    return false;
                }
            }
        }

        return true;
    }
}
