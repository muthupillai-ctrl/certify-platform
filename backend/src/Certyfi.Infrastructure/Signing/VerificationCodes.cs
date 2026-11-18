using System.Security.Cryptography;

namespace Certyfi.Infrastructure.Signing;

// Certificate verification codes: 12 characters from a 29-symbol alphabet
// with no look-alikes (no 0/O, 1/I/L, U/V) — about 58 bits of randomness, so
// codes can't be guessed or walked. Displayed in groups of four
// (K7QM-3XPD-9TWA); input is accepted with or without dashes, spaces or
// lower case.
public static class VerificationCodes
{
    public const int Length = 12;
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTWXYZ";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }

    // Null when the input can't be a code (wrong length or characters).
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var cleaned = new string(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return cleaned.Length == Length && cleaned.All(c => Alphabet.Contains(c)) ? cleaned : null;
    }

    public static string Format(string code) =>
        code.Length == Length ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;
}
