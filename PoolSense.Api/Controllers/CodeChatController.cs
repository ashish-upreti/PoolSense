using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

/// <summary>Repository Q&amp;A over a project's indexed code, with per-user saved conversations.</summary>
[ApiController]
[Route("api/projects/{projectId}/code-chat")]
[ServiceFilter(typeof(CodeApiFilter))]
public sealed class CodeChatController(ICodeChatService service) : ControllerBase
{
    [HttpPost("ask")]
    public async Task<ActionResult<CodeAskResponse>> Ask(string projectId, CodeAskRequest request, CancellationToken token) =>
        Ok(await service.AskAsync(CodeUser.Name(User), projectId, request, token));

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<CodeChatSessionSummary>>> History(string projectId, CancellationToken token) =>
        Ok(await service.SessionsAsync(CodeUser.Name(User), projectId, token));

    [HttpGet("history/{sessionId}")]
    public async Task<ActionResult<CodeChatSession>> Session(string projectId, string sessionId, CancellationToken token)
    {
        var session = await service.SessionAsync(CodeUser.Name(User), projectId, sessionId, token);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpDelete("history/{sessionId}")]
    public async Task<IActionResult> Delete(string projectId, string sessionId, CancellationToken token) =>
        await service.DeleteSessionAsync(CodeUser.Name(User), sessionId, token) ? NoContent() : NotFound();
}
