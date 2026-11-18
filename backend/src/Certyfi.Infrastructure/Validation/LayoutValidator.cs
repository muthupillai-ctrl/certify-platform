using System.Text.Json;
using System.Text.RegularExpressions;

namespace Certyfi.Infrastructure.Validation;

// Validates CertificateTemplate.LayoutJson (and the copy of it snapshotted
// onto Achievement.TemplateLayoutJson). Same trust boundary as
// FieldSchemaValidator — this is caller-supplied JSON (the canvas editor
// writes it), so shape validation is required, not optional: an
// unvalidated element with a non-numeric x/y or an unrecognized type would
// make CertificatePreviewComponent's positioned-rendering branch throw at
// render time on the public verify page. Only position/size/font/color/
// alignment ever live here — actual text content is never part of this
// JSON (see CertificateTemplate.TitleText/SubtitleText and Credential's own
// field values), so there is no free-form string/HTML injection surface to
// worry about beyond the hex-color and enum checks below.
public static class LayoutValidator
{
    private static readonly Regex HexColorPattern = new(@"^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$", RegexOptions.Compiled);
    private static readonly HashSet<string> ElementTypes = new()
    {
        "title", "subtitle", "institutionName", "logo", "recipientName", "achievementTitle",
        "achievementDescription", "field", "table", "issuedDate", "expiryDate", "qrCode", "credentialId", "signatories",
        // Design elements: the template's own words, a line, an image (seal, stamp, second logo).
        "text", "line", "image",
    };

    // Fonts a template or element may use (loaded from Google Fonts by the
    // frontend; see certificate-fonts.ts). Template-level FontFamily uses
    // the same list.
    public static readonly HashSet<string> FontKeys = new()
    {
        "serif", "sans", "playfair", "cormorant", "cinzel", "garamond", "merriweather",
        "great-vibes", "pinyon", "montserrat", "raleway", "lato",
    };
    private static readonly HashSet<string> FrameStyles = new() { "none", "line", "double", "classic", "ornate", "corners", "ribbon", "band" };
    private static readonly HashSet<string> LineStyles = new() { "solid", "dashed", "dotted", "double" };
    public const int MaxTextLength = 1000;
    // An image element's src: an http(s) address (an upload's own address included).
    private static readonly Regex ImageSrcPattern = new(@"^https?://[^\s""'<>]+$", RegexOptions.Compiled);

    // What every certificate design must show (when mandatory content is
    // enforced: CertificateTemplatesController). Each entry is satisfied by
    // any one of its element types: the institution by its name or its
    // logo, the certificate's identity by its QR code or its code.
    public static readonly IReadOnlyList<(string Label, string[] Types)> CoreElements =
    [
        ("Institution name or logo", ["institutionName", "logo"]),
        ("Recipient name", ["recipientName"]),
        ("Certificate title", ["achievementTitle"]),
        ("Issue date", ["issuedDate"]),
        ("Signatories", ["signatories"]),
        ("QR code or certificate code", ["qrCode", "credentialId"]),
    ];

