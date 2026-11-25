using System.Text;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Uploads;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class PreparedUploadReplayCryptoTests
{
    [Fact]
    public void RotationDecryptsOldRecords_AndTamperingOrMissingKeyFailsClosed()
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        var hash = new string('a', 64);
        var options = Options.Create(new PreparedUploadEncryptionOptions
        {
            ActiveKeyId = "v2",
            Keys = new Dictionary<string, string>
            {
                ["v1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()),
                ["v2"] = Convert.ToBase64String(Enumerable.Range(32, 32).Select(x => (byte)x).ToArray())
            }
        });
        var crypto = new PreparedUploadReplayCrypto(options);
        var plaintext = Encoding.UTF8.GetBytes("{\"uploadToken\":\"secret\"}");
        var encrypted = crypto.Encrypt(id, owner, source, hash, "Csv", plaintext);
        Assert.Equal("v2", encrypted.KeyId);
        Assert.NotEqual(plaintext, encrypted.Ciphertext);
        var prepared = PreparedUpload.CreateReady(owner, source, "key", new string('b', 64), "storage", "input.csv", hash,
            AcceptedFileFormat.Csv, 100, new string('c', 64), encrypted.Ciphertext, encrypted.KeyId, "{}", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(30), id);
        Assert.Equal(plaintext, crypto.Decrypt(prepared));

        encrypted.Ciphertext[^1] ^= 1;
        Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(prepared));
        options.Value.Keys.Remove("v2");
        Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(prepared));
    }

    [Fact]
    public void ConfigurationValidation_RequiresActiveKeyAnd32ByteKeys()
    {
        var options = new PreparedUploadEncryptionOptions
        {
            ActiveKeyId = "v1",
            Keys = new Dictionary<string, string> { ["v1"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" }
        };
        Assert.True(PreparedUploadReplayCrypto.IsConfigurationValid(options));
        Assert.False(PreparedUploadReplayCrypto.IsConfigurationValid(new PreparedUploadEncryptionOptions
        {
            ActiveKeyId = "missing",
            Keys = options.Keys
        }));
        Assert.False(PreparedUploadReplayCrypto.IsConfigurationValid(new PreparedUploadEncryptionOptions
        {
            ActiveKeyId = "v1",
            Keys = new Dictionary<string, string> { ["v1"] = Convert.ToBase64String(new byte[31]) }
        }));
    }
}
