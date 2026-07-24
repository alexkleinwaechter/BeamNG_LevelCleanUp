using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.BlazorUI.Services.OsmAutoAssign;

/// <summary>
///     Loads and persists the <see cref="OsmMaterialAutoAssignConfig" /> rule matrix as JSON in the
///     app settings folder (%LocalAppData%\BeamNG_LevelCleanUp\OsmMaterialAutoAssign.json).
///     The file is created from built-in defaults on first use so users can edit it.
/// </summary>
public static class OsmMaterialAutoAssignConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    ///     Full path of the user-editable assignment config file.
    /// </summary>
    public static string ConfigFilePath => Path.Combine(AppPaths.SettingsFolder, "OsmMaterialAutoAssign.json");

    /// <summary>
    ///     Loads the config from disk. On first use the default config is written to disk so the
    ///     user has a template to edit. A malformed file falls back to the built-in defaults
    ///     WITHOUT overwriting the user's file.
    /// </summary>
    public static OsmMaterialAutoAssignConfig LoadOrCreateDefault()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                var defaultConfig = OsmMaterialAutoAssignConfig.CreateDefault();
                Save(defaultConfig);
                return defaultConfig;
            }

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<OsmMaterialAutoAssignConfig>(json, JsonOptions);

            if (config == null || (config.RoadRules.Count == 0 && config.PolygonRules.Count == 0))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"OSM auto-assign config is empty — using built-in defaults. File: {ConfigFilePath}");
                return OsmMaterialAutoAssignConfig.CreateDefault();
            }

            if (config.Version < OsmMaterialAutoAssignConfig.CurrentVersion)
            {
                // Schema changed: keep the user's old file as backup and start fresh with the
                // new defaults (old rules may miss new required parts like ordering/priorities).
                var backupPath = ConfigFilePath + $".v{config.Version}.bak";
                File.Copy(ConfigFilePath, backupPath, overwrite: true);

                var defaultConfig = OsmMaterialAutoAssignConfig.CreateDefault();
                Save(defaultConfig);

                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"OSM auto-assign config upgraded to version {OsmMaterialAutoAssignConfig.CurrentVersion} " +
                    $"with new defaults. Your previous config was backed up to: {backupPath}");
                return defaultConfig;
            }

            return config;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not read OSM auto-assign config ({ex.Message}) — using built-in defaults. File: {ConfigFilePath}");
            return OsmMaterialAutoAssignConfig.CreateDefault();
        }
    }

    /// <summary>
    ///     Writes the config to the settings folder.
    /// </summary>
    public static void Save(OsmMaterialAutoAssignConfig config)
    {
        Directory.CreateDirectory(AppPaths.SettingsFolder);
        File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(config, JsonOptions));
    }
}
