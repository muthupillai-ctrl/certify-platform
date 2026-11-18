using System.Net;
using Certyfi.Infrastructure.Anchoring;
using Certyfi.Infrastructure.Anchoring.OpenTimestamps;

namespace Certyfi.IntegrationTests;

// Regression test for checkpoints that stayed "Pending" forever. Each
// calendar commits to the digest AFTER its own append/hash ops, so an
// upgrade must ask the calendar about the message at the pending
// attestation's node. The client used to ask every calendar about the root
// digest, which real calendars answer with 404 indefinitely — confirmed on
// 2026-09-29 when the official `ots upgrade` confirmed the same stored proofs
// in Bitcoin blocks 968300+ while Certyfi still showed them pending.
//
// Offline: a fake calendar answers ONLY the correct commitment, using the
// real alice.btc.calendar.opentimestamps.org response captured in
// OpenTimestampsSerializationTests as the pending proof.
public class OpenTimestampsUpgradeTests
{
    private const string FixtureDigestHex =
        "424f5f7f6fa49353c13ce703727fbcaef8c263545f22c28603ba814dc3844443";

    private const string FixtureResponseHex =
        "f00877f6242098891c5008f010f15aa4b1143810f5ed474c56bac4d1ce08f1202667f0" +
        "763eca9d841ddb7f02e78ee5b98fff8a07264c39b8d6901984a7452f0308f0206369fb" +
        "749c418ab80f40297a457edffef512187b4b4e434c03f732111c20840b08f020cf6289" +
        "ecc36e7ae50ea6ad2b1814d0fd56e711a030e9cbb961572d6caaa4f85208f1046ab405" +
        "5af0081d9abc68f551e1450083dfe30d2ef90c8e2e2d68747470733a2f2f616c696365" +
        "2e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267";

    [Fact]
    public async Task Upgrade_AsksCalendarForItsOwnCommitment_AndMergesBitcoinAttestation()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var root = Timestamp.Deserialize(new OtsReader(Convert.FromHexString(FixtureResponseHex)), digest);

        var (pendingNode, _) = root.AllAttestationNodes().Single(p => p.Attestation is PendingAttestation);
        var commitmentHex = Convert.ToHexString(pendingNode.Msg).ToLowerInvariant();
        Assert.NotEqual(FixtureDigestHex, commitmentHex);

        var handler = new FakeCalendar(commitmentHex, BitcoinUpgradeFor(pendingNode.Msg, 968300));
        var client = new OpenTimestampsCalendarClient(new HttpClient(handler));

        var report = await client.UpgradePendingAttestationsAsync(root, CancellationToken.None);

        Assert.True(report.Changed);
        var outcome = Assert.Single(report.Calendars);
        Assert.Equal(CalendarUpgradeStatus.Upgraded, outcome.Status);
        Assert.True(outcome.HasBitcoinAttestation);
        Assert.Equal($"/timestamp/{commitmentHex}", Assert.Single(handler.RequestedPaths));
        var bitcoin = Assert.Single(root.AllAttestations(), p => p.Attestation is BitcoinBlockHeaderAttestation);
        Assert.Equal(968300UL, ((BitcoinBlockHeaderAttestation)bitcoin.Attestation).Height);
    }

    [Fact]
    public async Task Upgrade_ReportsNoChange_WhileCalendarStillPending()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var root = Timestamp.Deserialize(new OtsReader(Convert.FromHexString(FixtureResponseHex)), digest);

        var handler = new FakeCalendar(expectedCommitmentHex: "none", upgradeBody: Array.Empty<byte>());
        var client = new OpenTimestampsCalendarClient(new HttpClient(handler));

        var report = await client.UpgradePendingAttestationsAsync(root, CancellationToken.None);
        Assert.False(report.Changed);
        Assert.Equal(CalendarUpgradeStatus.Pending, Assert.Single(report.Calendars).Status);
        Assert.Contains("still pending at 1 of 1", report.Summarize());
        Assert.DoesNotContain(root.AllAttestations(), p => p.Attestation is BitcoinBlockHeaderAttestation);
    }

    [Fact]
    public async Task Upgrade_ReportsCalendarErrors_WithoutThrowing()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var root = Timestamp.Deserialize(new OtsReader(Convert.FromHexString(FixtureResponseHex)), digest);
        var client = new OpenTimestampsCalendarClient(new HttpClient(new FailingCalendar()));

        var report = await client.UpgradePendingAttestationsAsync(root, CancellationToken.None);

        var outcome = Assert.Single(report.Calendars);
        Assert.Equal(CalendarUpgradeStatus.Error, outcome.Status);
        Assert.Contains("handshake failure", outcome.Error);
        Assert.Contains("alice.btc.calendar.opentimestamps.org: ", report.Summarize());
    }

    private sealed class FailingCalendar : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("The SSL connection could not be established.", new Exception("handshake failure"));
    }

    private static byte[] BitcoinUpgradeFor(byte[] commitment, ulong height)
    {
        var upgrade = new Timestamp(commitment);
        upgrade.Attestations.Add(new BitcoinBlockHeaderAttestation(height));
        var writer = new OtsWriter();
        upgrade.Serialize(writer);
        return writer.ToArray();
    }

    // 200 with `upgradeBody` for the one expected commitment, 404 (still
    // pending) for anything else — exactly how a real calendar behaves.
    private sealed class FakeCalendar(string expectedCommitmentHex, byte[] upgradeBody) : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            var response = path == $"/timestamp/{expectedCommitmentHex}"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(upgradeBody) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
