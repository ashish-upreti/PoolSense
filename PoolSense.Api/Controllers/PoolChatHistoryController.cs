using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Models;
using PoolSense.Api.Services;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Per-user PoolSense triage chat history: list, reopen, rename/pin and delete saved conversations.
/// The username always comes from the authenticated principal; requests can never target another user's history.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/ticket/history")]
public class PoolChatHistoryController(IPoolChatHistoryStore store) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PoolChatSessionSummary>>> List(CancellationToken cancellationToken)
    {
        return Ok(await store.SessionsAsync(CodeUser.Name(User), cancellationToken));
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> Get(string sessionId, CancellationToken cancellationToken)
    {
        var session = await store.SessionAsync(CodeUser.Name(User), sessionId, cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpPatch("{sessionId}")]
    public async Task<IActionResult> Patch(string sessionId, [FromBody] PoolChatSessionPatchRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("A request body is required.");
        }

        string? title = null;
        if (request.Title is not null)
        {
            title = request.Title.Trim();
            if (title.Length == 0 || title.Length > 100)
            {
                return BadRequest("Title must be between 1 and 100 characters.");
            }
        }

        if (title is null && request.IsPinned is null)
        {
            return BadRequest("Provide a title or isPinned value to update.");
        }

        var updated = await store.UpdateSessionAsync(CodeUser.Name(User), sessionId, title, request.IsPinned, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{sessionId}")]
    public async Task<IActionResult> Delete(string sessionId, CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteSessionAsync(CodeUser.Name(User), sessionId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
