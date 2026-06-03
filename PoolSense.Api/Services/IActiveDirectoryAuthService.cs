using PoolSense.Api.Contracts;

namespace PoolSense.Api.Services;

public interface IActiveDirectoryAuthService
{
    Task<AuthResult> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
}