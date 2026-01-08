using System.Text.Json;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Persists game-related settings between application sessions.
/// Settings are stored in the user's AppData folder.
/// </summary>
public class GameSettings
{
    private static readonly string SettingsFile = Path.Combine(AppPaths.SettingsFolder, "game-settings.json");

    /// <summary>
    /// Path to the BeamNG.drive installation directory (e.g., "C:\Steam\steamapps\common\BeamNG.drive")
    /// </summary>
    public string BeamNGInstallDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Loads game settings from disk. Returns null if no settings exist or loading fails.
    /// </summary>
    public static GameSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return null;

            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<GameSettings>(json, BeamJsonOptions.GetJsonSerializerOptions());
        }
        catch
        {
            // If loading fails for any reason, return null to use defaults
            return null;
        }
    }

    /// <summary>
    /// Saves the current game settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(AppPaths.SettingsFolder))
                Directory.CreateDirectory(AppPaths.SettingsFolder);

            var json = JsonSerializer.Serialize(this, BeamJsonOptions.GetJsonSerializerOptions());
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silently fail - game settings are not critical for app operation
        }
    }
}
