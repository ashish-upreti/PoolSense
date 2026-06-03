using PoolSense.Api.Contracts;

namespace PoolSense.Api.Services;

public interface ISessionPasswordStore
{
    void Store(string jti, string password, DateTimeOffset expiresAtUtc);

    bool TryGet(string jti, out SessionPasswordEntry? entry);

    void Remove(string jti);

    bool HasDecryptionFailure(string jti);
}