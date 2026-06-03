namespace PoolSense.Api.Services;

public interface IRsaKeyMaterialProvider
{
    string PublicKeyPem { get; }

    string DecryptBase64(string encryptedBase64);
}