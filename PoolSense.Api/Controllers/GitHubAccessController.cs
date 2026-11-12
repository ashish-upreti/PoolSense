using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/github")]
[ServiceFilter(typeof(CodeApiFilter))]
public sealed class GitHubAccessController(IManagedGitHubAccess access) : ControllerBase
{
    [HttpGet("access")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        if (!access.IsConfigured)
            return Ok(new { configured = false, available = false, login = (string?)null,
                message = "GitHub access is managed by the administrator. CodeRAG:GitHubToken is not configured." });

        try
        {
            var connection = await access.GetIdentityAsync(token);
            return Ok(new { configured = true, available = true, login = connection.Login, message = "" });
        }
        catch (CodeAccessException exception)
        {
            return Ok(new { configured = true, available = false, login = (string?)null, message = exception.Message });
        }
    }
}
