using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using PoolSense.Api.Configuration;
using PoolSense.Api.Logging;
using PoolSense.Api.Models;

namespace PoolSense.Api.Agents;

public interface IResolutionAgent
{
    Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarHistoricalIncidents, IReadOnlyList<NyraDocumentResult> nyraDocuments);
}

public sealed class ResolutionIncident
{
    public string TicketId { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public double FeedbackScore { get; set; }
    public string LatestHumanValidatedFix { get; set; } = string.Empty;
    public string LatestHumanAvoidanceNote { get; set; } = string.Empty;
    public string LatestHelpfulComment { get; set; } = string.Empty;
    public string LatestNotHelpfulComment { get; set; } = string.Empty;
}

public class ResolutionAgent : IResolutionAgent
{
    private readonly Kernel _kernel;
    private readonly ILlmTokenUsageRepository _tokenUsageRepository;
    private readonly IOptionsMonitor<NyraSettings> _nyraSettings;

    public ResolutionAgent(
        Kernel kernel,
        ILlmTokenUsageRepository tokenUsageRepository,
        IOptionsMonitor<NyraSettings> nyraSettings)
    {
        _kernel = kernel;
        _tokenUsageRepository = tokenUsageRepository;
        _nyraSettings = nyraSettings;
    }

    public Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarHistoricalIncidents, IReadOnlyList<NyraDocumentResult> nyraDocuments)
    {
        var incidentsJson = JsonSerializer.Serialize(similarHistoricalIncidents);
        var nyraDocumentsJson = JsonSerializer.Serialize(nyraDocuments);

        const string prompt = @"
You are an AI system that suggests resolutions for engineering support tickets.

You will be given a new ticket, a ranked list of similar historical incidents from the PoolSense database (most similar first), and optional NYRA wiki documents retrieved from project-specific knowledge bases.

IMPORTANT: The 'RootCause' field in historical incidents is an AI-generated summary and is often generic or inaccurate (e.g. 'Solver configuration mismatch causing...' repeated across unrelated tickets). Do NOT trust or copy it blindly.
The 'Problem' field is the most reliable and specific description of what actually happened in each historical incident.
The 'Resolution' field describes the steps that were taken to fix the issue.
NYRA wiki documents are product documentation. Use them when they directly explain the failing component, procedure, or configuration for this ticket. Do not treat a NYRA document as relevant solely because it shares broad vocabulary.

New Ticket Title:
{{$title}}

New Ticket Description:
{{$description}}

Similar Historical Incidents (ordered most-similar first, each has TicketId, Problem, RootCause, Resolution, FeedbackScore, LatestHumanValidatedFix, LatestHumanAvoidanceNote, LatestHelpfulComment, LatestNotHelpfulComment):
{{$similarHistoricalIncidents}}

NYRA Wiki Documents (ordered most-relevant first, each has DocumentId, KbName, Title, Content, SourceUrl, Citation, Score):
{{$nyraDocuments}}

Step-by-step instructions:
1. Read the new ticket's title and description. Identify the specific symptoms and affected items/components.
2. Compare the PROBLEM field of each historical incident against the new ticket's symptoms. Look for matching keywords, items, failure modes, and affected components.
3. Select the 1-2 historical incidents whose PROBLEM most closely matches the new ticket's specific symptoms.
4. Compare the NYRA wiki documents against the new ticket's symptoms and selected historical incidents. Select only documents that directly describe the affected component, workflow, or configuration.
5. Treat LatestHumanValidatedFix as the HIGHEST priority evidence — a lifeguard confirmed they used this exact fix on a real current pool issue. Use it directly in suggestedResolution if it is relevant and specific.
6. Treat LatestHumanAvoidanceNote as the HIGHEST priority negative evidence — a lifeguard confirmed this path was wrong. Do not suggest it.
7. Treat LatestHelpfulComment as high-trust supporting evidence when specific and relevant.
8. Treat LatestNotHelpfulComment as high-trust negative evidence indicating what to avoid.
9. For suggestedRootCause: Derive a SPECIFIC root cause from the best selected historical incident and/or directly relevant NYRA documentation. Do NOT use the generic stored RootCause. Example: instead of 'Solver configuration mismatch causing improper handling of VG items', say 'VG item 8PG3 missing A33 location mapping in VG Group Mapping, preventing correct die sort mapping'.
10. For suggestedResolution: Prioritize LatestHumanValidatedFix if present and relevant, then use the selected incident's Resolution field, helpful comments, and directly relevant NYRA document procedure, adapting minimally to this ticket's specific items/components.
11. In reasoning, state which TicketId(s) and NYRA Citation(s) were selected, why they match, and which source is closest to the query resolution.

Return only valid JSON with this exact structure:
{
  ""suggestedRootCause"": ""A specific root cause derived from the best-matching incident's Problem and Resolution — not a generic summary"",
  ""suggestedResolution"": ""The resolution steps from the best-matching incident, adapted to this ticket"",
  ""confidence"": 0.0,
    ""reasoning"": ""Which TicketId(s) and/or NYRA Citation(s) were selected, why they match this ticket, and which evidence source is closest""
}

Rules:
- Confidence: 0.8+ for close match, 0.5-0.79 for partial, below 0.5 for weak/no match.
- NEVER produce a generic root cause like 'Solver configuration mismatch causing improper handling of...' — be specific about what is missing, misconfigured, or broken.
- Do NOT blend all incidents into a generic summary. Pick the best match and use it.
- If NYRA documents are used, cite them by Citation or Title in reasoning, but do not put raw URLs in suggestedResolution.
- If no historical incident or NYRA document is relevant, use the ticket details alone and set confidence below 0.4.
- Do not include markdown, comments, code fences, or extra fields.
";

        var arguments = new KernelArguments
        {
            { "title", title },
            { "description", description },
            { "similarHistoricalIncidents", incidentsJson },
            { "nyraDocuments", nyraDocumentsJson }
        };

        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(
            _kernel,
            prompt,
            arguments,
            _tokenUsageRepository,
            "ResolutionGeneration",
            _nyraSettings.CurrentValue.Model);
    }
}
