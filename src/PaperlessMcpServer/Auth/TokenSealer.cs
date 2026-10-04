using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using PaperlessMcpServer.Configuration;

namespace PaperlessMcpServer.Auth;

/// <summary>
/// Purposes for sealed values. Each purpose uses its own derived key, so a value sealed for
/// one purpose (e.g. a refresh token) is never accepted as another (e.g. an access token).
/// </summary>
public enum SealPurpose
{
    Client,
    AuthorizationRequest,
    AuthorizationCode,
    AccessToken,
    RefreshToken,
}

/// <summary>
/// Stateless authenticated encryption (AES-256-GCM) of small JSON payloads.
/// Client registrations, authorization codes and tokens are self-contained encrypted blobs,
/// so the server needs no database and survives restarts as long as SECRET_KEY stays the same.
/// </summary>
public sealed class TokenSealer
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<SealPurpose, string> Prefixes = new()
    {
        [SealPurpose.Client] = "pmc_",
        [SealPurpose.AuthorizationRequest] = "pmr_",
        [SealPurpose.AuthorizationCode] = "pmac_",
        [SealPurpose.AccessToken] = "pmat_",
        [SealPurpose.RefreshToken] = "pmrt_",
    };

    private readonly Dictionary<SealPurpose, byte[]> _keys = new();
    private readonly byte[] _hmacKey;

    public TokenSealer(ServerOptions options) : this(options.SecretKey)
    {
    }

    public TokenSealer(string secret)
    {
        var ikm = Encoding.UTF8.GetBytes(secret);
        var salt = Encoding.UTF8.GetBytes("paperless-mcp-server");
        foreach (var purpose in Prefixes.Keys)
        {
            _keys[purpose] = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.UTF8.GetBytes($"seal:{purpose}"));
        }
        _hmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "hmac"u8.ToArray());
    }

    public string Seal<T>(SealPurpose purpose, T payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[purpose], TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), AssociatedData(purpose));
        return Prefixes[purpose] + WebEncoders.Base64UrlEncode(output);
    }

    /// <summary>Returns the decrypted payload, or default if the value is malformed or was tampered with.</summary>
    public T? Unseal<T>(SealPurpose purpose, string? value) where T : class
    {
        var prefix = Prefixes[purpose];
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        try
        {
            var raw = WebEncoders.Base64UrlDecode(value[prefix.Length..]);
            if (raw.Length < NonceSize + TagSize + 1) return null;
            var cipherLength = raw.Length - NonceSize - TagSize;
            var plaintext = new byte[cipherLength];
            using var aes = new AesGcm(_keys[purpose], TagSize);
            aes.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize, cipherLength), raw.AsSpan(NonceSize + cipherLength), plaintext, AssociatedData(purpose));
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Deterministic keyed hash, e.g. to derive client secrets or revocation keys.</summary>
    public string Hash(string purpose, string value)
    {
        var mac = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes($"{purpose}:{value}"));
        return WebEncoders.Base64UrlEncode(mac);
    }

    private static byte[] AssociatedData(SealPurpose purpose) => Encoding.UTF8.GetBytes(purpose.ToString());
}
