using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Centralized service for managing BeamNG.drive game directory.
/// Handles persistence, validation, and fallback to Steam detection.
/// </summary>
public static class GameDirectoryService
{
    private static string _cachedInstallDir = string.Empty;
    private static bool _isInitialized = false;
    private static bool _needsConfiguration = false;

    /// <summary>
    /// Gets the BeamNG install directory. Returns cached value if available.
    /// </summary>
    public static string GetInstallDirectory()
    {
        if (!_isInitialized)
        {
            // Synchronous initialization if not already done
            Initialize();
        }
        return _cachedInstallDir;
    }

    /// <summary>
    /// Initializes the game directory on application startup.
    /// Returns true if a valid directory was found/configured.
    /// </summary>
    public static bool Initialize()
    {
        if (_isInitialized)
            return !_needsConfiguration;

        _isInitialized = true;

        // Step 1: Try to load from saved settings
        var settings = GameSettings.Load();
        if (settings != null && !string.IsNullOrEmpty(settings.BeamNGInstallDirectory))
        {
            if (IsValidBeamNGDirectory(settings.BeamNGInstallDirectory))
            {
                _cachedInstallDir = settings.BeamNGInstallDirectory;
                Steam.BeamInstallDir = _cachedInstallDir;
                _needsConfiguration = false;
                return true;
            }
        }

        // Step 2: Try Steam auto-detection
        var detectedDir = DetectBeamInstallDir();
        if (!string.IsNullOrEmpty(detectedDir) && IsValidBeamNGDirectory(detectedDir))
        {
            _cachedInstallDir = detectedDir;
            Steam.BeamInstallDir = _cachedInstallDir;

            // Save the detected directory for future use
            var newSettings = settings ?? new GameSettings();
            newSettings.BeamNGInstallDirectory = detectedDir;
            newSettings.Save();

            _needsConfiguration = false;
            return true;
        }

        // Step 3: No valid directory found - needs user configuration
        _needsConfiguration = true;
        return false;
    }

    /// <summary>
    /// Async wrapper for initialization (for use in async contexts).
    /// </summary>
    public static Task<bool> InitializeAsync()
    {
        return Task.FromResult(Initialize());
    }

    /// <summary>
    /// Checks if the BeamNG directory needs to be configured by the user.
    /// </summary>
    public static bool NeedsConfiguration()
    {
        if (!_isInitialized)
        {
            Initialize();
        }
        return _needsConfiguration;
    }

    /// <summary>
    /// Sets the install directory and saves to settings.
    /// </summary>
    public static void SetInstallDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        _cachedInstallDir = path;
        Steam.BeamInstallDir = path;
        _needsConfiguration = false;

        // Persist to settings
        var settings = GameSettings.Load() ?? new GameSettings();
        settings.BeamNGInstallDirectory = path;
        settings.Save();
    }

    /// <summary>
    /// Validates that a given path is a valid BeamNG.drive installation.
    /// A valid installation must have a content/levels directory structure.
    /// </summary>
    public static bool IsValidBeamNGDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        // Check for BeamNG.drive content structure
        var contentPath = Path.Join(path, "content");
        var levelsPath = Path.Join(contentPath, "levels");

        return Directory.Exists(contentPath) && Directory.Exists(levelsPath);
    }

    /// <summary>
    /// Forces fresh detection of BeamNG install directory via Steam (clears cache first).
    /// </summary>
    private static string DetectBeamInstallDir()
    {
        // Clear Steam's cached value to force fresh detection
        Steam.BeamInstallDir = string.Empty;
        return Steam.GetBeamInstallDir();
    }

    /// <summary>
    /// Resets the service state. Useful for testing or forcing re-initialization.
    /// </summary>
    public static void Reset()
    {
        _cachedInstallDir = string.Empty;
        _isInitialized = false;
        _needsConfiguration = false;
        Steam.BeamInstallDir = string.Empty;
    }
}
