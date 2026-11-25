using System.Security.Cryptography;
using System.Text;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Entities;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Uploads;

public sealed record EncryptedReplay(byte[] Ciphertext, string KeyId);

public sealed class PreparedUploadReplayCrypto(IOptions<PreparedUploadEncryptionOptions> options)
{
    public static bool IsConfigurationValid(PreparedUploadEncryptionOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ActiveKeyId) || settings.ActiveKeyId.Length > 100 ||
            !settings.Keys.ContainsKey(settings.ActiveKeyId) || settings.Keys.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100))
        {
            return false;
        }

        try
        {
            return settings.Keys.Values.All(encoded => Convert.FromBase64String(encoded).Length == 32);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public EncryptedReplay Encrypt(Guid preparedUploadId, Guid ownerUserId, Guid dataSourceId, string fileSha256,
        string format, byte[] plaintext)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ActiveKeyId) || !settings.Keys.TryGetValue(settings.ActiveKeyId, out var encoded))
        {
            throw new InvalidOperationException("Prepared upload replay encryption is not configured.");
        }

        var key = DecodeKey(encoded);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag,
                    Aad(preparedUploadId, ownerUserId, dataSourceId, fileSha256, format));
            }

            var result = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            result[0] = 1;
            nonce.CopyTo(result, 1);
            tag.CopyTo(result, 1 + nonce.Length);
            ciphertext.CopyTo(result, 1 + nonce.Length + tag.Length);
            return new(result, settings.ActiveKeyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(PreparedUpload upload)
    {
        if (upload.ReplayResponseCiphertext is not { Length: > 29 } payload ||
            !options.Value.Keys.TryGetValue(upload.ReplayKeyId, out var encoded))
        {
            throw new InvalidOperationException("Prepared upload replay data cannot be decrypted.");
        }

        if (payload[0] != 1)
        {
            throw new InvalidOperationException("Prepared upload replay data has an unsupported format.");
        }

        var key = DecodeKey(encoded);
        try
        {
            var nonce = payload.AsSpan(1, 12);
            var tag = payload.AsSpan(13, 16);
            var ciphertext = payload.AsSpan(29);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext,
                Aad(upload.Id, upload.OwnerUserId, upload.DataSourceId, upload.FileSha256,
                    UploadFingerprints.PublicFormat(upload.Format)));
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("Prepared upload replay data cannot be decrypted.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Aad(Guid id, Guid owner, Guid source, string hash, string format) => Encoding.UTF8.GetBytes(
        $"prepare-replay-v1|{id:N}|{owner:N}|{source:N}|{hash.ToLowerInvariant()}|{format}");

    private static byte[] DecodeKey(string encoded)
    {
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32)
            {
                throw new InvalidOperationException("Prepared upload replay keys must contain exactly 32 bytes.");
            }

            return key;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Prepared upload replay keys must be Base64 encoded.", exception);
        }
    }
}
