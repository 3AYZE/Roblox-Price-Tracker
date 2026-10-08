using System.Diagnostics;
using System.Text.Json;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Local, bounded, stage-level timing records. Does not alter scan scheduling.
/// Recording is best effort; a logging problem cannot fail a market scan.
/// </summary>
public sealed class ScanDiagnostics
{
    private const long MaxLogBytes = 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public ScanDiagnostics(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        _path = Path.Combine(dataDirectory, "logs", "scan-metrics.jsonl");
    }

    public string LogPath => _path;

    public async Task<T> MeasureAsync<T>(
        string stage, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception)
        {
            outcome = "failed";
            throw;
        }
        finally
        {
            Record(stage, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    public void Record(string stage, string outcome, TimeSpan duration)
    {
        // Stage/outcome are controlled labels, never Roblox item names or URLs.
        if (string.IsNullOrEmpty(stage) || stage.Length > 80 ||
            stage.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid scan stage label.", nameof(stage));
        if (outcome is not ("success" or "failed" or "cancelled"))
            throw new ArgumentException("Invalid scan outcome.", nameof(outcome));

        var data = new ScanMeasurement(
            DateTimeOffset.UtcNow,
            stage,
            outcome,
            Math.Max(0, (long)duration.TotalMilliseconds));
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaxLogBytes)
                {
                    var archive = _path + ".1";
                    if (File.Exists(archive)) File.Delete(archive);
                    File.Move(_path, archive);
                }
                File.AppendAllText(_path, JsonSerializer.Serialize(data) + Environment.NewLine);
            }
            catch { /* Measuring an operation must not change its outcome. */ }
        }
    }

    public sealed record ScanMeasurement(
        DateTimeOffset TimestampUtc, string Stage, string Outcome, long DurationMs);
}
