using System.Net;
using Certyfi.Infrastructure.Anchoring.OpenTimestamps;

namespace Certyfi.Infrastructure.Anchoring;

// Talks to public OpenTimestamps calendar servers over their documented
// REST protocol (no NuGet dependency, matching the plan's original "plain
// HttpClient" intent) — submitting a digest for a pending timestamp, then
// polling later for an upgraded (eventually Bitcoin-attested) one. Submits
// to multiple free, no-registration calendars for redundancy: one
// calendar's outage shouldn't sink a checkpoint, and results are merged via
// Timestamp.Merge (also a direct python-opentimestamps port).
public class OpenTimestampsCalendarClient
{
    public static readonly string[] DefaultCalendarUrls =
    [
        "https://alice.btc.calendar.opentimestamps.org",
        "https://bob.btc.calendar.opentimestamps.org",
        "https://finney.calendar.eternitywall.com",
        "https://btc.calendar.catallaxy.com",
    ];

    private readonly HttpClient _http;

    public OpenTimestampsCalendarClient(HttpClient http)
    {
        _http = http;
    }

    // POST {calendarUrl}/digest, Accept: application/vnd.opentimestamps.v1,
    // raw 32-byte digest as the body — response body is a serialized
    // Timestamp tree rooted at that same digest. Returns null on any
    // non-success response (caller decides whether that's fatal).
    public async Task<Timestamp?> SubmitAsync(string calendarUrl, byte[] digest32, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{calendarUrl.TrimEnd('/')}/digest")
        {
            Content = new ByteArrayContent(digest32),
        };
        request.Headers.Accept.ParseAdd("application/vnd.opentimestamps.v1");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsByteArrayAsync(ct);
        return Timestamp.Deserialize(new OtsReader(body), digest32);
    }

    // GET {calendarUrl}/timestamp/{hex(commitment)} — 200 with an upgraded
    // Timestamp once more data (eventually Bitcoin confirmation) is
    // available, 404 means still pending (not an error). `commitment` is the
    // message at the pending attestation's node, NOT the original digest —
    // see UpgradePendingAttestationsAsync.
    public async Task<Timestamp?> UpgradeAsync(string calendarUrl, byte[] commitment, CancellationToken ct)
    {
        var hex = Convert.ToHexString(commitment).ToLowerInvariant();
        using var response = await _http.GetAsync($"{calendarUrl.TrimEnd('/')}/timestamp/{hex}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(ct);
        return Timestamp.Deserialize(new OtsReader(body), commitment);
    }

    public async Task<Timestamp> SubmitToAllAsync(byte[] digest32, CancellationToken ct)
    {
        Timestamp? merged = null;
        foreach (var url in DefaultCalendarUrls)
        {
            Timestamp? result;
            try
            {
                result = await SubmitAsync(url, digest32, ct);
            }
            catch (HttpRequestException)
            {
                result = null; // one calendar's outage shouldn't sink the whole submission
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                result = null; // nor should one that times out (HttpClient.Timeout)
            }
            if (result is null) continue;

            if (merged is null) merged = result;
            else merged.Merge(result);
        }

        if (merged is null)
        {
            throw new InvalidOperationException("All OpenTimestamps calendar servers failed to accept the digest.");
        }
        return merged;
    }

    // Upgrades every pending attestation in `root` in place and reports what
    // each calendar said. Mirrors python-opentimestamps' upgrade_timestamp():
    // each calendar committed to a DIFFERENT message — the original digest
    // after that calendar's own append/hash ops — so each PendingAttestation
    // is upgraded by asking ITS calendar about ITS node's message, and the
    // answer is merged into that node. Asking every calendar about the root
    // digest instead (as this client used to) always gets a 404, so
    // checkpoints never left "Pending".
    //
    // Only the known calendars are contacted, whatever URI a proof names —
    // same whitelist principle as the reference client. To look without
    // changing a stored proof (diagnostics), pass a freshly deserialized copy.
    public async Task<CalendarUpgradeReport> UpgradePendingAttestationsAsync(Timestamp root, CancellationToken ct)
    {
        var outcomes = new List<CalendarUpgradeOutcome>();
        // Materialized first: merging adds nodes to the tree being walked.
        var pending = root.AllAttestationNodes()
            .Where(p => p.Attestation is PendingAttestation)
            .Select(p => (p.Node, Uri: ((PendingAttestation)p.Attestation).Uri))
            .ToList();

        foreach (var (node, uri) in pending)
        {
            var commitmentHex = Convert.ToHexString(node.Msg).ToLowerInvariant();
            var calendarUrl = DefaultCalendarUrls.FirstOrDefault(u => string.Equals(u, uri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (calendarUrl is null)
            {
                outcomes.Add(new CalendarUpgradeOutcome(uri, commitmentHex, CalendarUpgradeStatus.Skipped, null, "Not a known calendar; not contacted.", 0, false));
                continue;
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await UpgradeAsync(calendarUrl, node.Msg, ct);
                if (result is null)
                {
                    outcomes.Add(new CalendarUpgradeOutcome(calendarUrl, commitmentHex, CalendarUpgradeStatus.Pending, 404, null, started.ElapsedMilliseconds, false));
                    continue;
                }

                node.Merge(result);
                var hasBitcoin = result.AllAttestations().Any(a => a.Attestation is BitcoinBlockHeaderAttestation);
                outcomes.Add(new CalendarUpgradeOutcome(calendarUrl, commitmentHex, CalendarUpgradeStatus.Upgraded, 200, null, started.ElapsedMilliseconds, hasBitcoin));
            }
            catch (HttpRequestException ex)
            {
                // One calendar's outage shouldn't stop the others upgrading.
                outcomes.Add(new CalendarUpgradeOutcome(calendarUrl, commitmentHex, CalendarUpgradeStatus.Error,
                    ex.StatusCode is { } code ? (int)code : null, Describe(ex), started.ElapsedMilliseconds, false));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                outcomes.Add(new CalendarUpgradeOutcome(calendarUrl, commitmentHex, CalendarUpgradeStatus.Error,
                    null, $"Timed out after {_http.Timeout.TotalSeconds:0}s.", started.ElapsedMilliseconds, false));
            }
        }
        return new CalendarUpgradeReport(outcomes);
    }

    // Connectivity check for the troubleshooting page: asks each calendar
    // about an all-zero commitment. A 404 is the healthy answer ("reachable,
    // API working, never heard of it"); anything else is reported as is.
    public async Task<IReadOnlyList<EndpointProbe>> ProbeCalendarsAsync(CancellationToken ct)
    {
        var probes = new List<EndpointProbe>();
        foreach (var url in DefaultCalendarUrls)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var response = await _http.GetAsync($"{url}/timestamp/{new string('0', 64)}", ct);
                var code = (int)response.StatusCode;
                probes.Add(new EndpointProbe(url, code is 404 or 200, code, null, started.ElapsedMilliseconds));
            }
            catch (HttpRequestException ex)
            {
                probes.Add(new EndpointProbe(url, false, null, Describe(ex), started.ElapsedMilliseconds));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                probes.Add(new EndpointProbe(url, false, null, $"Timed out after {_http.Timeout.TotalSeconds:0}s.", started.ElapsedMilliseconds));
            }
        }
        return probes;
    }

    // Innermost message is the useful one ("handshake failure", "Name or
    // service not known"), not "An error occurred while sending the request."
    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return inner == ex ? ex.Message : $"{ex.Message} ({inner.Message})";
    }
}

