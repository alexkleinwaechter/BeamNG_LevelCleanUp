using System.Collections.Concurrent;
using System.Diagnostics;

namespace BeamNgTerrainPoc.Terrain.Logging;

/// <summary>
///     Central logger for terrain creation operations.
///     Handles Info, Warning, Error, and Timing logs with file output.
///     Timing logs go to file only; other logs are forwarded to TerrainLogger for UI display.
///     Thread-safe and designed for real-time performance analysis.
/// </summary>
public sealed class TerrainCreationLogger : IDisposable
{
    private readonly StreamWriter _errorWriter;
    private readonly object _fileLock = new();
    private readonly StreamWriter _infoWriter;
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, Stopwatch> _operationStopwatches;
    private readonly string _sessionId;
    private readonly Stopwatch _sessionStopwatch;
    private readonly StreamWriter _timingWriter;
    private readonly StreamWriter _warningWriter;
    private bool _disposed;

    /// <summary>
    ///     Creates a new terrain creation logger that writes to the specified directory.
    /// </summary>
    /// <param name="logDirectory">Directory to write log files to</param>
    /// <param name="sessionName">Optional session name for log file prefix</param>
    public TerrainCreationLogger(string logDirectory, string sessionName = "TerrainGen")
    {
        _logDirectory = logDirectory;
        _sessionId = $"{sessionName}_{DateTime.Now:yyyyMMdd_HHmmss}";
        _sessionStopwatch = Stopwatch.StartNew();
        _operationStopwatches = new ConcurrentDictionary<string, Stopwatch>();

        Directory.CreateDirectory(logDirectory);

        // Create log files with immediate flush
        _infoWriter = CreateWriter("Info");
        _warningWriter = CreateWriter("Warnings");
        _errorWriter = CreateWriter("Errors");
        _timingWriter = CreateWriter("Timing");

        // Write header
        WriteHeader();

        // Set as current instance for static access
        Current = this;

        Info("Performance logging started");
    }

    /// <summary>
    ///     Gets the current active logger instance (for static access in algorithms).
    /// </summary>
    public static TerrainCreationLogger? Current { get; private set; }

    /// <summary>
    ///     Writes final summary and closes log files.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var totalTime = _sessionStopwatch.Elapsed;
        var summary = $"""

                       ================================================================================
                       Session Complete
                       Total Duration: {FormatDuration(totalTime)}
                       Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                       ================================================================================
                       """;

        lock (_fileLock)
        {
            _infoWriter.WriteLine(summary);
            _timingWriter.WriteLine(summary);

            _infoWriter.Dispose();
            _warningWriter.Dispose();
            _errorWriter.Dispose();
            _timingWriter.Dispose();
        }

