using Certyfi.Infrastructure.Anchoring.OpenTimestamps;

namespace Certyfi.IntegrationTests;

// Binary-format round-trip tests for the C# OpenTimestamps port — the
// analogue of SigningTests.cs's cross-tool checks, but for wire format
// instead of ECDSA. The FixtureResponseHex/FixtureDigestHex constants below
// are NOT hand-crafted: they're a real, live response captured with a
// single curl POST to https://alice.btc.calendar.opentimestamps.org/digest
// during implementation (2026-09-23) — see the plan's Phase 2 "Tests"
// section on why a same-codebase self-consistency check alone (serialize
// then deserialize your own output) is not sufficient proof of wire-format
// correctness, the same lesson the Phase 1 DER-vs-P1363 signature bug
// taught. This fixture proves the parser reads bytes a real calendar
// server actually produced, not just bytes this code itself produced.
public class OpenTimestampsSerializationTests
{
    private const string FixtureDigestHex =
        "424f5f7f6fa49353c13ce703727fbcaef8c263545f22c28603ba814dc3844443";

    // A real 207-byte Timestamp response for the digest above: a small
    // append/prepend/sha256 merkle path ending in a single PendingAttestation
    // pointing back at the submitting calendar.
    private const string FixtureResponseHex =
        "f00877f6242098891c5008f010f15aa4b1143810f5ed474c56bac4d1ce08f1202667f0" +
        "763eca9d841ddb7f02e78ee5b98fff8a07264c39b8d6901984a7452f0308f0206369fb" +
        "749c418ab80f40297a457edffef512187b4b4e434c03f732111c20840b08f020cf6289" +
        "ecc36e7ae50ea6ad2b1814d0fd56e711a030e9cbb961572d6caaa4f85208f1046ab405" +
        "5af0081d9abc68f551e1450083dfe30d2ef90c8e2e2d68747470733a2f2f616c696365" +
        "2e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267";

    private const string FixtureCalendarUri = "https://alice.btc.calendar.opentimestamps.org";

    [Fact]
    public void RealCalendarFixture_Deserializes_ToExpectedPendingAttestation()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var responseBytes = Convert.FromHexString(FixtureResponseHex);

        var timestamp = Timestamp.Deserialize(new OtsReader(responseBytes), digest);

        var attestations = timestamp.AllAttestations().ToList();
        var pending = Assert.Single(attestations.Select(a => a.Attestation).OfType<PendingAttestation>());
        Assert.Equal(FixtureCalendarUri, pending.Uri);
    }

    [Fact]
    public void RealCalendarFixture_RoundTrips_ToByteIdenticalOutput()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var responseBytes = Convert.FromHexString(FixtureResponseHex);

        var timestamp = Timestamp.Deserialize(new OtsReader(responseBytes), digest);

        var w = new OtsWriter();
        timestamp.Serialize(w);

        Assert.Equal(responseBytes, w.ToArray());
    }

    [Fact]
    public void RealCalendarFixture_RoundTripsThrough_DetachedTimestampFile()
    {
        var digest = Convert.FromHexString(FixtureDigestHex);
        var responseBytes = Convert.FromHexString(FixtureResponseHex);
        var timestamp = Timestamp.Deserialize(new OtsReader(responseBytes), digest);

        var proofFile = new DetachedTimestampFile(new OpSha256(), timestamp);
        var otsBytes = proofFile.Serialize();

        var roundTripped = DetachedTimestampFile.Deserialize(otsBytes);

        Assert.Equal(digest, roundTripped.FileDigest);
        var pending = Assert.Single(roundTripped.Timestamp.AllAttestations().Select(a => a.Attestation).OfType<PendingAttestation>());
        Assert.Equal(FixtureCalendarUri, pending.Uri);
    }

    [Fact]
    public void HandBuiltTree_AppendThenSha256ThenBitcoinAttestation_RoundTrips()
    {
        var msg = Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        var suffix = new byte[] { 0xde, 0xad, 0xbe, 0xef };

        var root = new Timestamp(msg);
        var appendOp = new OpAppend(suffix);
        var appended = appendOp.Apply(msg);
        var appendStamp = new Timestamp(appended);

        var sha256Op = new OpSha256();
        var digest = sha256Op.Apply(appended);
        var sha256Stamp = new Timestamp(digest);
        sha256Stamp.Attestations.Add(new BitcoinBlockHeaderAttestation(800000));

        appendStamp.Ops[sha256Op] = sha256Stamp;
        root.Ops[appendOp] = appendStamp;

        var w = new OtsWriter();
        root.Serialize(w);
        var bytes = w.ToArray();

        var deserialized = Timestamp.Deserialize(new OtsReader(bytes), msg);

        var (leafMsg, attestation) = Assert.Single(deserialized.AllAttestations());
        var btc = Assert.IsType<BitcoinBlockHeaderAttestation>(attestation);
        Assert.Equal(800000u, btc.Height);
        Assert.Equal(digest, leafMsg);

        // Re-serializing the deserialized tree must reproduce the same bytes.
        var w2 = new OtsWriter();
        deserialized.Serialize(w2);
        Assert.Equal(bytes, w2.ToArray());
    }

    [Fact]
    public void Merge_CombinesAttestationsFromTwoCalendars_WithoutDuplication()
    {
        var msg = Convert.FromHexString("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899");

        var a = new Timestamp(msg);
        a.Attestations.Add(new PendingAttestation("https://calendar-a.example"));

        var b = new Timestamp(msg);
        b.Attestations.Add(new PendingAttestation("https://calendar-b.example"));
        // Same attestation as `a` already has — must not become a duplicate.
        b.Attestations.Add(new PendingAttestation("https://calendar-a.example"));

        a.Merge(b);

        Assert.Equal(2, a.Attestations.Count);
        Assert.Contains(a.Attestations, x => x is PendingAttestation p && p.Uri == "https://calendar-a.example");
        Assert.Contains(a.Attestations, x => x is PendingAttestation p && p.Uri == "https://calendar-b.example");
    }
}