// Serialized by name ("Pending", not 1) for the Anchoring page and anyone
// reading the diagnose JSON directly.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CalendarUpgradeStatus>))]
public enum CalendarUpgradeStatus { Upgraded, Pending, Error, Skipped }

// HasBitcoinAttestation: the calendar's answer includes a Bitcoin block
// attestation (not just more pending data).
public record CalendarUpgradeOutcome(
    string CalendarUrl, string CommitmentHex, CalendarUpgradeStatus Status, int? HttpStatus, string? Error, long ElapsedMs,
    bool HasBitcoinAttestation);

public record CalendarUpgradeReport(IReadOnlyList<CalendarUpgradeOutcome> Calendars)
{
    public bool Changed => Calendars.Any(c => c.Status == CalendarUpgradeStatus.Upgraded);

    // One line for LedgerCheckpoint.LastCheckResult.
    public string Summarize()
    {
        if (Calendars.Count == 0) return "No pending attestations to check.";
        var parts = new List<string>();
        var pending = Calendars.Count(c => c.Status == CalendarUpgradeStatus.Pending);
        var upgraded = Calendars.Count(c => c.Status == CalendarUpgradeStatus.Upgraded);
        if (upgraded > 0) parts.Add($"{upgraded} calendar(s) returned an upgrade");
        if (pending > 0) parts.Add($"still pending at {pending} of {Calendars.Count} calendar(s)");
        foreach (var error in Calendars.Where(c => c.Status == CalendarUpgradeStatus.Error))
        {
            parts.Add($"{new Uri(error.CalendarUrl).Host}: {error.Error ?? $"HTTP {error.HttpStatus}"}");
        }
        return string.Join("; ", parts) + ".";
    }
}

public record EndpointProbe(string Url, bool Ok, int? HttpStatus, string? Error, long ElapsedMs);

