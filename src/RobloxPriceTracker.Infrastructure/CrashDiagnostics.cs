using System.Text;
using System.Text.RegularExpressions;

namespace RobloxPriceTracker.Infrastructure;

/// <summary>
/// Best-effort local-only crash records. A stale session marker also flags
/// process termination that bypasses managed exception handlers.
/// Never uploads files, alters saved tracker state, or suppresses fatal errors.
/// </summary>
public sealed class CrashDiagnostics
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly Regex SecretPattern = new(
        @"(?i)(\.ROBLOSECURITY|authorization|bearer|x-api-key)(\s*[:=]\s*|\s+)[^\s;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly string _markerPath;
    private bool _sessionStarted;

    public CrashDiagnostics(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        var logDirectory = Path.Combine(dataDirectory, "logs");
        _logPath = Path.Combine(logDirectory, "crash.log");
        _markerPath = Path.Combine(logDirectory, "session.active");
    }

    public string LogPath => _logPath;

    public static string ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("RPT_DATA_DIR");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobloxPriceTracker");
    }

    /// <returns>True when an earlier run did not exit through the normal shutdown path.</returns>
    public bool BeginSession()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                var unclean = File.Exists(_markerPath);
                if (unclean)
                    AppendLocked($"{DateTimeOffset.UtcNow:O} [UNCLEAN_EXIT] Previous session did not close normally. " +
                                 "An OS termination or power loss may not produce a managed exception.");
                File.WriteAllText(_markerPath, $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}");
                _sessionStarted = true;
                return unclean;
            }
            catch { return false; } // Diagnostics must never prevent application startup.
        }
    }

    public void EndSession()
    {
        lock (_gate)
        {
            if (!_sessionStarted) return;
            try { File.Delete(_markerPath); }
            catch { /* A diagnostic file lock must never block exit. */ }
            _sessionStarted = false;
        }
    }

    public void Record(string source, Exception exception, bool fatal)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var safeSource = Regex.Replace(source.Length > 60 ? source[..60] : source,
            @"[^a-zA-Z0-9_.-]", "_");
        var details = new StringBuilder();
        details.Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append(" [").Append(fatal ? "FATAL" : "UNOBSERVED").Append("] ")
            .Append(safeSource).Append(" | ")
            .Append(exception.GetType().FullName).Append(": ")
            .Append(exception.Message);
        if (!string.IsNullOrEmpty(exception.StackTrace))
            details.AppendLine().Append(exception.StackTrace);
        var redacted = SecretPattern.Replace(details.ToString(), "$1$2[REDACTED]");
        if (redacted.Length > 12000) redacted = redacted[..12000] + " [TRUNCATED]";
        lock (_gate)
        {
            try { AppendLocked(redacted); }
            catch { /* Keep the original exception path, not a logging failure. */ }
        }
    }

    private void AppendLocked(string entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= MaxLogBytes)
        {
            var archive = _logPath + ".1";
            if (File.Exists(archive)) File.Delete(archive);
            File.Move(_logPath, archive);
        }
        File.AppendAllText(_logPath, entry + Environment.NewLine + Environment.NewLine);
    }
}
