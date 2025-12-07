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
            var prefix = level switch
            {
                TerrainLogLevel.Warning => "WARNING: ",
                TerrainLogLevel.Error => "ERROR: ",
                _ => ""
            };
            Console.WriteLine($"{prefix}{message}");
        }
    }
}
