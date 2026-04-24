using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Data;
using PoolSense.Api.Models;
using System.Net.Mail;

namespace PoolSense.Api.Controllers;

/// <summary>
/// Provides endpoints for creating, listing, and updating project configurations.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _projectRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectsController"/> class.
    /// </summary>
    /// <param name="projectRepository">The repository used to manage project registrations.</param>
    public ProjectsController(IProjectRepository projectRepository)
    {
        _projectRepository = projectRepository;
    }

    /// <summary>
    /// Creates a project configuration.
    /// </summary>
    /// <param name="request">The project configuration to register.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The registered project configuration or an error response.</returns>
    [HttpPost]
    [HttpPost("register")]
    public async Task<IActionResult> CreateProject([FromBody] ProjectConfig request, CancellationToken cancellationToken)
    {
        var validationResult = ValidateRequest(request);
        if (validationResult is not null)
        {
            return validationResult;
        }

        try
        {
            var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
                ? CreateProjectId(request.ProjectName)
                : request.ProjectId.Trim();

            var existingProject = await _projectRepository.GetProjectByIdAsync(projectId, cancellationToken);
            if (existingProject is not null)
            {
                return Conflict($"A project with ProjectId '{projectId}' already exists.");
            }

            var project = CreateProjectConfig(request, projectId);
            project.ApplicationFilter = string.IsNullOrWhiteSpace(request.ApplicationFilter)
                ? request.ProjectName.Trim()
                : request.ApplicationFilter.Trim();

            var createdProject = await _projectRepository.CreateProjectAsync(project, cancellationToken);
            return CreatedAtAction(nameof(GetProjectById), new { projectId = createdProject.ProjectId }, ToResponse(createdProject));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while creating the project: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists all project configurations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The active project configurations.</returns>
    [HttpGet]
    public async Task<IActionResult> GetProjects(CancellationToken cancellationToken)
    {
        try
        {
            var projects = await _projectRepository.GetAllProjectsAsync(cancellationToken);
            return Ok(projects.Select(ToResponse));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving projects: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a project configuration by project ID.
    /// </summary>
    [HttpGet("{projectId}")]
    public async Task<IActionResult> GetProjectById(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projectRepository.GetProjectByIdAsync(projectId, cancellationToken);
            return project is null
                ? NotFound($"Project '{projectId}' was not found.")
                : Ok(ToResponse(project));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while retrieving project '{projectId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing project configuration.
    /// </summary>
    [HttpPut("{projectId}")]
    public async Task<IActionResult> UpdateProject(string projectId, [FromBody] ProjectConfig request, CancellationToken cancellationToken)
    {
        var validationResult = ValidateRequest(request, projectId);
        if (validationResult is not null)
        {
            return validationResult;
        }

        try
        {
            var existingProject = await _projectRepository.GetProjectByIdAsync(projectId, cancellationToken);
            if (existingProject is null)
            {
                return NotFound($"Project '{projectId}' was not found.");
            }

            var updatedProject = CreateProjectConfig(request, projectId);
            updatedProject.Id = existingProject.Id;
            updatedProject.CreatedAt = existingProject.CreatedAt;
            updatedProject.TicketSourceType = string.IsNullOrWhiteSpace(request.TicketSourceType)
                ? existingProject.TicketSourceType
                : request.TicketSourceType.Trim();
            updatedProject.ConnectionString = string.IsNullOrWhiteSpace(request.ConnectionString)
                ? existingProject.ConnectionString
                : request.ConnectionString.Trim();
            updatedProject.KnowledgeSources = request.KnowledgeSources.Count == 0
                ? existingProject.KnowledgeSources
                : request.KnowledgeSources;
            updatedProject.ApplicationFilter = string.IsNullOrWhiteSpace(request.ApplicationFilter)
                ? existingProject.ApplicationFilter
                : request.ApplicationFilter.Trim();

            var savedProject = await _projectRepository.UpdateProjectAsync(updatedProject, cancellationToken);
            return savedProject is null
                ? NotFound($"Project '{projectId}' was not found.")
                : Ok(ToResponse(savedProject));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred while updating project '{projectId}': {ex.Message}");
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
    /// Lists all configured project groups from the project configuration table.
    /// </summary>
    [HttpGet("groups")]
    public async Task<IActionResult> GetProjectGroups(CancellationToken cancellationToken)
    {
        var groups = (await _projectRepository.GetAllProjectsAsync(cancellationToken))
            .Where(project => !string.IsNullOrWhiteSpace(project.ProjectId) && !string.IsNullOrWhiteSpace(project.ProjectName))
            .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(project => new { groupId = project.ProjectId, displayName = project.ProjectName })
            .ToList();

        return Ok(new { groups });
    }

    private IActionResult? ValidateRequest(ProjectConfig? request, string? routeProjectId = null)
    {
        if (request is null)
        {
            return BadRequest("Project configuration is required.");
        }

        if (!string.IsNullOrWhiteSpace(routeProjectId)
            && !string.IsNullOrWhiteSpace(request.ProjectId)
            && !string.Equals(routeProjectId, request.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(ProjectConfig.ProjectId), "ProjectId in the request body must match the route value.");
        }

        if (string.IsNullOrWhiteSpace(routeProjectId) && string.IsNullOrWhiteSpace(request.ProjectId) && string.IsNullOrWhiteSpace(request.ProjectName))
        {
            ModelState.AddModelError(nameof(ProjectConfig.ProjectName), "ProjectName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
            ModelState.AddModelError(nameof(ProjectConfig.ProjectName), "ProjectName is required.");
        }

        if (request.SimilaritySearchLimit < 1 || request.SimilaritySearchLimit > 20)
        {
            ModelState.AddModelError(nameof(ProjectConfig.SimilaritySearchLimit), "SimilaritySearchLimit must be between 1 and 20.");
        }

        if (request.KnowledgeLookbackYears < 0)
        {
            ModelState.AddModelError(nameof(ProjectConfig.KnowledgeLookbackYears), "KnowledgeLookbackYears cannot be negative.");
        }

        if (!TryNormalizeEmailRecipients(request.EmailRecipients, out var normalizedRecipients, out var errorMessage))
        {
            ModelState.AddModelError(nameof(ProjectConfig.EmailRecipients), errorMessage);
        }
        else
        {
            request.EmailRecipients = normalizedRecipients;
        }

        return ModelState.IsValid ? null : ValidationProblem(ModelState);
    }

    private static ProjectConfig CreateProjectConfig(ProjectConfig request, string projectId)
    {
        return new ProjectConfig
        {
            ProjectId = projectId,
            ProjectName = request.ProjectName.Trim(),
            KnowledgeLookbackYears = request.KnowledgeLookbackYears,
            SimilaritySearchLimit = request.SimilaritySearchLimit,
            SendEmail = request.SendEmail,
            PoolingEnabled = request.PoolingEnabled,
            EmailRecipients = request.EmailRecipients,
            TicketSourceType = string.IsNullOrWhiteSpace(request.TicketSourceType) ? "sql" : request.TicketSourceType.Trim(),
            ConnectionString = request.ConnectionString?.Trim() ?? string.Empty,
            KnowledgeSources = request.KnowledgeSources ?? [],
            ApplicationFilter = request.ApplicationFilter?.Trim() ?? string.Empty
        };
    }

    private static object ToResponse(ProjectConfig project)
    {
        return new
        {
            id = project.Id,
            projectId = project.ProjectId,
            projectName = project.ProjectName,
            knowledgeLookbackYears = project.KnowledgeLookbackYears,
            similaritySearchLimit = project.SimilaritySearchLimit,
            sendEmail = project.SendEmail,
            poolingEnabled = project.PoolingEnabled,
            emailRecipients = project.EmailRecipients,
            applicationFilter = project.ApplicationFilter,
            createdAt = project.CreatedAt
        };
    }

    private static bool TryNormalizeEmailRecipients(string? emailRecipients, out string normalizedRecipients, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(emailRecipients))
        {
            normalizedRecipients = string.Empty;
            return true;
        }

        var recipients = emailRecipients
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
            .ToList();

        foreach (var recipient in recipients)
        {
            try
            {
                _ = new MailAddress(recipient);
            }
            catch (FormatException)
            {
                normalizedRecipients = string.Empty;
                errorMessage = $"'{recipient}' is not a valid email address.";
                return false;
            }
        }

        normalizedRecipients = string.Join(", ", recipients);
        return true;
    }
}
