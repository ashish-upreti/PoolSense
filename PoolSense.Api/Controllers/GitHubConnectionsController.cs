using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

[ApiController]
[Route("api/github")]
[ServiceFilter(typeof(CodeApiFilter))]
public sealed class GitHubConnectionsController(GitHubOAuthService oauth, ICodeRepositoryStore store,
    IOptions<CodeRAGOptions> options, CodeIndexFiles files) : ControllerBase
{
    [HttpGet("connection")]
    public async Task<IActionResult> Status(CancellationToken token)
    {
        var connection = await store.ConnectionAsync(CodeUser.Name(User), token);
        return Ok(new { connected = connection is not null, login = connection?.Login });
    }

    [HttpPost("connect")]
    public IActionResult Connect()
    {
        var (state, url) = oauth.Begin(CodeUser.Name(User), User.FindFirstValue("jti") ?? "");
        Response.Cookies.Append("poolsense-github-state", state, new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax,
            Path = "/api/github/callback", MaxAge = TimeSpan.FromMinutes(10)
        });
        return Ok(new { url });
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string state, string code, CancellationToken token)
    {
        var cookie = Request.Cookies["poolsense-github-state"];
        Response.Cookies.Delete("poolsense-github-state", new CookieOptions { Path = "/api/github/callback" });
        if (string.IsNullOrWhiteSpace(cookie) || cookie != state) throw new CodeAccessException();
        await files.Gate.WaitAsync(token);
        try { await oauth.CompleteAsync(CodeUser.Name(User), User.FindFirstValue("jti") ?? "", state, code, token); }
        finally { files.Gate.Release(); }
        return LocalRedirect(options.Value.ReturnUrl);
    }

    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect(CancellationToken token)
    {
        await files.Gate.WaitAsync(token);
        try { await oauth.DisconnectAsync(CodeUser.Name(User), token); }
        finally { files.Gate.Release(); }
        Response.Cookies.Delete("poolsense-github-state", new CookieOptions { Path = "/api/github/callback" });
        return NoContent();
    }
}