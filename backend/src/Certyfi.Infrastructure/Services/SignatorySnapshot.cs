using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Credential.SignatoriesJson: the names and designations printed on a
// certificate, copied from its template when the certificate is created.
public static class SignatorySnapshot
{
    public record Entry(string Name, string Designation);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<Entry> entries) => JsonSerializer.Serialize(entries, Json);

    public static IReadOnlyList<Entry>? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A template's signatories, in its order (unknown ids skipped).
    public static async Task<List<Signatory>> ForTemplateAsync(TenantDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var found = await db.Signatories.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        return ids.Select(id => found.FirstOrDefault(s => s.Id == id)).OfType<Signatory>().ToList();
    }
}
