using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Centralized application path management.
/// All temporary extraction folders are under AppData\Local\BeamNG_LevelCleanUp\temp
/// </summary>
public static class AppPaths
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNG_LevelCleanUp");

    /// <summary>
    /// Base folder for temporary extraction operations.
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp
    /// </summary>
    public static string TempFolder => Path.Combine(AppDataFolder, "temp");

    /// <summary>
    /// Folder for extracted target level (unpacking ZIPs).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp\_unpacked
    /// </summary>
    public static string UnpackedFolder => Path.Combine(TempFolder, "_unpacked");

    /// <summary>
    /// Folder for extracted source level (copy operations).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp\_copyFrom
    /// </summary>
    public static string CopyFromFolder => Path.Combine(TempFolder, "_copyFrom");

    /// <summary>
    /// Folder for application logs.
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\logs
    /// </summary>
    public static string LogsFolder => Path.Combine(AppDataFolder, "logs");

    /// <summary>
    /// Settings folder (already used by WindowSettings).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp
    /// </summary>
    public static string SettingsFolder => AppDataFolder;

    /// <summary>
    /// Folder for the OSM2World default style package (CC0 building textures).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\OSM2World-default-style
    /// </summary>
    public static string Osm2WorldStyleFolder => Path.Combine(AppDataFolder, "OSM2World-default-style");

    /// <summary>
    /// Path to the main OSM2World style configuration JSON file.
    /// Generated from standard.properties after the style package is downloaded.
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\osm2world-style.json
    /// </summary>
    public static string StyleConfigFile => Path.Combine(AppDataFolder, "osm2world-style.json");

    /// <summary>
    /// Folder for locale-specific style configuration files (traffic signs, locale defaults).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\osm2world-style-locales\
    /// </summary>
    public static string StyleLocalesFolder => Path.Combine(AppDataFolder, "osm2world-style-locales");

    /// <summary>
    /// Ensures all required directories exist.
    /// Call this at application startup.
    /// </summary>
    /// <param name="cleanupOnStartup">If true, cleans up stale temp folders from previous sessions. 
    /// Should be true only on application startup to prevent data loss during normal operation.</param>
    public static void Initialize(bool cleanupOnStartup = false)
    {
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(LogsFolder);
        
        // On application startup, clean up any stale temp folders from previous sessions
        // This prevents accumulation of temp files and ensures a fresh state
        if (cleanupOnStartup)
        {
            CleanupTempFoldersQuietly();
        }
    }

    /// <summary>
    /// Sets the working directory to the centralized temp folder.
    /// Call this on page initialization for all pages EXCEPT GenerateTerrain.
    /// Note: This does NOT reset ZipFileHandler static paths or BeamFileReader state,
    /// preserving "previously loaded level" detection functionality.
    /// </summary>
    public static void EnsureWorkingDirectory()
    {
        ZipFileHandler.WorkingDirectory = TempFolder;
        Initialize(); // Creates all directories (without cleanup)
    }

    /// <summary>
    /// Cleans up all temporary extraction folders.
    /// Safe to call - creates fresh empty directories.
    /// Also resets ZipFileHandler static path references to prevent stale references.
    /// </summary>
    public static void CleanupTempFolders()
    {
        CleanupFolder(UnpackedFolder, resetUnpackedPaths: true);
        CleanupFolder(CopyFromFolder, resetCopyFromPaths: true);
    }

    /// <summary>
    /// Cleans up only the _unpacked folder.
    /// Also resets ZipFileHandler's unpacked path references.
    /// </summary>
    public static void CleanupUnpackedFolder()
    {
        CleanupFolder(UnpackedFolder, resetUnpackedPaths: true);
    }

    /// <summary>
    /// Cleans up only the _copyFrom folder.
    /// Also resets ZipFileHandler's copyFrom path references.
    /// </summary>
    public static void CleanupCopyFromFolder()
    {
        CleanupFolder(CopyFromFolder, resetCopyFromPaths: true);
    }

    /// <summary>
    /// Completely removes the temp folder and all its contents on startup.
    /// Used during application startup to ensure a clean state.
    /// </summary>
    private static void CleanupTempFoldersQuietly()
    {
        try
        {
            // Delete the entire temp folder and all contents
            if (Directory.Exists(TempFolder))
                Directory.Delete(TempFolder, true);
        }
        catch
        {
            // Silently ignore - folder might be in use or already deleted
        }
        
        // Recreate the temp folder structure
        Directory.CreateDirectory(TempFolder);
        
        // Reset ZipFileHandler static paths since we're starting fresh
        // Note: BeamFileReader static state is NOT reset here - that would break
        // "previously loaded level" detection, but since we just deleted the folders,
        // those paths are now invalid anyway and will be repopulated on next extraction
        ZipFileHandler.ResetStaticPaths();
    }

    private static void CleanupFolder(string folderPath, bool resetUnpackedPaths = false, bool resetCopyFromPaths = false)
    {
        try
        {
            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, true);
            Directory.CreateDirectory(folderPath);
            
            // Reset the corresponding ZipFileHandler static paths to prevent stale references
            if (resetUnpackedPaths)
            {
                ZipFileHandler.ResetUnpackedPaths();
            }
            if (resetCopyFromPaths)
            {
                ZipFileHandler.ResetCopyFromPaths();
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - cleanup failures shouldn't block operations
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not clean up {Path.GetFileName(folderPath)}: {ex.Message}");
        }
    }
}