        if (Current == this)
            Current = null;
    }

    private StreamWriter CreateWriter(string suffix)
    {
        var path = Path.Combine(_logDirectory, $"Log_{_sessionId}_{suffix}.txt");
        var writer = new StreamWriter(path, false) { AutoFlush = true };
        return writer;
    }

    private void WriteHeader()
    {
        var header = $"""
                      ================================================================================
                      Terrain Generation Performance Log
                      Session: {_sessionId}
                      Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
                      Machine: {Environment.MachineName}
                      Processors: {Environment.ProcessorCount}
                      ================================================================================

                      """;

        lock (_fileLock)
        {
            _infoWriter.Write(header);
            _timingWriter.Write(header);
        }
    }

    /// <summary>
    ///     Logs an informational message with timestamp.
    ///     Writes to file AND forwards to TerrainLogger for UI display.
    /// </summary>
    public void Info(string message)
    {
        var line = FormatLogLine("INFO", message);
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
        }

        // Also forward to TerrainLogger for UI display
        TerrainLogger.Info(message);
    }

    /// <summary>
    ///     Logs an informational message to file only (not forwarded to UI).
    ///     Use this for messages that should be logged but not shown to users.
    /// </summary>
    public void InfoFileOnly(string message)
    {
        var line = FormatLogLine("INFO", message);
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
        }
        // NOT forwarded to TerrainLogger - file only
    }

    /// <summary>
    ///     Logs a timing/performance message to file only (not forwarded to UI).
    ///     Use this for detailed timing data that would clutter the UI.
    /// </summary>
    public void Timing(string message)
    {
        var line = FormatLogLine("TIMING", message);
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _timingWriter.WriteLine(line);
        }
        // NOT forwarded to TerrainLogger - file only
    }

    /// <summary>
    ///     Logs a warning message with timestamp.
    /// </summary>
    public void Warning(string message)
    {
        var line = FormatLogLine("WARN", message);
        lock (_fileLock)
        {
            _warningWriter.WriteLine(line);
        }

        TerrainLogger.Warning(message);
    }

    /// <summary>
    ///     Logs an error message with timestamp.
    /// </summary>
    public void Error(string message)
    {
        var line = FormatLogLine("ERROR", message);
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _errorWriter.WriteLine(line);
        }

        TerrainLogger.Error(message);
    }

    /// <summary>
    ///     Starts timing an operation. Returns the operation name for use with StopTiming.
    /// </summary>
    public string StartTiming(string operationName)
    {
        var sw = new Stopwatch();
        sw.Start();
        _operationStopwatches[operationName] = sw;

        var line = FormatLogLine("START", $"[{operationName}] Starting...");
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _timingWriter.WriteLine(line);
        }

        return operationName;
    }

    /// <summary>
    ///     Stops timing an operation and logs the elapsed time.
    /// </summary>
    public TimeSpan StopTiming(string operationName, string? additionalInfo = null)
    {
        if (!_operationStopwatches.TryRemove(operationName, out var sw))
        {
            Warning($"StopTiming called for unknown operation: {operationName}");
            return TimeSpan.Zero;
        }

        sw.Stop();
        var elapsed = sw.Elapsed;

        var info = string.IsNullOrEmpty(additionalInfo) ? "" : $" - {additionalInfo}";
        var line = FormatLogLine("DONE", $"[{operationName}] Completed in {FormatDuration(elapsed)}{info}");

        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _timingWriter.WriteLine(line);
        }

        // Also log to UI if operation took more than 1 second
        if (elapsed.TotalSeconds >= 1)
            TerrainLogger.Info($"[{operationName}] Completed in {FormatDuration(elapsed)}{info}");

        return elapsed;
    }

    /// <summary>
    ///     Logs a timing checkpoint within an operation (doesn't stop the timer).
    /// </summary>
    public void LogCheckpoint(string operationName, string checkpointMessage)
    {
        if (_operationStopwatches.TryGetValue(operationName, out var sw))
        {
            var elapsed = sw.Elapsed;
            var line = FormatLogLine("CHECKPOINT",
                $"[{operationName}] @ {FormatDuration(elapsed)}: {checkpointMessage}");
            lock (_fileLock)
            {
                _infoWriter.WriteLine(line);
                _timingWriter.WriteLine(line);
            }
        }
        else
        {
            Info($"[{operationName}] {checkpointMessage}");
        }
    }

    /// <summary>
    ///     Creates a scoped timing operation that automatically logs when disposed.
    ///     Usage: using (logger.TimeOperation("MyOperation")) { ... }
    /// </summary>
    public TimingScope TimeOperation(string operationName)
    {
        return new TimingScope(this, operationName);
    }

    /// <summary>
    ///     Logs a debug/detail message to file only (not forwarded to UI).
    ///     Use this for verbose diagnostic information that would clutter the UI.
    /// </summary>
    public void Detail(string message)
    {
        var line = FormatLogLine("DETAIL", message);
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
        }
        // NOT forwarded to TerrainLogger - file only
    }

    /// <summary>
    ///     Logs memory usage statistics.
    /// </summary>
    public void LogMemoryUsage(string context)
    {
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64 / (1024.0 * 1024.0);
        var privateBytes = process.PrivateMemorySize64 / (1024.0 * 1024.0);
        var gcMemory = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        var line = FormatLogLine("MEMORY",
            $"{context}: Working={workingSet:F1}MB, Private={privateBytes:F1}MB, GC={gcMemory:F1}MB");
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _timingWriter.WriteLine(line);
        }
    }

    /// <summary>
    ///     Writes a separator line for visual grouping in logs.
    /// </summary>
    public void LogSection(string sectionName)
    {
        var separator = new string('=', 40);
        var line = $"\n{separator}\n  {sectionName}\n{separator}\n";
        lock (_fileLock)
        {
            _infoWriter.WriteLine(line);
            _timingWriter.WriteLine(line);
        }
    }

    private string FormatLogLine(string level, string message)
    {
        var sessionTime = _sessionStopwatch.Elapsed;
        return $"[{DateTime.Now:HH:mm:ss.fff}] [{sessionTime.TotalSeconds:F3}s] [{level,-6}] {message}";
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.TotalHours:F2}h";
        if (ts.TotalMinutes >= 1)
            return $"{ts.TotalMinutes:F2}min";
        if (ts.TotalSeconds >= 1)
            return $"{ts.TotalSeconds:F2}s";
        return $"{ts.TotalMilliseconds:F1}ms";
    }

    /// <summary>
    ///     Flushes all log writers to disk.
    /// </summary>
    public void Flush()
    {
        lock (_fileLock)
        {
            _infoWriter.Flush();
            _warningWriter.Flush();
            _errorWriter.Flush();
            _timingWriter.Flush();
        }
    }

    /// <summary>
    ///     Disposable scope for automatic timing of operations.
    /// </summary>
    public readonly struct TimingScope : IDisposable
    {
        private readonly TerrainCreationLogger _logger;
        private readonly string _operationName;

        internal TimingScope(TerrainCreationLogger logger, string operationName)
        {
            _logger = logger;
            _operationName = operationName;
            _logger.StartTiming(operationName);
        }

        public void Dispose()
        {
            _logger.StopTiming(_operationName);
        }
    }
}