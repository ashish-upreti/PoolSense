using Microsoft.SemanticKernel;
using PoolSense.Api.Logging;
using System.Diagnostics;
using System.Text;

namespace PoolSense.Api.Agents;

internal static class SemanticKernelRetryHelper
{
    private const int MaxAttempts = 3;

    public static Task<T> ExecuteWithDeploymentRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteCoreAsync(operation, cancellationToken);
    }

    public static Task<string> InvokePromptWithDeploymentRetryAsync(
        Kernel kernel,
        string prompt,
        KernelArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(arguments);

        return ExecuteWithDeploymentRetryAsync(
            async () =>
            {
                var result = await kernel.InvokePromptAsync(prompt, arguments, cancellationToken: cancellationToken);
                return result.ToString();
            },
            cancellationToken);
    }

    public static async Task<string> InvokePromptWithDeploymentRetryAsync(
        Kernel kernel,
        string prompt,
        KernelArguments arguments,
        ILlmTokenUsageRepository tokenUsageRepository,
        string operationName,
        string model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(tokenUsageRepository);

        var inputText = BuildInputText(prompt, arguments);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await ExecuteWithDeploymentRetryAsync(
                () => kernel.InvokePromptAsync(prompt, arguments, cancellationToken: cancellationToken),
                cancellationToken);
            var outputText = result.ToString();

            await TryLogUsageAsync(tokenUsageRepository, new LlmTokenUsageRecord
            {
                ServiceType = "inference",
                OperationName = operationName,
                Model = model,
                DeploymentName = model,
                InputCharacters = inputText.Length,
                OutputCharacters = outputText.Length,
                LatencyMs = GetElapsedMilliseconds(stopwatch),
                Success = true,
                CorrelationId = Activity.Current?.Id ?? string.Empty
            }, result.Metadata, inputText, outputText, cancellationToken);

            return outputText;
        }
        catch (Exception ex)
        {
            await TryLogUsageAsync(tokenUsageRepository, new LlmTokenUsageRecord
            {
                ServiceType = "inference",
                OperationName = operationName,
                Model = model,
                DeploymentName = model,
                InputCharacters = inputText.Length,
                LatencyMs = GetElapsedMilliseconds(stopwatch),
                Success = false,
                ErrorMessage = ex.Message,
                CorrelationId = Activity.Current?.Id ?? string.Empty
            }, metadata: null, inputText, outputText: string.Empty, cancellationToken);

            throw;
        }
    }

    private static async Task<T> ExecuteCoreAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransientDeploymentNotFound(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransientDeploymentNotFound(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("DeploymentNotFound", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildInputText(string prompt, KernelArguments arguments)
    {
        var builder = new StringBuilder(prompt);
        foreach (var argument in arguments)
        {
            builder.AppendLine();
            builder.Append(argument.Key).Append(": ").Append(argument.Value?.ToString() ?? string.Empty);
        }

        return builder.ToString();
    }

    private static async Task TryLogUsageAsync(
        ILlmTokenUsageRepository tokenUsageRepository,
        LlmTokenUsageRecord record,
        object? metadata,
        string inputText,
        string outputText,
        CancellationToken cancellationToken)
    {
        try
        {
            var tokenUsage = TokenUsageMetadataExtractor.FromMetadata(metadata, inputText, outputText);
            record.PromptTokens = tokenUsage.PromptTokens;
            record.CompletionTokens = tokenUsage.CompletionTokens;
            record.TotalTokens = tokenUsage.TotalTokens;
            record.IsEstimated = tokenUsage.IsEstimated;
            await tokenUsageRepository.LogAsync(record, cancellationToken);
        }
        catch
        {
        }
    }

    private static int GetElapsedMilliseconds(Stopwatch stopwatch)
    {
        return (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
    }
}
