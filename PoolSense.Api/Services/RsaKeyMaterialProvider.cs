using System.Security.Cryptography;

namespace PoolSense.Api.Services;

public sealed class RsaKeyMaterialProvider : IRsaKeyMaterialProvider, IDisposable
{
    private readonly ILogger<RsaKeyMaterialProvider> _logger;
    private readonly RSA? _rsa;

    public string PublicKeyPem { get; }

    public RsaKeyMaterialProvider(ILogger<RsaKeyMaterialProvider> logger)
    {
        _logger = logger;

        try
        {
            var rsa = RSA.Create();
            rsa.KeySize = 2048;
            PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
            _rsa = rsa;
        }
        catch (Exception ex)
        {
            PublicKeyPem = string.Empty;
            _rsa = null;
            _logger.LogError(ex, "RSA key material initialization failed. Encrypted-password auth will be unavailable.");
        }
    }

    public string DecryptBase64(string encryptedBase64)
    {
        if (_rsa is null)
        {
            throw new InvalidOperationException("RSA key material is unavailable on this server.");
        }

        var encryptedBytes = Convert.FromBase64String(encryptedBase64);
        var decryptedBytes = _rsa.Decrypt(encryptedBytes, RSAEncryptionPadding.OaepSHA256);
        return System.Text.Encoding.UTF8.GetString(decryptedBytes);
    }

    public void Dispose()
    {
        _rsa?.Dispose();
    }
}