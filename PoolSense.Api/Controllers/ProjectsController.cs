using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoolSense.Api.Configuration;
using PoolSense.Api.Data;
using PoolSense.Api.Models;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for registering and listing project configurations.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _projectRepository;
    private readonly TicketAutomationSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectsController"/> class.
    /// </summary>
    /// <param name="projectRepository">The repository used to manage project registrations.</param>
    /// <param name="settings">The ticket automation settings containing configured project groups.</param>
    public ProjectsController(IProjectRepository projectRepository, IOptions<TicketAutomationSettings> settings)
    {
        _projectRepository = projectRepository;
        _settings = settings.Value;
    }

    /// <summary>
    /// Registers a project configuration for ticket ingestion and knowledge enrichment.
    /// </summary>
    /// <param name="request">The project configuration to register.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The registered project configuration or an error response.</returns>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] ProjectConfig request, CancellationToken cancellationToken)
    {
        if (request == null
            || string.IsNullOrWhiteSpace(request.ProjectName)
            || string.IsNullOrWhiteSpace(request.TicketSourceType)
            || string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest("ProjectName, TicketSourceType, and ConnectionString are required.");
        }

        try
        {
            var project = new ProjectConfig
            {
                ProjectId = string.IsNullOrWhiteSpace(request.ProjectId)
                    ? CreateProjectId(request.ProjectName)
                    : request.ProjectId,
                ProjectName = request.ProjectName,
                TicketSourceType = request.TicketSourceType,
                ConnectionString = request.ConnectionString,
                KnowledgeSources = request.KnowledgeSources ?? [],
                IsActive = true
            };

            await _projectRepository.RegisterProject(project, cancellationToken);
            return Ok(project);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while registering the project: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists all active project configurations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The active project configurations.</returns>
    [HttpGet]
    public async Task<IActionResult> GetProjects(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _projectRepository.ListActiveProjects(cancellationToken);
            return Ok(new
            {
                projects = projects.Select(project => new
                {
                    projectId = project.ProjectId,
                    projectName = project.ProjectName,
                    ticketSourceType = project.TicketSourceType,
                    knowledgeSources = project.KnowledgeSources,
                    isActive = project.IsActive
                })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving projects: {ex.Message}");
        }
    }

    private static string CreateProjectId(string projectName)
    {
        var normalized = new string(projectName
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(normalized)
            ? $"project-{Guid.NewGuid():N}"[..15]
            : normalized;
    }

    /// <summary>
    /// Lists all configured project groups from application settings.
    /// Returns all groups plus a virtual "All" option for combined-group search.
    /// </summary>
    [HttpGet("groups")]
    public IActionResult GetProjectGroups()
    {
        var groups = _settings.ProjectGroups
            .Select(g => new { groupId = g.GroupId, displayName = g.DisplayName })
            .ToList();

        return Ok(new { groups });
    }
}