    // Labels of the core items the layout doesn't show (empty when complete
    // or when the JSON can't be read; TryValidateLayout reports that).
    public static List<string> MissingCoreElements(string layoutJson)
    {
        var types = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(layoutJson);
            if (doc.RootElement.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in elements.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return CoreElements.Where(c => !c.Types.Any(types.Contains)).Select(c => c.Label).ToList();
    }
    private static readonly HashSet<string> FieldElementTypes = new() { "field", "table" };
    private static readonly HashSet<string> AlignValues = new() { "left", "center", "right" };

    public static bool TryValidateLayout(string layoutJson, string fieldSchemaJson, out string? error)
    {
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(layoutJson);
        }
        catch (JsonException)
        {
            error = "Layout must be valid JSON.";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Layout must be a JSON object.";
                return false;
            }

            if (!TryGetNumberInRange(root, "canvasWidth", 100, 4000, out error) ||
                !TryGetNumberInRange(root, "canvasHeight", 100, 4000, out error))
            {
                return false;
            }

            if (root.TryGetProperty("backgroundColor", out var bg) && bg.ValueKind != JsonValueKind.Null)
            {
                if (bg.ValueKind != JsonValueKind.String || !HexColorPattern.IsMatch(bg.GetString()!))
                {
                    error = "backgroundColor must be a hex color.";
                    return false;
                }
            }

            if (root.TryGetProperty("frame", out var frame) && frame.ValueKind != JsonValueKind.Null && !TryValidateFrame(frame, out error))
            {
                return false;
            }

            if (!root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            {
                error = "Layout must have an 'elements' array.";
                return false;
            }

            var fieldKeys = ExtractFieldKeys(fieldSchemaJson);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in elements.EnumerateArray())
            {
                if (!TryValidateElement(element, fieldKeys, seenIds, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryValidateElement(JsonElement element, HashSet<string> fieldKeys, HashSet<string> seenIds, out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each layout element must be an object.";
            return false;
        }

        if (!element.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String || idEl.GetString()!.Length == 0)
        {
            error = "Each layout element needs a non-empty string 'id'.";
            return false;
        }
        var id = idEl.GetString()!;
        if (!seenIds.Add(id))
        {
            error = $"Duplicate layout element id '{id}'.";
            return false;
        }

        if (!element.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            error = "Each layout element needs a string 'type'.";
            return false;
        }
        var type = typeEl.GetString()!;
        if (!ElementTypes.Contains(type))
        {
            error = $"Layout element type '{type}' is not recognized.";
            return false;
        }

        if (!TryGetNumberInRange(element, "x", -1000, 4000, out error) ||
            !TryGetNumberInRange(element, "y", -1000, 4000, out error) ||
            !TryGetNumberInRange(element, "width", 1, 4000, out error))
        {
            return false;
        }

        if (element.TryGetProperty("height", out var heightEl) && heightEl.ValueKind != JsonValueKind.Null)
        {
            if (heightEl.ValueKind != JsonValueKind.Number || heightEl.GetDouble() is < 1 or > 4000)
            {
                error = "height must be a number between 1 and 4000.";
                return false;
            }
        }

        if (element.TryGetProperty("fontSize", out var fontSizeEl) && fontSizeEl.ValueKind != JsonValueKind.Null)
        {
            if (fontSizeEl.ValueKind != JsonValueKind.Number || fontSizeEl.GetDouble() is < 1 or > 200)
            {
                error = "fontSize must be a number between 1 and 200.";
                return false;
            }
        }

        if (element.TryGetProperty("color", out var colorEl) && colorEl.ValueKind != JsonValueKind.Null)
        {
            if (colorEl.ValueKind != JsonValueKind.String || !HexColorPattern.IsMatch(colorEl.GetString()!))
            {
                error = "color must be a hex color.";
                return false;
            }
        }

        if (element.TryGetProperty("align", out var alignEl) && alignEl.ValueKind != JsonValueKind.Null)
        {
            if (alignEl.ValueKind != JsonValueKind.String || !AlignValues.Contains(alignEl.GetString()!))
            {
                error = "align must be one of left, center, right.";
                return false;
            }
        }

        if (!TryValidateStyle(element, out error)) return false;

        if (type == "text")
        {
            if (!element.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(textEl.GetString()) || textEl.GetString()!.Length > MaxTextLength)
            {
                error = $"A text element needs its text (1 to {MaxTextLength} characters).";
                return false;
            }
        }
        if (type == "image")
        {
            if (!element.TryGetProperty("src", out var srcEl) || srcEl.ValueKind != JsonValueKind.String
                || srcEl.GetString()!.Length > 500 || !ImageSrcPattern.IsMatch(srcEl.GetString()!))
            {
                error = "An image element needs an http:// or https:// image address.";
                return false;
            }
        }
        if (type == "line" && element.TryGetProperty("lineStyle", out var lineStyleEl) && lineStyleEl.ValueKind != JsonValueKind.Null
            && (lineStyleEl.ValueKind != JsonValueKind.String || !LineStyles.Contains(lineStyleEl.GetString()!)))
        {
            error = $"lineStyle must be one of: {string.Join(", ", LineStyles)}.";
            return false;
        }

        if (FieldElementTypes.Contains(type))
        {
            if (!element.TryGetProperty("fieldKey", out var fieldKeyEl) || fieldKeyEl.ValueKind != JsonValueKind.String || fieldKeyEl.GetString()!.Length == 0)
            {
                error = $"A '{type}' layout element needs a non-empty string 'fieldKey'.";
                return false;
            }
            var fieldKey = fieldKeyEl.GetString()!;
            if (!fieldKeys.Contains(fieldKey))
            {
                error = $"Layout element references unknown field key '{fieldKey}'.";
                return false;
            }
        }

        return true;
    }

    // Optional type settings shared by every element.
    private static bool TryValidateStyle(JsonElement element, out string? error)
    {
        error = null;
        if (element.TryGetProperty("fontFamily", out var font) && font.ValueKind != JsonValueKind.Null
            && (font.ValueKind != JsonValueKind.String || !FontKeys.Contains(font.GetString()!)))
        {
            error = $"fontFamily must be one of: {string.Join(", ", FontKeys)}.";
            return false;
        }
        foreach (var flag in new[] { "bold", "italic", "uppercase", "locked" })
        {
            if (element.TryGetProperty(flag, out var f) && f.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                error = $"'{flag}' must be true or false.";
                return false;
            }
        }
        return OptionalNumber(element, "letterSpacing", -5, 50, out error)
            && OptionalNumber(element, "lineHeight", 0.5, 4, out error)
            && OptionalNumber(element, "opacity", 0.05, 1, out error);
    }

    private static bool TryValidateFrame(JsonElement frame, out string? error)
    {
        error = null;
        if (frame.ValueKind != JsonValueKind.Object)
        {
            error = "frame must be an object.";
            return false;
        }
        if (!frame.TryGetProperty("style", out var style) || style.ValueKind != JsonValueKind.String || !FrameStyles.Contains(style.GetString()!))
        {
            error = $"frame.style must be one of: {string.Join(", ", FrameStyles)}.";
            return false;
        }
        if (frame.TryGetProperty("color", out var color) && color.ValueKind != JsonValueKind.Null
            && (color.ValueKind != JsonValueKind.String || !HexColorPattern.IsMatch(color.GetString()!)))
        {
            error = "frame.color must be a hex color.";
            return false;
        }
        return OptionalNumber(frame, "width", 1, 40, out error) && OptionalNumber(frame, "inset", 0, 200, out error);
    }

    private static bool OptionalNumber(JsonElement obj, string name, double min, double max, out string? error)
    {
        error = null;
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || value.GetDouble() < min || value.GetDouble() > max)
        {
            error = $"'{name}' must be a number between {min} and {max}.";
            return false;
        }
        return true;
    }

    private static bool TryGetNumberInRange(JsonElement obj, string propertyName, double min, double max, out string? error)
    {
        error = null;
        if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            error = $"'{propertyName}' must be a number.";
            return false;
        }
        var d = value.GetDouble();
        if (d < min || d > max)
        {
            error = $"'{propertyName}' must be between {min} and {max}.";
            return false;
        }
        return true;
    }

    private static HashSet<string> ExtractFieldKeys(string fieldSchemaJson)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(fieldSchemaJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in doc.RootElement.EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                    {
                        keys.Add(keyEl.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Caller validates fieldSchemaJson itself separately (FieldSchemaValidator)
            // — an unparseable schema here just means no field keys are known,
            // so any field/table element will correctly fail validation above.
        }
        return keys;
    }
}
