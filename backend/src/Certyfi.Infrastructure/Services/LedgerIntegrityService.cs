using System.Text.Json;
using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Certyfi.Infrastructure.Services;

// Re-walks a tenant's hash-chained ledger and, on failure, explains it in
// terms an Institution Admin can act on: which certificate and event, what
// kind of break, what it means, what to do — plus the hashes for support.
//
// Entries written before timestamps were rounded to microseconds (see
// LedgerService.AppendAsync) were hashed with a 100 ns digit Postgres
// doesn't store. For those, restoring that single digit (0–9) must
// reproduce the stored hash exactly; every other field is checked as-is,
// so any real change to an entry still fails. They're counted in
// PrecisionAdjustedEntries rather than silently passed.
public class LedgerIntegrityService
{
    public async Task<LedgerIntegrityReport> CheckAsync(TenantDbContext db, CancellationToken ct)
    {
        var entries = await db.LedgerEntries.AsNoTracking()
            .Include(e => e.ActorUser)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(ct);

        var expectedPrevious = LedgerService.GenesisHash;
        var precisionAdjusted = 0;
        foreach (var entry in entries)
        {
            if (entry.PreviousEntryHash != expectedPrevious)
            {
                return await FailAsync(db, entries.Count, precisionAdjusted, entry, LedgerProblemKind.ChainBroken,
                    LedgerService.ComputeEntryHash(entry.PreviousEntryHash, entry.EventType, entry.CredentialId, entry.ActorUserId, entry.OccurredAt, entry.EventDataJson),
                    expectedPrevious, ct);
            }

            var recomputed = HashAt(entry, entry.OccurredAt);
            if (entry.EntryHash != recomputed)
            {
                if (Enumerable.Range(1, 9).Any(ticks => HashAt(entry, entry.OccurredAt.AddTicks(ticks)) == entry.EntryHash))
                {
                    precisionAdjusted++;
                }
                else
                {
                    return await FailAsync(db, entries.Count, precisionAdjusted, entry, LedgerProblemKind.ContentChanged, recomputed, expectedPrevious, ct);
                }
            }

            expectedPrevious = entry.EntryHash;
        }

        // The chain holds a salted fingerprint of each entry's personal
        // details (LedgerEntryDetail); the details themselves must still
        // match it, unless the certificate's personal data was erased.
        var details = await db.LedgerEntryDetails.AsNoTracking().ToDictionaryAsync(d => d.LedgerEntryId, ct);
        var credentials = await db.Credentials.AsNoTracking()
            .Select(c => new { c.Id, c.PersonalDataSalt, c.PersonalDataErasedAt })
            .ToDictionaryAsync(c => c.Id, ct);
        foreach (var entry in entries.Where(e => e.EventDataJson.Contains("\"personalData\"")))
        {
            var fingerprint = PersonalDataFingerprintIn(entry.EventDataJson);
            if (fingerprint is null) continue;
            var credential = credentials.GetValueOrDefault(entry.CredentialId);
            if (credential?.PersonalDataErasedAt is not null) continue;
            var detail = details.GetValueOrDefault(entry.Id);
            var recomputed = detail is not null && credential?.PersonalDataSalt is { } salt
                ? PersonalDataFingerprint.Of(salt, detail.DataJson)
                : "(details missing)";
            if (recomputed != fingerprint)
                return await FailAsync(db, entries.Count, precisionAdjusted, entry, LedgerProblemKind.DetailsChanged, recomputed, fingerprint, ct);
        }

        return new LedgerIntegrityReport(true, null, null, entries.Count, precisionAdjusted, DateTimeOffset.UtcNow, null);
    }

    private static string? PersonalDataFingerprintIn(string eventDataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventDataJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("personalData", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HashAt(LedgerEntry entry, DateTimeOffset occurredAt) =>
        LedgerService.ComputeEntryHash(entry.PreviousEntryHash, entry.EventType, entry.CredentialId, entry.ActorUserId, occurredAt, entry.EventDataJson);

    private static async Task<LedgerIntegrityReport> FailAsync(
        TenantDbContext db, int total, int precisionAdjusted, LedgerEntry entry, string kind, string recomputed, string expectedPrevious, CancellationToken ct)
    {
        var credential = await db.Credentials.AsNoTracking()
            .Where(c => c.Id == entry.CredentialId)
            .Select(c => new { c.RecipientName, c.Achievement.Title, c.VerificationCode })
            .FirstOrDefaultAsync(ct);

        var (reason, explanation) = kind == LedgerProblemKind.ChainBroken
            ? ("An entry before this one is missing or was altered: the chain of hashes no longer links up.",
               "Each audit entry records the fingerprint (hash) of the entry before it. This entry points to a fingerprint that " +
               "doesn't match the previous entry, which means an earlier entry was removed, replaced or edited directly in the database.")
            : kind == LedgerProblemKind.DetailsChanged
            ? ("The personal details stored with this entry were changed or removed.",
               "Names, emails and reasons are kept beside the audit trail (so they can be erased on request), and the entry holds " +
               "a fingerprint of them. The details stored now don't match that fingerprint, so they were edited or deleted directly " +
               "in the database.")
            : ("This entry's recorded details were changed after it was written.",
               "When this entry was written, Certyfi stored a fingerprint (hash) of its details. Recalculating that fingerprint from " +
               "what's stored now gives a different result, so something in the entry — the event, the certificate it belongs to, " +
               "who did it, when, or its details — was edited directly in the database.");

        var problem = new LedgerIntegrityProblem(
            kind, entry.SequenceNumber, entry.EventType, entry.CredentialId,
            credential?.RecipientName, credential?.Title,
            credential is null ? null : Signing.VerificationCodes.Format(credential.VerificationCode),
            entry.OccurredAt, entry.ActorUser?.FullName,
            explanation,
            "Don't issue or change certificates until this is resolved. Certificates already issued keep their own digital signatures, " +
            "and your Bitcoin-anchored checkpoints record the ledger's earlier state, so the original history can be checked. " +
            "Contact Certyfi support with the report below.",
            entry.EntryHash, recomputed, expectedPrevious, entry.PreviousEntryHash);

        return new LedgerIntegrityReport(false, entry.SequenceNumber, reason, total, precisionAdjusted, DateTimeOffset.UtcNow, problem);
    }
}

public static class LedgerProblemKind
{
    public const string ContentChanged = "ContentChanged";
    public const string ChainBroken = "ChainBroken";
    public const string DetailsChanged = "DetailsChanged";
}

// Reason: one line for the banner. Problem: everything for the details view.
public record LedgerIntegrityReport(
    bool Intact, long? BrokenAtSequenceNumber, string? Reason, long TotalEntries,
    int PrecisionAdjustedEntries, DateTimeOffset CheckedAt, LedgerIntegrityProblem? Problem);

public record LedgerIntegrityProblem(
    string Kind, long SequenceNumber, string EventType, Guid CredentialId,
    string? RecipientName, string? AchievementTitle, string? CertificateCode,
    DateTimeOffset OccurredAt, string? ActorName,
    string Explanation, string WhatToDo,
    string StoredEntryHash, string RecomputedEntryHash, string ExpectedPreviousHash, string StoredPreviousHash);
