using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PoolSense.Api.Services.CodeRAG;

namespace PoolSense.Api.Controllers;

public sealed class CodeApiFilter(IOptions<CodeRAGOptions> options) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!options.Value.Enabled)
        {
            context.Result = Error(503, "Code knowledge is not enabled.");
            return;
        }
        if (!HttpMethods.IsGet(request.Method) && request.Headers["X-PoolSense-CSRF"] != "1")
        {
            context.Result = Error(400, "Missing request verification header.");
            return;
        }
        var result = await next();
        if (result.Exception is null || result.Exception is OperationCanceledException) return;
        var (status, title) = result.Exception switch
        {
            GitHubDisconnectException disconnect => (503, disconnect.Message),
            CodeAccessException => (403, "GitHub access unavailable. Connect GitHub and check repository access."),
            KeyNotFoundException => (404, "Repository or project not found."),
            ArgumentException argument => (400, argument.Message),
            InvalidOperationException => (409, "Repository changed or is busy. Reload and retry."),
            SqlException sql when sql.Number is 2601 or 2627 => (409, "Repository is already configured."),
            _ => (503, "Code knowledge is temporarily unavailable.")
        };
        result.Result = Error(status, title);
        result.ExceptionHandled = true;
    }

    private static ObjectResult Error(int status, string title) => new(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
}

[ApiController]
[Route("api/projects/{projectId}/repositories")]
[ServiceFilter(typeof(CodeApiFilter))]
public sealed class ProjectCodeRepositoriesController(ICodeRepositoryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CodeRepositoryResponse>>> List(string projectId, CancellationToken token) =>
        Ok(await service.ListAsync(CodeUser.Name(User), projectId, token));

    [HttpGet("{id}")]
    public async Task<ActionResult<CodeRepositoryResponse>> Get(string projectId, string id, CancellationToken token)
    {
        var record = (await service.ListAsync(CodeUser.Name(User), projectId, token)).FirstOrDefault(entry => entry.Id == id);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost]
    public async Task<ActionResult<CodeRepositoryResponse>> Create(string projectId, SaveCodeRepositoryRequest request, CancellationToken token)
    {
        var record = await service.SaveAsync(CodeUser.Name(User), projectId, null, request, token);
        return CreatedAtAction(nameof(Get), new { projectId, id = record.Id }, record);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<CodeRepositoryResponse>> Update(string projectId, string id, SaveCodeRepositoryRequest request, CancellationToken token) =>
        Ok(await service.SaveAsync(CodeUser.Name(User), projectId, id, request, token));

    [HttpPost("{id}/refresh")]
    public async Task<ActionResult<CodeRepositoryResponse>> Refresh(string projectId, string id, CancellationToken token) =>
        AcceptedAtAction(nameof(Get), new { projectId, id }, await service.RefreshAsync(CodeUser.Name(User), projectId, id, false, token));

    [HttpPost("{id}/take-ownership")]
    public async Task<ActionResult<CodeRepositoryResponse>> TakeOwnership(string projectId, string id, CancellationToken token) =>
        AcceptedAtAction(nameof(Get), new { projectId, id }, await service.RefreshAsync(CodeUser.Name(User), projectId, id, true, token));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string projectId, string id, CancellationToken token)
    {
        await service.DeleteAsync(CodeUser.Name(User), projectId, id, token);
        return NoContent();
    }
}