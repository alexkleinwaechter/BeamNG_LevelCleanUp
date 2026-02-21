using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeamNgTerrainPoc.Terrain.StyleConfig;

/// <summary>
///     Generates osm2world-style.json and locale JSON files by:
///     1. Parsing .properties files from the downloaded OSM2World style package
///     2. Converting to structured JSON models
///     3. Enhancing materials with BeamNG-specific extensions
///     4. Writing JSON files to %LocalAppData%\BeamNG_LevelCleanUp\
///     This is a one-shot generation that runs after the OSM2World style package
///     is downloaded. The resulting JSON files become the user-editable configuration.
/// </summary>
public class StyleConfigGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // =========================================================================
    // BeamNG Extension Mapping — 28 materials
    // This is the externalized version of BuildingMaterialLibrary.RegisterDefaultMaterials().
    // =========================================================================

    private static readonly Dictionary<string, BeamNgMaterialExtension> BeamNgExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // --- Wall Materials (12) ---

            ["BUILDING_DEFAULT"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_plaster", InstanceDiffuse = true,
                DefaultColor = [220, 210, 190],
                ColorMapFile = "Plaster002_Color.color.png",
                NormalMapFile = "Plaster002_Normal.normal.png",
                OrmMapFile = "Plaster002_ORM.data.png"
            },
            ["BRICK"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_brick",
                DefaultColor = [165, 85, 60],
                ColorMapFile = "Bricks029_Color.color.png",
                NormalMapFile = "Bricks029_Normal.normal.png",
                OrmMapFile = "Bricks029_ORM.data.png"
            },
            ["CONCRETE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_concrete",
                DefaultColor = [170, 170, 165],
                ColorMapFile = "Concrete034_Color.color.png",
                NormalMapFile = "Concrete034_Normal.normal.png",
                OrmMapFile = "Concrete034_ORM.data.png"
            },
            ["WOOD_WALL"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_wood_wall",
                DefaultColor = [160, 120, 75],
                ColorMapFile = "WoodSiding008_Color.color.png",
                NormalMapFile = "WoodSiding008_Normal.normal.png",
                OrmMapFile = "WoodSiding008_ORM.data.png"
            },
            ["GLASS_WALL"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_glass_wall",
                DefaultColor = [230, 230, 230],
                ColorMapFile = "Facade005_Color.color.png",
                NormalMapFile = "Facade005_Normal.normal.png",
                OrmMapFile = "Facade005_ORM.data.png"
            },
            ["CORRUGATED_STEEL"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_corrugated_steel",
                DefaultColor = [160, 165, 170],
                ColorMapFile = "CorrugatedSteel005_Color.color.png",
                NormalMapFile = "CorrugatedSteel005_Normal.normal.png",
                OrmMapFile = "CorrugatedSteel005_ORM.data.png"
            },
            ["ADOBE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_adobe",
                DefaultColor = [180, 150, 110],
                ColorMapFile = "Ground026_Color.color.png",
                NormalMapFile = "Ground026_Normal.normal.png",
                OrmMapFile = "Ground026_ORM.data.png"
            },
            ["SANDSTONE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_sandstone",
                DefaultColor = [210, 190, 150],
                ColorMapFile = "Bricks008_Color.color.png",
                NormalMapFile = "Bricks008_Normal.normal.png",
                OrmMapFile = "Bricks008_ORM.data.png"
            },
            ["STONE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_stone",
                DefaultColor = [160, 160, 155],
                ColorMapFile = "Bricks008_Color.color.png",
                NormalMapFile = "Bricks008_Normal.normal.png",
                OrmMapFile = "Bricks008_ORM.data.png"
            },
            ["STEEL"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_steel",
                DefaultColor = [180, 185, 190],
                ColorMapFile = "Metal002_Color.color.png",
                NormalMapFile = "Metal002_Normal.normal.png",
                OrmMapFile = "Metal002_ORM.data.png"
            },
            ["TILES"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_tiles",
                DefaultColor = [200, 200, 195],
                ColorMapFile = "Tiles036_Color.color.png",
                NormalMapFile = "Tiles036_Normal.normal.png",
                OrmMapFile = "Tiles036_ORM.data.png"
            },
            ["MARBLE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_marble",
                DefaultColor = [230, 230, 225],
                ColorMapFile = "Marble001_Color.color.png",
                NormalMapFile = "Marble001_Normal.normal.png",
                OrmMapFile = "Marble001_ORM.data.png"
            },

            // --- Roof Materials (8) ---

            ["ROOF_DEFAULT"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_roof_tiles", IsRoofMaterial = true,
                DefaultColor = [140, 55, 40],
                ColorMapFile = "RoofingTiles010_Color.color.png",
                NormalMapFile = "RoofingTiles010_Normal.normal.png",
                OrmMapFile = "RoofingTiles010_ORM.data.png"
            },
            ["ROOF_TILES"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_roof_tiles", IsRoofMaterial = true,
                DefaultColor = [140, 55, 40],
                ColorMapFile = "RoofingTiles010_Color.color.png",
                NormalMapFile = "RoofingTiles010_Normal.normal.png",
                OrmMapFile = "RoofingTiles010_ORM.data.png"
            },
            // BUG FIX: MetalPlates006 does NOT exist in OSM2World cc0textures.
            // Using CorrugatedSteel005 which is the closest available metal texture.
            ["ROOF_METAL"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_roof_metal", IsRoofMaterial = true,
                DefaultColor = [120, 125, 130],
                ColorMapFile = "CorrugatedSteel005_Color.color.png",
                NormalMapFile = "CorrugatedSteel005_Normal.normal.png",
                OrmMapFile = "CorrugatedSteel005_ORM.data.png"
            },
            ["SLATE"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_slate", IsRoofMaterial = true,
                DefaultColor = [90, 95, 100],
                ColorMapFile = "RoofingTiles003_Color.color.png",
                NormalMapFile = "RoofingTiles003_Normal.normal.png",
                OrmMapFile = "RoofingTiles003_ORM.data.png"
            },
            ["THATCH_ROOF"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_thatch", IsRoofMaterial = true,
                DefaultColor = [170, 150, 90],
                ColorMapFile = "ThatchedRoof001A_Color.color.png",
                NormalMapFile = "ThatchedRoof001A_Normal.normal.png",
                OrmMapFile = "ThatchedRoof001A_ORM.data.png"
            },
            ["COPPER_ROOF"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_copper_roof", IsRoofMaterial = true,
                DefaultColor = [195, 219, 185],
                ColorMapFile = "RoofingTiles010_Color.color.png",
                NormalMapFile = "RoofingTiles010_Normal.normal.png",
                OrmMapFile = "RoofingTiles010_ORM.data.png"
            },
            ["WOOD_ROOF"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_wood_roof", IsRoofMaterial = true,
                DefaultColor = [160, 120, 75],
                ColorMapFile = "Wood026_Color.color.png",
                NormalMapFile = "Wood026_Normal.normal.png",
                OrmMapFile = "Wood026_ORM.data.png"
            },
            ["GLASS_ROOF"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_glass_roof", IsRoofMaterial = true,
                DefaultColor = [230, 230, 230],
                ColorMapFile = "Facade005_Color.color.png",
                NormalMapFile = "Facade005_Normal.normal.png",
                OrmMapFile = "Facade005_ORM.data.png"
            },

            // --- Facade Materials (7) ---
            // Port of OSM2World facade elements. Custom textures from style package.

            // OSM2World: SINGLE_WINDOW — individual window pane texture (LOD1)
            ["SINGLE_WINDOW"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_window_single",
                DefaultColor = [255, 255, 255],
                ColorMapFile = "Window_Color.color.png",
                NormalMapFile = "Window_Normal.normal.png",
                OrmMapFile = "Window_ORM.data.png"
            },
            // OSM2World: BUILDING_WINDOWS — facade-wide window texture band (LOD1)
            ["BUILDING_WINDOWS"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_building_windows",
                DefaultColor = [255, 230, 140],
                ColorMapFile = "Windows_Color.color.png",
                NormalMapFile = "Windows_Normal.normal.png",
                OrmMapFile = "Windows_ORM.data.png"
            },
            // Window frame stays solid-color (OSM2World PLASTIC = Material(FLAT, WHITE))
            ["WINDOW_FRAME"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_window_frame",
                DefaultColor = [255, 255, 255],
                ColorMapFile = "mtb_window_frame_Color.color.png"
            },
            // OSM2World: GLASS — opaque glass
            ["GLASS"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_window_glass", Opacity = 0.5f, DoubleSided = true,
                DefaultColor = [230, 230, 230],
                ColorMapFile = "Glass_Color.color.png",
                NormalMapFile = "Glass_Normal.normal.png",
                OrmMapFile = "Glass_ORM.data.png"
            },
            // OSM2World: GLASS_TRANSPARENT — transparent glass variant
            ["GLASS_TRANSPARENT"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_glass_transparent", Opacity = 0.5f, DoubleSided = true,
                DefaultColor = [230, 230, 230],
                ColorMapFile = "Glass_Color.color.png",
                NormalMapFile = "Glass_Normal.normal.png",
                OrmMapFile = "Glass_ORM.data.png"
            },
            // OSM2World: ENTRANCE_DEFAULT — standard entrance door (locale-specific texture)
            ["ENTRANCE_DEFAULT"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_door_default",
                DefaultColor = [255, 255, 255],
                ColorMapFile = "DE19F1FreisingDoor00005_small.color.png"
            },
            // OSM2World: GARAGE_DOOR — overhead/rollup garage door (locale-specific texture)
            ["GARAGE_DOOR"] = new BeamNgMaterialExtension
            {
                MaterialName = "mtb_door_garage",
                DefaultColor = [255, 255, 255],
                ColorMapFile = "DE20F1GarageDoor00001.color.png"
            }
        };

    private readonly string _osm2WorldStyleFolder;
    private readonly string _outputFolder;

    public StyleConfigGenerator(string osm2WorldStyleFolder, string outputFolder)
    {
        _osm2WorldStyleFolder = osm2WorldStyleFolder;
        _outputFolder = outputFolder;
    }

    /// <summary>
    ///     Generates all JSON configuration files from the downloaded OSM2World style package.
    /// </summary>
    public StyleConfigGenerationResult Generate()
    {
        var result = new StyleConfigGenerationResult();
        var parser = new Osm2WorldPropertiesParser();

        try
        {
            // 1. Parse standard.properties
            var standardPath = Path.Combine(_osm2WorldStyleFolder, "standard.properties");
            if (!File.Exists(standardPath))
            {
                result.ErrorMessage = $"standard.properties not found at {standardPath}";
                return result;
            }

            var standardProps = parser.Parse(standardPath);

            // 2. Build main config
            var mainConfig = BuildMainConfig(standardProps);

            // 3. Apply BeamNG extensions to known materials
            ApplyBeamNgExtensions(mainConfig);

            // 4. Write main config
            var mainConfigPath = Path.Combine(_outputFolder, "osm2world-style.json");
            WriteJson(mainConfigPath, mainConfig);
            result.MainConfigWritten = true;
            result.MainMaterialCount = mainConfig.Materials.Count;

            // 5. Parse and write locale files
            var localesDir = Path.Combine(_osm2WorldStyleFolder, "locales");
            if (Directory.Exists(localesDir))
            {
                var localeOutputDir = Path.Combine(_outputFolder, "osm2world-style-locales");
                Directory.CreateDirectory(localeOutputDir);

                // Process locale defaults files (e.g., DE-defaults.properties)
                foreach (var localeFile in Directory.GetFiles(localesDir, "*-defaults.properties"))
                {
                    var localeName = ExtractLocaleName(localeFile);
                    var localeProps = parser.Parse(localeFile);
                    var localeConfig = BuildLocaleConfig(localeProps, localeName, Path.GetFileName(localeFile));

                    var outputPath = Path.Combine(localeOutputDir, $"{localeName}-defaults.json");
                    WriteJson(outputPath, localeConfig);
                    result.LocaleFilesWritten++;
                }

                // Process traffic sign files (e.g., DE-trafficSigns.properties)
                foreach (var signFile in Directory.GetFiles(localesDir, "*-trafficSigns.properties"))
                {
                    var localeName = ExtractLocaleName(signFile);
                    var signProps = parser.Parse(signFile);
                    var signConfig = BuildTrafficSignsConfig(signProps, localeName, Path.GetFileName(signFile));

                    var outputPath = Path.Combine(localeOutputDir, $"{localeName}-trafficSigns.json");
                    WriteJson(outputPath, signConfig);
                    result.TrafficSignFilesWritten++;
                    result.TrafficSignMaterialCount += signConfig.Materials.Count;
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private Osm2WorldStyleConfig BuildMainConfig(PropertiesParseResult props)
    {
        var config = new Osm2WorldStyleConfig
        {
            GeneratedFrom = "standard.properties",
            GeneratedAt = DateTime.UtcNow
        };

        // Map global settings
        config.Settings = MapGlobalSettings(props.GlobalSettings);

        // Convert parsed materials to StyleMaterial objects
        foreach (var (matName, parsedMat) in props.Materials)
            config.Materials[matName] = ConvertToStyleMaterial(parsedMat);

        // Copy models
        foreach (var (name, paths) in props.Models) config.Models[name] = paths;

        return config;
    }

    private Osm2WorldLocaleConfig BuildLocaleConfig(PropertiesParseResult props, string locale, string sourceFile)
    {
        var config = new Osm2WorldLocaleConfig
        {
            Locale = locale,
            GeneratedFrom = $"locales/{sourceFile}"
        };

        // Settings (e.g., drivingSide)
        foreach (var (key, value) in props.GlobalSettings) config.Settings[key] = value;

        // Traffic sign mappings
        foreach (var (name, materialKey) in props.TrafficSignMappings) config.TrafficSignMappings[name] = materialKey;

        // Materials (sign materials with text overlays)
        foreach (var (matName, parsedMat) in props.Materials)
            config.Materials[matName] = ConvertToStyleMaterial(parsedMat);

        return config;
    }

    private Osm2WorldTrafficSignsConfig BuildTrafficSignsConfig(PropertiesParseResult props, string locale,
        string sourceFile)
    {
        var config = new Osm2WorldTrafficSignsConfig
        {
            Locale = locale,
            GeneratedFrom = $"locales/{sourceFile}"
        };

        // Traffic sign defaults (defaultTrafficSignHeight, standardPoleRadius)
        foreach (var (key, value) in props.GlobalSettings)
            if (float.TryParse(value, CultureInfo.InvariantCulture, out var floatVal))
                config.TrafficSignDefaults[key] = floatVal;

        // Per-sign properties
        foreach (var (signName, signProps) in props.TrafficSignProperties)
            config.TrafficSignProperties[signName] = new Dictionary<string, string>(signProps);

        // Sign materials
        foreach (var (matName, parsedMat) in props.Materials)
            config.Materials[matName] = ConvertToStyleMaterial(parsedMat);

        return config;
    }

    private static StyleGlobalSettings MapGlobalSettings(Dictionary<string, string> settings)
    {
        var s = new StyleGlobalSettings();

        if (settings.TryGetValue("locale", out var locale))
            s.Locale = locale;
        if (settings.TryGetValue("createTerrain", out var ct))
            s.CreateTerrain = ct.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("backgroundColor", out var bg))
            s.BackgroundColor = bg;
        if (settings.TryGetValue("exportAlpha", out var ea))
            s.ExportAlpha = ea.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("treesPerSquareMeter", out var tpsm))
            s.TreesPerSquareMeter = ParseFloat(tpsm, 0.02f);
        if (settings.TryGetValue("useBuildingColors", out var ubc))
            s.UseBuildingColors = ubc.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("useBillboards", out var ub))
            s.UseBillboards = ub.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("renderUnderground", out var ru))
            s.RenderUnderground = ru.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("forceUnbufferedPNGRendering", out var fu))
            s.ForceUnbufferedPNGRendering = fu.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (settings.TryGetValue("canvasLimit", out var cl) && int.TryParse(cl, out var clVal))
            s.CanvasLimit = clVal;
        if (settings.TryGetValue("msaa", out var msaa) && int.TryParse(msaa, out var msaaVal))
            s.Msaa = msaaVal;
        if (settings.TryGetValue("drivingSide", out var ds))
            s.DrivingSide = ds;

        return s;
    }

    private static StyleMaterial ConvertToStyleMaterial(ParsedMaterial parsed)
    {
        var mat = new StyleMaterial
        {
            Color = parsed.Color,
            Transparency = parsed.Transparency,
            DoubleSided = parsed.DoubleSided?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ? true
                : parsed.DoubleSided?.Equals("false", StringComparison.OrdinalIgnoreCase) == true ? false
                : null
        };

        foreach (var (layerIndex, layerProps) in parsed.TextureLayers.OrderBy(kv => kv.Key))
        {
            var layer = new StyleTextureLayer { Index = layerIndex };

            if (layerProps.TryGetValue("dir", out var dir)) layer.Dir = dir;
            if (layerProps.TryGetValue("file", out var file)) layer.File = file;
            if (layerProps.TryGetValue("color_file", out var cf)) layer.ColorFile = cf;
            if (layerProps.TryGetValue("width", out var w)) layer.Width = ParseFloatNullable(w);
            if (layerProps.TryGetValue("height", out var h)) layer.Height = ParseFloatNullable(h);
            if (layerProps.TryGetValue("widthPerEntity", out var wpe)) layer.WidthPerEntity = ParseFloatNullable(wpe);
            if (layerProps.TryGetValue("heightPerEntity", out var hpe)) layer.HeightPerEntity = ParseFloatNullable(hpe);
            if (layerProps.TryGetValue("coord_function", out var coordFn)) layer.CoordFunction = coordFn;
            if (layerProps.TryGetValue("colorable", out var colorable))
                layer.Colorable = colorable.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (layerProps.TryGetValue("wrap", out var wrap)) layer.Wrap = wrap;
            if (layerProps.TryGetValue("padding", out var padding)) layer.Padding = ParseFloatNullable(padding);
            if (layerProps.TryGetValue("transparency", out var transparency)) layer.Transparency = transparency;
            if (layerProps.TryGetValue("color", out var color)) layer.Color = color;
            if (layerProps.TryGetValue("type", out var type)) layer.Type = type;
            if (layerProps.TryGetValue("text", out var text)) layer.Text = text;
            if (layerProps.TryGetValue("font", out var font)) layer.Font = font;
            if (layerProps.TryGetValue("relative_font_size", out var rfs))
                layer.RelativeFontSize = ParseFloatNullable(rfs);
            if (layerProps.TryGetValue("textColor", out var textColor)) layer.TextColor = textColor;

            mat.TextureLayers.Add(layer);
        }

        return mat;
    }

    /// <summary>
    ///     Applies BeamNG-specific extensions to materials that we use in the building pipeline.
    ///     Only materials with known BeamNG mappings get extensions; all others keep Beamng = null.
    /// </summary>
    private static void ApplyBeamNgExtensions(Osm2WorldStyleConfig config)
    {
        foreach (var (matKey, extension) in BeamNgExtensions)
            if (config.Materials.TryGetValue(matKey, out var styleMat))
                // Material exists in parsed properties — add BeamNG extension
                styleMat.Beamng = extension;
            else
                // Material not in standard.properties (e.g., ROOF_TILES, facade materials)
                // Add it with just the BeamNG extension
                config.Materials[matKey] = new StyleMaterial { Beamng = extension };
    }

    private static string ExtractLocaleName(string filePath)
    {
        // "DE-defaults.properties" → "DE", "PL-trafficSigns.properties" → "PL"
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var dashIndex = fileName.IndexOf('-');
        return dashIndex > 0 ? fileName[..dashIndex] : fileName;
    }

    private static void WriteJson<T>(string path, T config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static float ParseFloat(string value, float defaultValue)
    {
        return float.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static float? ParseFloatNullable(string? value)
    {
        if (value == null) return null;
        return float.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}

/// <summary>
///     Result of JSON configuration generation.
/// </summary>
public class StyleConfigGenerationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool MainConfigWritten { get; set; }
    public int MainMaterialCount { get; set; }
    public int LocaleFilesWritten { get; set; }
    public int TrafficSignFilesWritten { get; set; }
    public int TrafficSignMaterialCount { get; set; }

    public override string ToString()
    {
        if (!Success)
            return $"Generation failed: {ErrorMessage}";
        return $"Generated: {MainMaterialCount} main materials, " +
               $"{LocaleFilesWritten} locale files, " +
               $"{TrafficSignFilesWritten} traffic sign files ({TrafficSignMaterialCount} sign materials)";
    }
}