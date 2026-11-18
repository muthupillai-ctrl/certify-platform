using System.Text;

namespace Certyfi.Infrastructure.Anchoring.OpenTimestamps;

// Direct port of python-opentimestamps' DetachedTimestampFile
// (opentimestamps/core/timestamp.py) — the actual downloadable `.ots` file
// format: a fixed header/magic, a version byte, the file-hash op used (we
// always use OpSha256, since we anchor a SHA-256 digest), the raw digest,
// then the serialized Timestamp tree. HeaderMagic is built from the exact
// same literal bytes as the Python source's
// `b'\x00OpenTimestamps\x00\x00Proof\x00\xbf\x89\xe2\xe8\x84\xe8\x92\x94'`
// rather than a hand-transcribed hex string, to avoid a manual-transcription
// error in a magic-byte constant that would silently make every proof this
// app writes unreadable by real OpenTimestamps tooling.
public sealed class DetachedTimestampFile
{
    public const byte MajorVersion = 1;

    public static readonly byte[] HeaderMagic = BuildHeaderMagic();

    private static byte[] BuildHeaderMagic()
    {
        var bytes = new List<byte> { 0x00 };
        bytes.AddRange(Encoding.ASCII.GetBytes("OpenTimestamps"));
        bytes.Add(0x00);
        bytes.Add(0x00);
        bytes.AddRange(Encoding.ASCII.GetBytes("Proof"));
        bytes.Add(0x00);
        bytes.AddRange(new byte[] { 0xbf, 0x89, 0xe2, 0xe8, 0x84, 0xe8, 0x92, 0x94 });
        return bytes.ToArray();
    }

    public Op FileHashOp { get; }
    public Timestamp Timestamp { get; }

    public byte[] FileDigest => Timestamp.Msg;

    public DetachedTimestampFile(Op fileHashOp, Timestamp timestamp)
    {
        FileHashOp = fileHashOp;
        Timestamp = timestamp;
    }

    public byte[] Serialize()
    {
        var w = new OtsWriter();
        w.WriteBytes(HeaderMagic);
        w.WriteUInt8(MajorVersion);
        FileHashOp.Serialize(w);
        w.WriteBytes(Timestamp.Msg);
        Timestamp.Serialize(w);
        return w.ToArray();
    }

    public static DetachedTimestampFile Deserialize(byte[] data)
    {
        var r = new OtsReader(data);
        r.AssertMagic(HeaderMagic);

        var major = r.ReadUInt8();
        if (major != MajorVersion)
        {
            throw new OtsFormatException($"Version {major} detached timestamp files are not supported.");
        }

        var fileHashOp = Op.Deserialize(r);
        var digestLength = fileHashOp switch
        {
            OpSha256 => 32,
            OpSha1 => 20,
            OpRipemd160 => 20,
            _ => throw new OtsFormatException($"File hash op {fileHashOp} is not a cryptographic digest op."),
        };
        var fileHash = r.ReadBytes(digestLength);
        var timestamp = Timestamp.Deserialize(r, fileHash);

        r.AssertEof();
        return new DetachedTimestampFile(fileHashOp, timestamp);
    }
}
