using System.Security.Cryptography;

namespace Certyfi.Infrastructure.Auth;

// One rule for every password a person chooses or is given: new users, new
// institutions' first admin, and password changes. Length is what matters
// most (NIST SP 800-63B); no composition rules.
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    // Null when acceptable, else a message for the person choosing it.
    public static string? Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        if (password.Length > MaxLength)
            return $"Password must be at most {MaxLength} characters.";
        if (password.Distinct().Count() < 4)
            return "Password is too simple.";
        if (!string.IsNullOrEmpty(email) && string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            return "Password can't be the same as the email address.";
        return null;
    }

    // A one-time password an admin hands to a user after a reset: 14
    // characters from an alphabet with no look-alikes, ~80 bits.
    public static string GenerateTemporary()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Create(14, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }
}
