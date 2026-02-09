namespace BeamNgTerrainPoc.Terrain.Logging;

/// <summary>
/// Log level for terrain creation messages.
/// </summary>
public enum TerrainLogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Delegate for handling terrain log messages.
/// </summary>
/// <param name="level">The log level</param>
/// <param name="message">The log message</param>
public delegate void TerrainLogHandler(TerrainLogLevel level, string message);

/// <summary>
/// Static logger for terrain creation operations.
/// Allows external applications to subscribe to log messages.
/// Falls back to Console.WriteLine if no handler is configured.
/// </summary>
public static class TerrainLogger
{
    private static TerrainLogHandler? _logHandler;
    private static readonly object _lock = new();
    
    /// <summary>
    /// When true, suppresses detailed per-item logging for bulk operations.
    /// Only summary and progress messages will be sent to the UI handler.
    /// Detailed messages will still be written to Console for log files.
    /// </summary>
    public static bool SuppressDetailedLogging { get; set; }

    /// <summary>
    /// Sets the log handler for terrain operations.
    /// Pass null to reset to default Console.WriteLine behavior.
    /// </summary>
    /// <param name="handler">The handler to receive log messages</param>
    public static void SetLogHandler(TerrainLogHandler? handler)
    {
        lock (_lock)
        {
            _logHandler = handler;
        }
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string message)
    {
        Log(TerrainLogLevel.Info, message);
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public static void Warning(string message)
    {
        Log(TerrainLogLevel.Warning, message);
    }

    /// <summary>
    /// Logs an error message.
    /// </summary>
    public static void Error(string message)
    {
        Log(TerrainLogLevel.Error, message);
    }

    /// <summary>
    /// Logs a detailed message that will be suppressed when SuppressDetailedLogging is true.
    /// Use this for per-item messages in bulk operations (e.g., per-tile in GeoTIFF combining).
    /// </summary>
    public static void Detail(string message)
    {
        LogDetail(TerrainLogLevel.Info, message);
    }

    /// <summary>
    /// Logs a detailed warning that will be suppressed when SuppressDetailedLogging is true.
    /// </summary>
    public static void DetailWarning(string message)
    {
        LogDetail(TerrainLogLevel.Warning, message);
    }

    /// <summary>
    /// Logs a message with the specified level.
    /// </summary>
    public static void Log(TerrainLogLevel level, string message)
    {
        TerrainLogHandler? handler;
        lock (_lock)
        {
            handler = _logHandler;
        }

        if (handler != null)
        {
            try
            {
                handler(level, message);
            }
            catch
            {
                // Don't let handler exceptions break the terrain creation
                Console.WriteLine(message);
            }
        }
        else
        {
            // Default to console output
            WriteToConsole(level, message);
        }
    }

    /// <summary>
    /// Logs a detailed message that may be suppressed from UI but always goes to console.
    /// </summary>
    private static void LogDetail(TerrainLogLevel level, string message)
    {
        // Always write to console for log files
        WriteToConsole(level, message);

        // Only send to UI handler if detailed logging is not suppressed
        if (SuppressDetailedLogging)
            return;

        TerrainLogHandler? handler;
        lock (_lock)
        {
            handler = _logHandler;
        }

        if (handler != null)
        {
            try
            {
                handler(level, message);
            }
            catch
            {
                // Ignore handler exceptions
            }
        }
    }

    /// <summary>
    /// Writes a message to the console with appropriate prefix.
    /// </summary>
    private static void WriteToConsole(TerrainLogLevel level, string message)
    {
        var prefix = level switch
        {
            TerrainLogLevel.Warning => "WARNING: ",
            TerrainLogLevel.Error => "ERROR: ",
            _ => ""
        };
        Console.WriteLine($"{prefix}{message}");
    }
}
