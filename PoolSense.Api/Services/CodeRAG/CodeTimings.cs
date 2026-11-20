using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PoolSense.Api.Services.CodeRAG;

/// <summary>Stage work durations can overlap; total is elapsed request time, not their sum.</summary>
public sealed class CodeTimings(string operation)
{
    private static readonly Meter Meter = new("PoolSense.CodeRAG");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("coderag.stage.duration", "ms");
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly ConcurrentDictionary<string, double> stages = new(StringComparer.Ordinal);

    public IDisposable Measure(string stage) => new Measurement(this, stage);
    public double ElapsedMilliseconds => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(stages, StringComparer.Ordinal)
    {
        ["total"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
    };

    private sealed class Measurement(CodeTimings owner, string stage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            owner.stages.AddOrUpdate(stage, duration, (_, previous) => previous + duration);
            Duration.Record(duration, new KeyValuePair<string, object?>("operation", owner.Operation),
                new KeyValuePair<string, object?>("stage", stage));
        }
    }

    private string Operation => operation;
}
