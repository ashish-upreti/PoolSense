using System.Diagnostics;
using System.Text;

namespace CodeRAG.Core.Indexing;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {executable}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var output = DrainAsync(process.StandardOutput, deadline.Token);
            var error = DrainAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var streams = await Task.WhenAll(output, error);
            if (streams.Any(s => s.Truncated)) throw new IOException("Command output exceeded the 1 MiB limit.");
            return new(process.ExitCode, streams[0].Text, streams[1].Text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{executable} timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        var truncated = false;
        int read;
        while ((read = await stream.BaseStream.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var take = Math.Min(read, Math.Max(0, 1_048_576 - (int)data.Length));
            data.Write(buffer, 0, take);
            truncated |= take != read;
        }
        return (Encoding.UTF8.GetString(data.ToArray()), truncated);
    }
}
