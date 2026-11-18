using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Certyfi.Infrastructure.Signing;

// Encrypts small secrets Certyfi has to read back (a partner's webhook
// signing secret) with AES-GCM under a key derived from the platform master
// key, like tenant signing keys. Stored as base64(nonce | ciphertext | tag).
public class SecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretProtector(IOptions<KmsOptions> kms)
    {
        var master = Convert.FromBase64String(kms.Value.LocalMasterKey);
        _key = HMACSHA256.HashData(master, Encoding.UTF8.GetBytes("certyfi-webhook-secret-v1"));
    }

    public string Protect(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length, TagSize));
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedSecret)
    {
        var input = Convert.FromBase64String(protectedSecret);
        var length = input.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length, TagSize), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
