using System.Security.Cryptography;
using System.Text;

namespace Certyfi.Infrastructure.Anchoring.OpenTimestamps;

// Direct port of python-opentimestamps' opentimestamps/core/op.py. Tag
// bytes and shapes read verbatim from that file. Only OpAppend/OpPrepend/
// OpSha256 are what real calendar servers actually emit in a merkle path
// (per the reference project's own comments), so those three fully
// implement Apply(); the rest parse correctly (so a proof containing them
// doesn't fail to deserialize) but Apply() throws NotSupportedException —
// LedgerAnchorBackgroundService treats that as "leave this checkpoint
// pending, don't crash" rather than a hard failure. See the plan's "Known
// gaps" section.
public abstract class Op : IEquatable<Op>, IComparable<Op>
{
    public abstract byte Tag { get; }

    protected abstract void SerializeBody(OtsWriter w);

    public void Serialize(OtsWriter w)
    {
        w.WriteUInt8(Tag);
        SerializeBody(w);
    }

    public abstract byte[] Apply(byte[] msg);

    // Comparable/equatable on (Tag, Arg) — mirrors Python's Op(tuple)
    // equality/ordering (self.TAG, tuple(self)), needed both for dict
    // keying (OpSet) and for Timestamp.Serialize's canonical sibling order.
    protected virtual byte[] ComparableArg => Array.Empty<byte>();

    public bool Equals(Op? other) =>
        other is not null && Tag == other.Tag && ComparableArg.AsSpan().SequenceEqual(other.ComparableArg);

    public override bool Equals(object? obj) => Equals(obj as Op);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tag);
        foreach (var b in ComparableArg) hash.Add(b);
        return hash.ToHashCode();
    }

    public int CompareTo(Op? other)
    {
        if (other is null) return 1;
        var tagCmp = Tag.CompareTo(other.Tag);
        if (tagCmp != 0) return tagCmp;
        return CompareBytes(ComparableArg, other.ComparableArg);
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

    public static Op DeserializeFromTag(OtsReader r, byte tag) => tag switch
    {
        0xf0 => new OpAppend(r.ReadVarBytes(4096, minLen: 1)),
        0xf1 => new OpPrepend(r.ReadVarBytes(4096, minLen: 1)),
        0xf2 => new OpReverse(),
        0xf3 => new OpHexlify(),
        0x02 => new OpSha1(),
        0x03 => new OpRipemd160(),
        0x08 => new OpSha256(),
        _ => throw new OtsFormatException($"Unknown operation tag 0x{tag:x2}"),
    };

    public static Op Deserialize(OtsReader r) => DeserializeFromTag(r, r.ReadUInt8());
}

public sealed class OpAppend : Op
{
    public byte[] Arg { get; }
    public OpAppend(byte[] arg) => Arg = arg;
    public override byte Tag => 0xf0;
    protected override byte[] ComparableArg => Arg;
    protected override void SerializeBody(OtsWriter w) => w.WriteVarBytes(Arg);
    public override byte[] Apply(byte[] msg) => [.. msg, .. Arg];
    public override string ToString() => $"append {Convert.ToHexString(Arg).ToLowerInvariant()}";
}

public sealed class OpPrepend : Op
{
    public byte[] Arg { get; }
    public OpPrepend(byte[] arg) => Arg = arg;
    public override byte Tag => 0xf1;
    protected override byte[] ComparableArg => Arg;
    protected override void SerializeBody(OtsWriter w) => w.WriteVarBytes(Arg);
    public override byte[] Apply(byte[] msg) => [.. Arg, .. msg];
    public override string ToString() => $"prepend {Convert.ToHexString(Arg).ToLowerInvariant()}";
}

public sealed class OpReverse : Op
{
    public override byte Tag => 0xf2;
    protected override void SerializeBody(OtsWriter w) { }
    public override byte[] Apply(byte[] msg) => msg.Reverse().ToArray();
    public override string ToString() => "reverse";
}

public sealed class OpHexlify : Op
{
    public override byte Tag => 0xf3;
    protected override void SerializeBody(OtsWriter w) { }
    public override byte[] Apply(byte[] msg) => Encoding.ASCII.GetBytes(Convert.ToHexString(msg).ToLowerInvariant());
    public override string ToString() => "hexlify";
}

public sealed class OpSha1 : Op
{
    public override byte Tag => 0x02;
    protected override void SerializeBody(OtsWriter w) { }
    public override byte[] Apply(byte[] msg) => SHA1.HashData(msg);
    public override string ToString() => "sha1";
}

// python-opentimestamps implements RIPEMD160 via hashlib, which most
// platforms (including .NET) no longer ship — real calendar servers never
// emit this op in practice (Bitcoin's own commitment path only uses
// SHA-256), so this deliberately parses but doesn't execute. See the type
// doc comment above.
public sealed class OpRipemd160 : Op
{
    public override byte Tag => 0x03;
    protected override void SerializeBody(OtsWriter w) { }
    public override byte[] Apply(byte[] msg) =>
        throw new NotSupportedException("RIPEMD-160 is not implemented — no real calendar server has been observed to emit it.");
    public override string ToString() => "ripemd160";
}

public sealed class OpSha256 : Op
{
    public override byte Tag => 0x08;
    public const int DigestLength = 32;
    protected override void SerializeBody(OtsWriter w) { }
    public override byte[] Apply(byte[] msg) => SHA256.HashData(msg);
    public override string ToString() => "sha256";
}
