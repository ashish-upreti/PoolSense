using System.Text;
using PoolSense.Api.Models;

namespace PoolSense.Api.Services;

/// <summary>
/// Builds a readable historical incident context block from similar tickets.
/// </summary>
public class IncidentContextBuilder
{
    /// <summary>
    /// Builds a formatted context string from similar incidents.
    /// </summary>
    /// <param name="incidents">The similar incidents to include in the context.</param>
    /// <returns>A formatted incident context string.</returns>
    public string Build(List<TicketKnowledge> incidents)
    {
        if (incidents == null || incidents.Count == 0)
        {
            return "No similar historical incidents were found.";
        }

        var builder = new StringBuilder();

        for (var i = 0; i < incidents.Count; i++)
        {
            var incident = incidents[i];

            builder.AppendLine($"Incident {i + 1}");
            builder.AppendLine($"Problem: {incident.Problem}");
            builder.AppendLine($"Root Cause: {incident.RootCause}");
            builder.AppendLine($"Resolution: {incident.Resolution}");

            if (i < incidents.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }
}
