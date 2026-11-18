using System.Text;
using System.Text.Json;
using Certyfi.Domain.Enums;

namespace Certyfi.Infrastructure.Validation;

// Generates a starting LayoutJson for a new CertificateTemplate — used by
// CertificateTemplatesController.Create/Update whenever a request omits
// LayoutJson (a raw API call, or the pre-canvas-editor form flow), and by
// the canvas editor's "Reset to default layout" action. Produces a simple,
// always-valid vertical stack (fixed elements, then one element per field in
// fieldSchemaJson) — a reasonable starting point to drag from, not a
// pixel-accurate reproduction of the two legacy hardcoded designs.
public static class DefaultLayouts
{
    public static string Generate(TemplateKind kind, string fieldSchemaJson)
    {
        var elements = new List<string>
        {
            Element("institutionName", "institutionName", x: 60, y: 24, width: 580, fontSize: 11, align: "center", color: "#6b7280"),
            Element("logo", "logo", x: 300, y: 60, width: 100, height: 60),
            Element("title", "title", x: 60, y: 140, width: 580, fontSize: 32, align: "center", color: "#111827", bold: true),
            Element("subtitle", "subtitle", x: 60, y: 182, width: 580, fontSize: 14, align: "center", color: "#6b7280"),
            Element("recipientName", "recipientName", x: 60, y: 240, width: 580, fontSize: 28, align: "center", color: "#111827"),
            Element("achievementTitle", "achievementTitle", x: 60, y: 300, width: 580, fontSize: 18, align: "center", color: "#111827"),
        };

        if (kind == TemplateKind.Certificate)
        {
            elements.Add(Element("achievementDescription", "achievementDescription", x: 110, y: 336, width: 480, fontSize: 12, align: "center", color: "#4b5563"));
        }

        var fieldY = 380;
        foreach (var field in ExtractFields(fieldSchemaJson))
        {
            if (field.Type == "table")
            {
                elements.Add(Element($"field_{field.Key}", "table", x: 60, y: fieldY, width: 580, height: 200, fontSize: 12, color: "#111827", fieldKey: field.Key));
                fieldY += 220;
            }
            else
            {
                elements.Add(Element($"field_{field.Key}", "field", x: 60, y: fieldY, width: 260, fontSize: 13, align: "left", color: "#111827", fieldKey: field.Key));
                fieldY += 36;
            }
        }

        // Signatories above the footer; must match layout-generation.ts.
        var signatoriesY = Math.Max(fieldY + 30, 470);
        elements.Add(Element("signatories", "signatories", x: 60, y: signatoriesY, width: 580, height: 70, fontSize: 12, align: "center", color: "#111827"));
        var footerY = signatoriesY + 130;
        elements.Add(Element("issuedDate", "issuedDate", x: 60, y: footerY, width: 200, fontSize: 11, align: "left", color: "#6b7280"));
        // 140px: smallest slot where the offline-capable QR reliably scans; must match layout-generation.ts on the frontend.
        elements.Add(Element("qrCode", "qrCode", x: 280, y: footerY - 60, width: 140, height: 140));
        elements.Add(Element("credentialId", "credentialId", x: 420, y: footerY, width: 220, fontSize: 10, align: "right", color: "#6b7280"));

        var canvasHeight = footerY + 120;

        var sb = new StringBuilder();
        sb.Append("{\"canvasWidth\":700,\"canvasHeight\":").Append(canvasHeight)
          .Append(",\"backgroundColor\":\"#fdfbf5\",\"elements\":[").Append(string.Join(",", elements)).Append("]}");
        return sb.ToString();
    }

    private static string Element(
        string id, string type, double x, double y, double width,
        double? height = null, double? fontSize = null, string? align = null, string? color = null,
        bool? bold = null, string? fieldKey = null)
    {
        var parts = new List<string>
        {
            $"\"id\":{JsonSerializer.Serialize(id)}",
            $"\"type\":{JsonSerializer.Serialize(type)}",
            $"\"x\":{x}", $"\"y\":{y}", $"\"width\":{width}",
        };
        if (height is not null) parts.Add($"\"height\":{height}");
        if (fontSize is not null) parts.Add($"\"fontSize\":{fontSize}");
        if (align is not null) parts.Add($"\"align\":{JsonSerializer.Serialize(align)}");
        if (color is not null) parts.Add($"\"color\":{JsonSerializer.Serialize(color)}");
        if (bold is not null) parts.Add($"\"bold\":{(bold.Value ? "true" : "false")}");
        if (fieldKey is not null) parts.Add($"\"fieldKey\":{JsonSerializer.Serialize(fieldKey)}");
        return "{" + string.Join(",", parts) + "}";
    }

    private static IEnumerable<(string Key, string Type)> ExtractFields(string fieldSchemaJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(fieldSchemaJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;

            foreach (var field in doc.RootElement.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object) continue;
                if (!field.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
                var type = field.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString()! : "text";
                yield return (keyEl.GetString()!, type);
            }
        }
    }
}
