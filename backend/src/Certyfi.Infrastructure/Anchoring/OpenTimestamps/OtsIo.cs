namespace Certyfi.Infrastructure.Anchoring.OpenTimestamps;

// Direct port of python-opentimestamps' StreamSerializationContext /
// StreamDeserializationContext (opentimestamps/core/serialize.py) — read
// verbatim from the reference implementation, not paraphrased, after the
// Phase 1 DER-vs-P1363 signature bug taught that same-runtime "it compiles
// and round-trips" is not proof of wire-format correctness.
public sealed class OtsWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteUInt8(byte value) => _stream.WriteByte(value);

    public void WriteBytes(byte[] value) => _stream.Write(value, 0, value.Length);

    // Unsigned LEB128: 7 bits per byte, high bit = "more bytes follow".
    public void WriteVarUint(ulong value)
    {
        if (value == 0)
        {
            _stream.WriteByte(0);
            return;
        }

        while (value != 0)
        {
            var b = (byte)(value & 0b0111_1111);
            if (value > 0b0111_1111)
            {
                b |= 0b1000_0000;
            }
            _stream.WriteByte(b);
            if (value <= 0b0111_1111)
            {
                break;
            }
            value >>= 7;
        }
    }

    public void WriteVarBytes(byte[] value)
    {
        WriteVarUint((ulong)value.Length);
        WriteBytes(value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

public sealed class OtsFormatException : FormatException
{
    public OtsFormatException(string message) : base(message)
    {
    }
}

public sealed class OtsReader
{
    private readonly byte[] _buf;
    private int _pos;

    public OtsReader(byte[] buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public int Position => _pos;

    private byte[] ReadRaw(int length)
    {
        if (_pos + length > _buf.Length)
        {
            throw new OtsFormatException($"Tried to read {length} bytes but only {_buf.Length - _pos} remain.");
        }
        var result = new byte[length];
        Array.Copy(_buf, _pos, result, 0, length);
        _pos += length;
        return result;
    }

    public byte ReadUInt8() => ReadRaw(1)[0];

    public byte[] ReadBytes(int expectedLength) => ReadRaw(expectedLength);

    public ulong ReadVarUint()
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadUInt8();
            value |= (ulong)(b & 0b0111_1111) << shift;
            if ((b & 0b1000_0000) == 0)
            {
                break;
            }
            shift += 7;
        }
        return value;
    }

    public byte[] ReadVarBytes(int maxLen, int minLen = 0)
    {
        var len = checked((int)ReadVarUint());
        if (len > maxLen)
        {
            throw new OtsFormatException($"varbytes max length exceeded; {len} > {maxLen}");
        }
        if (len < minLen)
        {
            throw new OtsFormatException($"varbytes min length not met; {len} < {minLen}");
        }
        return ReadRaw(len);
    }

    public void AssertMagic(byte[] expectedMagic)
    {
        var actual = ReadRaw(expectedMagic.Length);
        if (!actual.AsSpan().SequenceEqual(expectedMagic))
        {
            throw new OtsFormatException(
                $"Expected magic bytes 0x{Convert.ToHexString(expectedMagic)}, but got 0x{Convert.ToHexString(actual)} instead.");
        }
    }

    public void AssertEof()
    {
        if (_pos != _buf.Length)
        {
            throw new OtsFormatException("Trailing garbage found after end of deserialized data.");
        }
    }
}
