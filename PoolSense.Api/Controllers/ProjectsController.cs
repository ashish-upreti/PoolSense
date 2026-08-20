using Microsoft.AspNetCore.Mvc;
using PoolSense.Api.Data;
using PoolSense.Api.Logging;
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
    private readonly ITicketSourceApplicationRepository _ticketSourceApplicationRepository;
    private readonly ILogger<ProjectsController> _logger;
    private readonly IUserActivityAuditLogger _auditLogger;

    /// <summary>
    /// Request payload used to update email delivery settings for one application mapping.
    /// </summary>
    public sealed class ProjectEmailSettingsUpdateRequest
    {
        /// <summary>
        /// Exact value of the project_configs.application_filter column to update.
        /// </summary>
        /// <example>AT MPS Capacity Response</example>
        public string ApplicationFilter { get; set; } = string.Empty;

        /// <summary>
        /// Enables or disables recommendation email delivery for the matched application.
        /// </summary>
        /// <example>true</example>
        public bool SendEmail { get; set; }

        /// <summary>
        /// Semicolon-separated Lifeguard recipient email addresses.
        /// </summary>
        /// <example>lifeguard1@intel.com; lifeguard2@intel.com</example>
        public string EmailRecipients { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request payload used by external applications to create or update project settings by application name.
    /// </summary>
    public sealed class ExternalProjectSettingsUpsertRequest
    {
        /// <summary>
        /// Application name to store as both project_name and application_filter.
        /// </summary>
        /// <example>AT MPS Capacity Response</example>
        public string ApplicationName { get; set; } = string.Empty;

        /// <summary>
        /// Semicolon-separated Lifeguard recipient email addresses.
        /// </summary>
        /// <example>lifeguard1@intel.com; lifeguard2@intel.com</example>
        public string EmailRecipients { get; set; } = string.Empty;

        /// <summary>
        /// Compatibility input accepted from external callers.
        /// The persisted send_email value is normalized from PoolingEnabled for this API.
        /// </summary>
        /// <example>true</example>
        public bool SendEmail { get; set; }

        /// <summary>
        /// Enables or disables both send_email and pooling_enabled for this API.
        /// </summary>
        /// <example>true</example>
        public bool PoolingEnabled { get; set; }
    }

    /// <summary>
    /// Response payload returned for project configuration endpoints in this controller.
    /// </summary>
    public sealed class ProjectConfigResponse
    {
        public int Id { get; set; }
        public string ProjectId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public int KnowledgeLookbackYears { get; set; }
        public int SimilaritySearchLimit { get; set; }
        public bool SendEmail { get; set; }
        public bool PoolingEnabled { get; set; }
        public string EmailRecipients { get; set; } = string.Empty;
        public string ApplicationFilter { get; set; } = string.Empty;
        public string NyraKbNames { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectsController"/> class.
    /// </summary>
    /// <param name="projectRepository">The repository used to manage project registrations.</param>
    /// <param name="ticketSourceApplicationRepository">The repository used to write back the PoolSense flag to tbl_Application.</param>
    /// <param name="logger">Logger for this controller.</param>
    public ProjectsController(
        IProjectRepository projectRepository,
        ITicketSourceApplicationRepository ticketSourceApplicationRepository,
        ILogger<ProjectsController> logger,
        IUserActivityAuditLogger auditLogger)
    {
        _projectRepository = projectRepository;
        _ticketSourceApplicationRepository = ticketSourceApplicationRepository;
        _logger = logger;
        _auditLogger = auditLogger;
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
            _logger.LogInformation("Project created: {ProjectId} ({ProjectName}) by {User}.", createdProject.ProjectId, createdProject.ProjectName, User.Identity?.Name ?? "unknown");
            await _auditLogger.LogAsync("CreateProject", "ProjectConfig", createdProject.ProjectId,
                $"ProjectName={createdProject.ProjectName}; ApplicationFilter={createdProject.ApplicationFilter}; PoolingEnabled={createdProject.PoolingEnabled}; SendEmail={createdProject.SendEmail}", cancellationToken: cancellationToken);
            return CreatedAtAction(nameof(GetProjectById), new { projectId = createdProject.ProjectId }, ToResponse(createdProject));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating project.");
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
            _logger.LogError(ex, "Error retrieving projects.");
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
            _logger.LogError(ex, "Error retrieving project '{ProjectId}'.", projectId);
            return StatusCode(500, $"An error occurred while retrieving project '{projectId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing project configuration.
    /// </summary>
    [HttpPut("{projectId}")]
    public async Task<IActionResult> UpdateProject(string projectId, [FromBody] ProjectConfig request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Project configuration is required.");
        }

        request.ProjectId = projectId;

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
            updatedProject.NyraKbNames = request.NyraKbNames?.Trim() ?? string.Empty;

            var savedProject = await _projectRepository.UpdateProjectAsync(updatedProject, cancellationToken);
            if (savedProject is null)
                return NotFound($"Project '{projectId}' was not found.");

            await TryUpdateTicketSourcePoolSenseFlagAsync(savedProject.ApplicationFilter, savedProject.PoolingEnabled, cancellationToken);

            _logger.LogInformation("Project updated: {ProjectId} ({ProjectName}) by {User}.", savedProject.ProjectId, savedProject.ProjectName, User.Identity?.Name ?? "unknown");
            await _auditLogger.LogAsync("UpdateProject", "ProjectConfig", savedProject.ProjectId,
                $"ProjectName={savedProject.ProjectName}; ApplicationFilter={savedProject.ApplicationFilter}; PoolingEnabled={savedProject.PoolingEnabled}; SendEmail={savedProject.SendEmail}; EmailRecipients={savedProject.EmailRecipients}", cancellationToken: cancellationToken);

            return Ok(ToResponse(savedProject));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating project '{ProjectId}'.", projectId);
            return StatusCode(500, $"An error occurred while updating project '{projectId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Updates recommendation email settings for the project row that matches an application filter.
    /// </summary>
    /// <remarks>
    /// Sample request:
    ///
    ///     PATCH /api/projects/email-settings/by-application
    ///     {
    ///       "applicationFilter": "AT MPS Capacity Response",
    ///       "sendEmail": true,
    ///       "emailRecipients": "lifeguard1@intel.com; lifeguard2@intel.com"
    ///     }
    ///
    /// The application filter is matched against the exact value stored in dbo.project_configs.application_filter.
    /// Email recipients are normalized as semicolon-separated addresses before being saved.
    /// </remarks>
    /// <param name="request">The application filter plus the new send-email flag and recipient list.</param>
    /// <param name="cancellationToken">The cancellation token for the request.</param>
    /// <returns>The updated project configuration response.</returns>
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [HttpPatch("email-settings/by-application")]
    [HttpPut("email-settings/by-application")]
    [HttpPut("email-settings")]
    public async Task<IActionResult> UpdateEmailSettingsByApplicationFilter(
        [FromBody] ProjectEmailSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Email settings update request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationFilter))
        {
            ModelState.AddModelError(nameof(ProjectEmailSettingsUpdateRequest.ApplicationFilter), "ApplicationFilter is required.");
        }

        if (!TryNormalizeEmailRecipients(request.EmailRecipients, out var normalizedRecipients, out var errorMessage))
        {
            ModelState.AddModelError(nameof(ProjectEmailSettingsUpdateRequest.EmailRecipients), errorMessage);
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var existingProject = await _projectRepository.GetProjectByApplicationFilterAsync(request.ApplicationFilter.Trim(), cancellationToken);
            if (existingProject is null)
            {
                return NotFound($"Project with ApplicationFilter '{request.ApplicationFilter}' was not found.");
            }

            existingProject.SendEmail = request.SendEmail;
            existingProject.EmailRecipients = normalizedRecipients;

            var savedProject = await _projectRepository.UpdateProjectAsync(existingProject, cancellationToken);
            if (savedProject is null)
                return NotFound($"Project with ApplicationFilter '{request.ApplicationFilter}' was not found.");

            _logger.LogInformation("Email settings updated for ApplicationFilter '{ApplicationFilter}' by {User}.", savedProject.ApplicationFilter, User.Identity?.Name ?? "unknown");
            await _auditLogger.LogAsync("UpdateEmailSettings", "ProjectConfig", savedProject.ProjectId,
                $"ApplicationFilter={savedProject.ApplicationFilter}; SendEmail={savedProject.SendEmail}; EmailRecipients={savedProject.EmailRecipients}", cancellationToken: cancellationToken);

            return Ok(ToResponse(savedProject));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating email settings for ApplicationFilter '{ApplicationFilter}'.", request.ApplicationFilter);
            return StatusCode(500, $"An error occurred while updating email settings for ApplicationFilter '{request.ApplicationFilter}': {ex.Message}");
        }
    }

    /// <summary>
    /// Creates or updates one project configuration for an external application based on application name.
    /// </summary>
    /// <remarks>
    /// Sample request:
    ///
    ///     PUT /api/projects/external-settings/by-application
    ///     {
    ///       "applicationName": "AT MPS Capacity Response",
    ///       "emailRecipients": "lifeguard1@intel.com; lifeguard2@intel.com",
    ///       "sendEmail": true,
    ///       "poolingEnabled": true
    ///     }
    ///
    /// The application name is stored as both project_name and application_filter.
    /// If application_filter already exists, the matching row is updated.
    /// If it does not exist, a new project_configs row is created.
    /// For this API, both send_email and pooling_enabled are persisted from the PoolingEnabled input.
    /// </remarks>
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ProjectConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    [HttpPost("external-settings/by-application")]
    [HttpPut("external-settings/by-application")]
    public async Task<IActionResult> UpsertExternalProjectSettingsByApplication(
        [FromBody] ExternalProjectSettingsUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("External project settings request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ApplicationName))
        {
            ModelState.AddModelError(nameof(ExternalProjectSettingsUpsertRequest.ApplicationName), "ApplicationName is required.");
        }

        if (!TryNormalizeEmailRecipients(request.EmailRecipients, out var normalizedRecipients, out var errorMessage))
        {
            ModelState.AddModelError(nameof(ExternalProjectSettingsUpsertRequest.EmailRecipients), errorMessage);
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var normalizedApplicationName = request.ApplicationName.Trim();

        try
        {
            var existingProject = await _projectRepository.GetProjectByApplicationFilterAsync(normalizedApplicationName, cancellationToken);

            if (existingProject is not null)
            {
                existingProject.ProjectName = normalizedApplicationName;
                existingProject.ApplicationFilter = normalizedApplicationName;
                existingProject.EmailRecipients = normalizedRecipients;
                existingProject.SendEmail = request.PoolingEnabled;
                existingProject.PoolingEnabled = request.PoolingEnabled;

                var updatedProject = await _projectRepository.UpdateProjectAsync(existingProject, cancellationToken);
                if (updatedProject is null)
                    return StatusCode(500, $"Project with ApplicationName '{request.ApplicationName}' could not be updated.");

                await TryUpdateTicketSourcePoolSenseFlagAsync(updatedProject.ApplicationFilter, updatedProject.PoolingEnabled, cancellationToken);

                _logger.LogInformation("External project settings updated: {ProjectId} ({ApplicationName}) by {User}.", updatedProject.ProjectId, updatedProject.ProjectName, User.Identity?.Name ?? "unknown");
                await _auditLogger.LogAsync("UpsertExternalProjectSettings", "ProjectConfig", updatedProject.ProjectId,
                    $"Action=Update; ApplicationName={updatedProject.ProjectName}; PoolingEnabled={updatedProject.PoolingEnabled}; SendEmail={updatedProject.SendEmail}; EmailRecipients={updatedProject.EmailRecipients}", cancellationToken: cancellationToken);

                return Ok(ToResponse(updatedProject));
            }

            var projectId = await CreateUniqueProjectIdAsync(normalizedApplicationName, cancellationToken);
            var newProject = new ProjectConfig
            {
                ProjectId = projectId,
                ProjectName = normalizedApplicationName,
                ApplicationFilter = normalizedApplicationName,
                EmailRecipients = normalizedRecipients,
                SendEmail = request.PoolingEnabled,
                PoolingEnabled = request.PoolingEnabled,
                KnowledgeLookbackYears = 2,
                SimilaritySearchLimit = 5,
                TicketSourceType = "sql",
                ConnectionString = string.Empty,
                KnowledgeSources = []
            };

            var createdProject = await _projectRepository.CreateProjectAsync(newProject, cancellationToken);

            await TryUpdateTicketSourcePoolSenseFlagAsync(createdProject.ApplicationFilter, createdProject.PoolingEnabled, cancellationToken);

            _logger.LogInformation("External project settings created: {ProjectId} ({ApplicationName}) by {User}.", createdProject.ProjectId, createdProject.ProjectName, User.Identity?.Name ?? "unknown");
            await _auditLogger.LogAsync("UpsertExternalProjectSettings", "ProjectConfig", createdProject.ProjectId,
                $"Action=Create; ApplicationName={createdProject.ProjectName}; PoolingEnabled={createdProject.PoolingEnabled}; SendEmail={createdProject.SendEmail}; EmailRecipients={createdProject.EmailRecipients}", cancellationToken: cancellationToken);

            return Ok(ToResponse(createdProject));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving external project settings for ApplicationName '{ApplicationName}'.", request.ApplicationName);
            return StatusCode(500, $"An error occurred while saving external project settings for ApplicationName '{request.ApplicationName}': {ex.Message}");
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

    private async Task<string> CreateUniqueProjectIdAsync(string projectName, CancellationToken cancellationToken)
    {
        var baseProjectId = CreateProjectId(projectName);
        var candidateProjectId = baseProjectId;

        for (var suffix = 1; suffix <= 100; suffix++)
        {
            var existingProject = await _projectRepository.GetProjectByIdAsync(candidateProjectId, cancellationToken);
            if (existingProject is null)
            {
                return candidateProjectId;
            }

            candidateProjectId = $"{baseProjectId}-{suffix}";
        }

        return $"project-{Guid.NewGuid():N}"[..15];
    }

    private async Task TryUpdateTicketSourcePoolSenseFlagAsync(string applicationFilter, bool poolingEnabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationFilter))
        {
            _logger.LogWarning("TicketSource PoolSense flag not updated: ApplicationFilter is empty.");
            return;
        }

        try
        {
            await _ticketSourceApplicationRepository.UpdatePoolSenseFlagAsync(applicationFilter, poolingEnabled, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log but do not propagate — project_configs is already saved successfully.
            _logger.LogError(ex, "Failed to update PoolSense flag in tbl_Application for '{ApplicationFilter}'. The project_configs record was saved.", applicationFilter);
        }
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
            ApplicationFilter = request.ApplicationFilter?.Trim() ?? string.Empty,
            NyraKbNames = request.NyraKbNames?.Trim() ?? string.Empty
        };
    }

    private static ProjectConfigResponse ToResponse(ProjectConfig project)
    {
        return new ProjectConfigResponse
        {
            Id = project.Id,
            ProjectId = project.ProjectId,
            ProjectName = project.ProjectName,
            KnowledgeLookbackYears = project.KnowledgeLookbackYears,
            SimilaritySearchLimit = project.SimilaritySearchLimit,
            SendEmail = project.SendEmail,
            PoolingEnabled = project.PoolingEnabled,
            EmailRecipients = project.EmailRecipients,
            ApplicationFilter = project.ApplicationFilter,
            NyraKbNames = project.NyraKbNames,
            CreatedAt = project.CreatedAt
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
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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

        normalizedRecipients = string.Join("; ", recipients);
        return true;
    }
}
