using System.Text.Json;
using Microsoft.SemanticKernel;

namespace PoolSense.Api.Agents;

public interface IResolutionAgent
{
    Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarHistoricalIncidents);
}

public sealed class ResolutionIncident
{
    public string TicketId { get; set; } = string.Empty;
    public string Problem { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
}

public class ResolutionAgent : IResolutionAgent
{
    private readonly Kernel _kernel;

    public ResolutionAgent(Kernel kernel)
    {
        _kernel = kernel;
    }

    public Task<string> GenerateResolutionAsync(string title, string description, IReadOnlyList<ResolutionIncident> similarHistoricalIncidents)
    {
        var incidentsJson = JsonSerializer.Serialize(similarHistoricalIncidents);

        const string prompt = @"
You are an AI system that suggests resolutions for engineering support tickets.

You will be given a new ticket and a ranked list of similar historical incidents (most similar first).

IMPORTANT: The 'RootCause' field in historical incidents is an AI-generated summary and is often generic or inaccurate (e.g. 'Solver configuration mismatch causing...' repeated across unrelated tickets). Do NOT trust or copy it blindly.
The 'Problem' field is the most reliable and specific description of what actually happened in each historical incident.
The 'Resolution' field describes the steps that were taken to fix the issue.

New Ticket Title:
{{$title}}

New Ticket Description:
{{$description}}

Similar Historical Incidents (ordered most-similar first, each has a TicketId, Problem, RootCause, Resolution):
{{$similarHistoricalIncidents}}

Step-by-step instructions:
1. Read the new ticket's title and description. Identify the specific symptoms and affected items/components.
2. Compare the PROBLEM field of each historical incident against the new ticket's symptoms. Look for matching keywords, items, failure modes, and affected components.
3. Select the 1-2 historical incidents whose PROBLEM most closely matches the new ticket's specific symptoms.
4. For suggestedRootCause: Derive a SPECIFIC root cause from the selected incident's Problem and Resolution fields. Do NOT use the generic stored RootCause. Example: instead of 'Solver configuration mismatch causing improper handling of VG items', say 'VG item 8PG3 missing A33 location mapping in VG Group Mapping, preventing correct die sort mapping'.
5. For suggestedResolution: Use the selected incident's Resolution field, adapting it minimally to fit the new ticket's specific items/components.
6. In reasoning, state which TicketId(s) you selected and why their Problem description specifically matches.

Return only valid JSON with this exact structure:
{
  ""suggestedRootCause"": ""A specific root cause derived from the best-matching incident's Problem and Resolution — not a generic summary"",
  ""suggestedResolution"": ""The resolution steps from the best-matching incident, adapted to this ticket"",
  ""confidence"": 0.0,
  ""reasoning"": ""Which TicketId(s) were selected and why their Problem symptoms match this ticket""
}

Rules:
- Confidence: 0.8+ for close match, 0.5-0.79 for partial, below 0.5 for weak/no match.
- NEVER produce a generic root cause like 'Solver configuration mismatch causing improper handling of...' — be specific about what is missing, misconfigured, or broken.
- Do NOT blend all incidents into a generic summary. Pick the best match and use it.
- If no historical incident is relevant, use the ticket details alone and set confidence below 0.4.
- Do not include markdown, comments, code fences, or extra fields.
";

        var arguments = new KernelArguments
        {
            { "title", title },
            { "description", description },
            { "similarHistoricalIncidents", incidentsJson }
        };

        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(_kernel, prompt, arguments);
    }
}
