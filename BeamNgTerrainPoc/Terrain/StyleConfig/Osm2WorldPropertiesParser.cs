using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
/// Parses OSM2World .properties files into structured dictionaries.
/// Handles the key=value format with comments (#), empty lines, and include directives.
///
/// Key parsing challenge: material names contain underscores, so
/// "material_ROAD_MARKING_ARROW_THROUGH_RIGHT_texture0_file"
/// must be parsed as materialName="ROAD_MARKING_ARROW_THROUGH_RIGHT", property="texture0_file".
///
/// Strategy: Use regex with greedy (.+) before known suffix anchors like _texture\d+_.
/// The greedy match naturally captures the longest possible material name.
/// </summary>
public class Osm2WorldPropertiesParser
{
    // Texture layer properties: material_(.+)_texture(\d+)_(.+) = value
    // Greedy (.+) captures the full material name before _textureN_
    private static readonly Regex MaterialTextureRegex =
        new(@"^material_(.+)_texture(\d+)_(.+)$", RegexOptions.Compiled);

    // Material-level properties: material_(.+)_(color|transparency|doubleSided) = value
    private static readonly Regex MaterialPropertyRegex =
        new(@"^material_(.+)_(color|transparency|doubleSided)$", RegexOptions.Compiled);

    // Traffic sign mappings: trafficSign_(.+)_material = value
    private static readonly Regex TrafficSignMappingRegex =
        new(@"^trafficSign_(.+)_material$", RegexOptions.Compiled);

    // Model entries: model_(.+) = value
    private static readonly Regex ModelRegex =
        new(@"^model_(.+)$", RegexOptions.Compiled);

    // Known traffic sign per-sign properties (non-material lines in traffic sign files)
    private static readonly HashSet<string> TrafficSignPropertySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "numPosts", "defaultHeight"
    };

    /// <summary>
    /// Parses a .properties file and returns structured results.
    /// </summary>
    public PropertiesParseResult Parse(string filePath)
    {
        var result = new PropertiesParseResult();

        foreach (var rawLine in File.ReadLines(filePath, Encoding.UTF8))
        {
            var line = rawLine.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            // Split key = value at first '='
            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            if (key == "include")
            {
                result.IncludeDirectives.Add(value);
                continue;
            }

            // Try material texture property: material_NAME_textureN_PROP
            var m = MaterialTextureRegex.Match(key);
            if (m.Success)
            {
                var matName = m.Groups[1].Value;
                var textureIndex = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var propName = m.Groups[3].Value;
                GetOrAddMaterial(result.Materials, matName)
                    .GetOrAddLayer(textureIndex)[propName] = value;
                continue;
            }

            // Try material-level property: material_NAME_color/transparency/doubleSided
            m = MaterialPropertyRegex.Match(key);
            if (m.Success)
            {
                var matName = m.Groups[1].Value;
                var propName = m.Groups[2].Value;
                SetMaterialProperty(result.Materials, matName, propName, value);
                continue;
            }

            // Try traffic sign mapping: trafficSign_NAME_material = SIGN_ID
            m = TrafficSignMappingRegex.Match(key);
            if (m.Success)
            {
                result.TrafficSignMappings[m.Groups[1].Value] = value;
                continue;
            }

            // Try model entry: model_NAME = path1; path2
            m = ModelRegex.Match(key);
            if (m.Success)
            {
                var modelName = m.Groups[1].Value;
                var paths = value.Split(';')
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .ToList();
                result.Models[modelName] = paths;
                continue;
            }

            // Try traffic sign per-sign properties (e.g., SIGN_DE_600_30_numPosts = 2)
            if (TryParseTrafficSignProperty(key, value, result))
                continue;

            // Everything else is a global setting
            result.GlobalSettings[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Tries to parse a traffic sign per-sign property like "SIGN_DE_600_30_numPosts = 2".
    /// These appear in traffic sign files with known suffixes: numPosts, defaultHeight.
    /// </summary>
    private static bool TryParseTrafficSignProperty(string key, string value, PropertiesParseResult result)
    {
        foreach (var suffix in TrafficSignPropertySuffixes)
        {
            if (key.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                var signName = key[..^(suffix.Length + 1)]; // Strip _suffix
                if (!result.TrafficSignProperties.ContainsKey(signName))
                    result.TrafficSignProperties[signName] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result.TrafficSignProperties[signName][suffix] = value;
                return true;
            }
        }
        return false;
    }

    private static ParsedMaterial GetOrAddMaterial(Dictionary<string, ParsedMaterial> materials, string name)
    {
        if (!materials.TryGetValue(name, out var mat))
        {
            mat = new ParsedMaterial();
            materials[name] = mat;
        }
        return mat;
    }

    private static void SetMaterialProperty(Dictionary<string, ParsedMaterial> materials, string matName, string propName, string value)
    {
        var mat = GetOrAddMaterial(materials, matName);
        switch (propName.ToLowerInvariant())
        {
            case "color":
                mat.Color = value;
                break;
            case "transparency":
                mat.Transparency = value;
                break;
            case "doublesided":
                mat.DoubleSided = value;
                break;
        }
    }
}

/// <summary>
/// Result of parsing an OSM2World .properties file.
/// </summary>
public class PropertiesParseResult
{
    /// <summary>Non-material, non-model global settings (key → value).</summary>
    public Dictionary<string, string> GlobalSettings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Material definitions keyed by material name.</summary>
    public Dictionary<string, ParsedMaterial> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Model definitions keyed by model name (e.g., "CAR" → ["path1", "path2"]).</summary>
    public Dictionary<string, List<string>> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Traffic sign name → material key mappings (e.g., "SIGN_CITY_LIMIT" → "SIGN_DE_310").</summary>
    public Dictionary<string, string> TrafficSignMappings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-sign properties (e.g., "SIGN_DE_600_30" → { "numPosts": "2" }).</summary>
    public Dictionary<string, Dictionary<string, string>> TrafficSignProperties { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Include directives found in the file (e.g., "./locales/DE-defaults.properties").</summary>
    public List<string> IncludeDirectives { get; } = new();
}

/// <summary>
/// A material parsed from properties file, before conversion to typed <see cref="StyleMaterial"/>.
/// </summary>
public class ParsedMaterial
{
    public string? Color { get; set; }
    public string? Transparency { get; set; }
    public string? DoubleSided { get; set; }

    /// <summary>Texture layers keyed by index. Each layer has property name → value pairs.</summary>
    public Dictionary<int, Dictionary<string, string>> TextureLayers { get; } = new();

    /// <summary>Gets or creates the property dictionary for a texture layer index.</summary>
    public Dictionary<string, string> GetOrAddLayer(int index)
    {
        if (!TextureLayers.TryGetValue(index, out var layer))
        {
            layer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TextureLayers[index] = layer;
        }
        return layer;
    }
}
