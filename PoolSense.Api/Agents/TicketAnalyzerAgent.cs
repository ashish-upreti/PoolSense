using Microsoft.SemanticKernel;

namespace PoolSense.Api.Agents;

public interface ITicketAnalyzerAgent
{
    Task<string> AnalyzeTicketAsync(string title, string description);
}

public class TicketAnalyzerAgent : ITicketAnalyzerAgent
{
    private readonly Kernel _kernel;

    public TicketAnalyzerAgent(Kernel kernel)
    {
        _kernel = kernel;
    }

    public Task<string> AnalyzeTicketAsync(string title, string description)
    {
        const string prompt = @"
        Analyze the following support ticket and extract structured knowledge that can later be embedded and stored in a vector database.
        
        Ticket Title: {{$title}}
        Ticket Description: {{$description}}

        Return only valid JSON with this exact structure:
        {
          ""problem"": ""A concise description of the reported issue"",
          ""rootCause"": ""The most likely root cause based on the ticket details"",
          ""resolution"": ""The recommended or applied fix for the issue"",
          ""keywords"": [""keyword1"", ""keyword2"", ""keyword3""]
        }

        Do not include markdown, explanations, or additional fields.
        ";

        var arguments = new KernelArguments
        {
            { "title", title },
            { "description", description }
        };

        return SemanticKernelRetryHelper.InvokePromptWithDeploymentRetryAsync(_kernel, prompt, arguments);
    }
}