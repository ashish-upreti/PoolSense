using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace PoolSense.Api.Services.CodeRAG;

public sealed class CodeFeedbackPolicy(IDataProtectionProvider provider, IHttpContextAccessor context, IOptions<CodeRAGOptions> options)
{
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("PoolSense.SharedFeedback.v1").ToTimeLimitedDataProtector();
    private string Payload(string answer) => CodeUser.Name(context.HttpContext?.User) + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(answer.Trim())));
    public string Permit(string answer) => options.Value.Enabled ? protector.Protect(Payload(answer), TimeSpan.FromHours(24)) : "";
    public bool Allows(string answer, string? permit)
    {
        if (!options.Value.Enabled) return true;
        if (string.IsNullOrWhiteSpace(permit)) return false;
        try { return protector.Unprotect(permit, out _) == Payload(answer); }
        catch (CryptographicException) { return false; }
    }
}