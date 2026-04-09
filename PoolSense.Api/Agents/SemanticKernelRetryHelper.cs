using Microsoft.SemanticKernel;

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
}
