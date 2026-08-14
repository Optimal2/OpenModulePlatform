// File: OpenModulePlatform.Web.Shared/Telemetry/OmpPerformanceTelemetry.cs
using System.Collections.Concurrent;

namespace OpenModulePlatform.Web.Shared.Telemetry;

/// <summary>One aggregated metric bucket, ready to be written.</summary>
public sealed record OmpPerformanceSample(
    string AppKey,
    string Scope,
    string MetricKey,
    DateTime BucketUtc,
    long Count,
    double Total,
    double Min,
    double Max,
    DateTime FirstUtc,
    DateTime LastUtc);

/// <summary>
/// Collects application timings in memory and hands them over in aggregated form.
/// </summary>
/// <remarks>
/// <para>
/// The point of this class is that measuring must cost far less than the thing being
/// measured. Recording a value takes a dictionary lookup and a lock on one small object;
/// nothing touches the database on the request path. A background service drains the
/// accumulated buckets on an interval and writes them in one batch.
/// </para>
/// <para>
/// The key space is bounded on purpose. Scope labels come from a small fixed set chosen by
/// the caller -- never a raw URL -- and once <see cref="MaxTrackedKeys"/> distinct keys
/// exist, further new keys are dropped rather than allowed to grow the map. An
/// unbounded metric key space is the standard way telemetry turns into the outage it was
/// installed to prevent.
/// </para>
/// <para>
/// Nothing here throws. A telemetry failure must never surface as a failed page, so the
/// recording path has no failure mode that can reach the caller.
/// </para>
/// </remarks>
public sealed class OmpPerformanceTelemetry
{
    /// <summary>Ceiling on distinct (app, scope, metric, bucket) combinations held at once.</summary>
    public const int MaxTrackedKeys = 4000;

    private sealed class Bucket
    {
        private readonly object _sync = new();

        public long Count;
        public double Total;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public DateTime FirstUtc = DateTime.MaxValue;
        public DateTime LastUtc = DateTime.MinValue;

        public void Add(double value, DateTime observedUtc)
        {
            lock (_sync)
            {
                Count++;
                Total += value;
                if (value < Min) Min = value;
                if (value > Max) Max = value;
                if (observedUtc < FirstUtc) FirstUtc = observedUtc;
                if (observedUtc > LastUtc) LastUtc = observedUtc;
            }
        }

        public (long Count, double Total, double Min, double Max, DateTime First, DateTime Last) Read()
        {
            lock (_sync)
            {
                return (Count, Total, Min, Max, FirstUtc, LastUtc);
            }
        }
    }

    private readonly record struct Key(string AppKey, string Scope, string MetricKey, DateTime BucketUtc);

    private readonly ConcurrentDictionary<Key, Bucket> _buckets = new();
    private long _droppedKeys;

    /// <summary>
    /// How many distinct keys were discarded because the ceiling was reached. Exposed so the
    /// flush service can log it -- silently dropping measurements would make the data lie.
    /// </summary>
    public long DroppedKeys => Interlocked.Read(ref _droppedKeys);

    /// <summary>
    /// Records one observation. Never throws.
    /// </summary>
    /// <param name="appKey">Application that produced it.</param>
    /// <param name="scope">Coarse label. Must not be a raw path -- see the class remarks.</param>
    /// <param name="metricKey">What was measured, e.g. <c>page.duration.ms</c>.</param>
    /// <param name="value">The measured value.</param>
    public void Record(string? appKey, string? scope, string? metricKey, double value)
    {
        if (string.IsNullOrWhiteSpace(metricKey) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var key = new Key(
            Truncate(appKey, 100, "unknown"),
            Truncate(scope, 150, "-"),
            Truncate(metricKey, 100, "unknown"),
            new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc));

        if (_buckets.TryGetValue(key, out var existing))
        {
            existing.Add(value, nowUtc);
            return;
        }

        // Checked only on the miss path, so the common case stays a single lookup. The
        // ceiling can be exceeded slightly under concurrency; that is deliberate, because
        // a lock here would put contention on every first-of-hour observation.
        if (_buckets.Count >= MaxTrackedKeys)
        {
            Interlocked.Increment(ref _droppedKeys);
            return;
        }

        _buckets.GetOrAdd(key, static _ => new Bucket()).Add(value, nowUtc);
    }

    /// <summary>
    /// Removes and returns everything accumulated so far.
    /// </summary>
    /// <remarks>
    /// Buckets are removed before being read, so an observation arriving during the drain
    /// lands in a fresh bucket rather than in one already on its way to the database. The
    /// alternative -- read then clear -- loses whatever arrived in between.
    /// </remarks>
    public IReadOnlyList<OmpPerformanceSample> Drain()
    {
        var drained = new List<OmpPerformanceSample>();

        foreach (var key in _buckets.Keys.ToArray())
        {
            if (!_buckets.TryRemove(key, out var bucket))
            {
                continue;
            }

            var (count, total, min, max, first, last) = bucket.Read();
            if (count == 0)
            {
                continue;
            }

            drained.Add(new OmpPerformanceSample(
                key.AppKey,
                key.Scope,
                key.MetricKey,
                key.BucketUtc,
                count,
                total,
                min,
                max,
                first,
                last));
        }

        return drained;
    }

    private static string Truncate(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
