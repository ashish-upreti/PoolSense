using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Repository Q&amp;A across one, several, or all applications. <c>projectIds</c> selects the scope; omitting it means every application.
/// Conversations are saved per exact scope, so single-application sessions remain visible from the per-project endpoints.
/// </summary>
[ApiController]
[Route("api/code-chat")]
[ServiceFilter(typeof(CodeApiFilter))]
public sealed class CodeChatScopeController(ICodeChatService service, ICodeRepositoryService repositories) : ControllerBase
{
    private const int MaxScopeSize = 500;

    [HttpPost("ask")]
    public async Task<ActionResult<CodeAskResponse>> Ask(CodeScopedAskRequest request, CancellationToken token) =>
        Ok(await service.AskAsync(CodeUser.Name(User), Scope(request.ProjectIds), request.ToAskRequest(), token));

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<CodeChatSessionSummary>>> History([FromQuery] string[]? projectIds, CancellationToken token) =>
        Ok(await service.SessionsAsync(CodeUser.Name(User), Scope(projectIds), token));

    [HttpGet("history/{sessionId}")]
    public async Task<ActionResult<CodeChatSession>> Session(string sessionId, CancellationToken token)
    {
        var session = await service.SessionAsync(CodeUser.Name(User), sessionId, token);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpDelete("history/{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken token) =>
        await service.DeleteSessionAsync(CodeUser.Name(User), sessionId, token) ? NoContent() : NotFound();

    [HttpGet("repositories")]
    public async Task<ActionResult<IReadOnlyList<CodeRepositoryResponse>>> Repositories([FromQuery] string[]? projectIds, CancellationToken token) =>
        Ok(await repositories.ListAsync(Scope(projectIds), token));

    private static CodeChatScope Scope(IReadOnlyList<string>? projectIds)
    {
        if (projectIds is { Count: > MaxScopeSize }) throw new ArgumentException($"Select at most {MaxScopeSize} applications.");
        if (projectIds?.Any(id => id.Length > 128) == true) throw new ArgumentException("Invalid application identifier.");
        return CodeChatScope.From(projectIds);
    }
}
