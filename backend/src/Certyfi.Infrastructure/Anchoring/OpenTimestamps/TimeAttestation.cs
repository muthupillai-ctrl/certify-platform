using System.Text;

namespace Certyfi.Infrastructure.Anchoring.OpenTimestamps;

// Direct port of python-opentimestamps' opentimestamps/core/notary.py.
// Tag bytes and payload shapes (including the "payload is itself a
// varbytes/varuint-encoded inner value" double-wrapping) read verbatim from
// that file's serialize()/deserialize() and PendingAttestation/
// BitcoinBlockHeaderAttestation classes.
public abstract class TimeAttestation : IEquatable<TimeAttestation>, IComparable<TimeAttestation>
{
    private const int MaxPayloadSize = 8192;

    public abstract byte[] Tag { get; } // exactly 8 bytes

    protected abstract void SerializePayload(OtsWriter w);

    public void Serialize(OtsWriter w)
    {
        w.WriteBytes(Tag);
        var payloadWriter = new OtsWriter();
        SerializePayload(payloadWriter);
        w.WriteVarBytes(payloadWriter.ToArray());
    }

    public static TimeAttestation Deserialize(OtsReader r)
    {
        var tag = r.ReadBytes(8);
        var payload = r.ReadVarBytes(MaxPayloadSize);
        var payloadReader = new OtsReader(payload);

        TimeAttestation result;
        if (tag.AsSpan().SequenceEqual(PendingAttestation.TagBytes))
        {
            result = PendingAttestation.DeserializePayload(payloadReader);
        }
        else if (tag.AsSpan().SequenceEqual(BitcoinBlockHeaderAttestation.TagBytes))
        {
            result = BitcoinBlockHeaderAttestation.DeserializePayload(payloadReader);
        }
        else if (tag.AsSpan().SequenceEqual(LitecoinBlockHeaderAttestation.TagBytes))
        {
            result = LitecoinBlockHeaderAttestation.DeserializePayload(payloadReader);
        }
        else
        {
            return new UnknownAttestation(tag, payload);
        }

        // Forces future-upgrade fields to be explicit rather than silently
        // ignored — matches notary.py's own comment on this exact call.
        payloadReader.AssertEof();
        return result;
    }

    protected abstract byte[] ComparableKey { get; }

    public bool Equals(TimeAttestation? other) =>
        other is not null && GetType() == other.GetType() && Tag.AsSpan().SequenceEqual(other.Tag) &&
        ComparableKey.AsSpan().SequenceEqual(other.ComparableKey);

    public override bool Equals(object? obj) => Equals(obj as TimeAttestation);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        foreach (var b in ComparableKey) hash.Add(b);
        return hash.ToHashCode();
    }

    public int CompareTo(TimeAttestation? other)
    {
        if (other is null) return 1;
        var tagCmp = CompareBytes(Tag, other.Tag);
        if (tagCmp != 0) return tagCmp;
        return CompareBytes(ComparableKey, other.ComparableKey);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            var cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }
}

// "Not yet Bitcoin-confirmed" — commitment recorded with a remote calendar,
// uri is where to poll for the upgraded (eventually Bitcoin-attested) proof.
public sealed class PendingAttestation : TimeAttestation
{
    public static readonly byte[] TagBytes = Convert.FromHexString("83dfe30d2ef90c8e");
    private const int MaxUriLength = 1000;

    public string Uri { get; }
    public PendingAttestation(string uri) => Uri = uri;

    public override byte[] Tag => TagBytes;
    protected override byte[] ComparableKey => Encoding.UTF8.GetBytes(Uri);
    protected override void SerializePayload(OtsWriter w) => w.WriteVarBytes(Encoding.UTF8.GetBytes(Uri));

    public static PendingAttestation DeserializePayload(OtsReader r)
    {
        var uriBytes = r.ReadVarBytes(MaxUriLength);
        return new PendingAttestation(Encoding.UTF8.GetString(uriBytes));
    }
}

// "Confirmed" — this IS the signal a checkpoint has been irreversibly
// anchored: the leaf message this attestation is attached to (see
// Timestamp.AllAttestations) equals the named Bitcoin block's merkle root,
// in that block's internal (non-reversed) byte order — NOT the
// byte-reversed hex a block explorer displays. See
// LedgerAnchorBackgroundService.VerifyAgainstBitcoinBlockAsync.
public sealed class BitcoinBlockHeaderAttestation : TimeAttestation
{
    public static readonly byte[] TagBytes = Convert.FromHexString("0588960d73d71901");

    public ulong Height { get; }
    public BitcoinBlockHeaderAttestation(ulong height) => Height = height;

    public override byte[] Tag => TagBytes;
    protected override byte[] ComparableKey => BitConverter.GetBytes(Height);
    protected override void SerializePayload(OtsWriter w) => w.WriteVarUint(Height);

    public static BitcoinBlockHeaderAttestation DeserializePayload(OtsReader r) => new(r.ReadVarUint());
}

// Structurally identical to Bitcoin's — parsed for forward-compatibility
// with proofs that carry it, but never acted on (Bitcoin-only anchoring is
// this plan's scope; see "Known gaps").
public sealed class LitecoinBlockHeaderAttestation : TimeAttestation
{
    public static readonly byte[] TagBytes = Convert.FromHexString("06869a0d73d71b45");

    public ulong Height { get; }
    public LitecoinBlockHeaderAttestation(ulong height) => Height = height;

    public override byte[] Tag => TagBytes;
    protected override byte[] ComparableKey => BitConverter.GetBytes(Height);
    protected override void SerializePayload(OtsWriter w) => w.WriteVarUint(Height);

    public static LitecoinBlockHeaderAttestation DeserializePayload(OtsReader r) => new(r.ReadVarUint());
}

// Forward-compatible fallback for any attestation tag this parser doesn't
// recognize yet — never throws, keeps the raw payload bytes as-is so a
// proof round-trips even through attestation types invented after this
// code was written.
public sealed class UnknownAttestation : TimeAttestation
{
    private readonly byte[] _tag;
    public byte[] Payload { get; }

    public UnknownAttestation(byte[] tag, byte[] payload)
    {
        _tag = tag;
        Payload = payload;
    }

    public override byte[] Tag => _tag;
    protected override byte[] ComparableKey => Payload;
    protected override void SerializePayload(OtsWriter w) => w.WriteBytes(Payload);
}
