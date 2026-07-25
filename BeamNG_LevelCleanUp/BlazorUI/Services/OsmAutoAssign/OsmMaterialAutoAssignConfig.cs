namespace BeamNG_LevelCleanUp.BlazorUI.Services.OsmAutoAssign;

/// <summary>
///     User-editable rule matrix for the "Auto assign OpenStreetMap data to materials" feature.
///     Persisted as JSON in the app settings folder (see <see cref="OsmMaterialAutoAssignConfigStore" />),
///     so users can adapt the matching to their own material naming schemes without code changes.
/// </summary>
public class OsmMaterialAutoAssignConfig
{
    /// <summary>
    ///     Config schema version. Bump when the structure changes.
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    ///     Current config schema version. Older files on disk are backed up and replaced
    ///     with fresh defaults (see <see cref="OsmMaterialAutoAssignConfigStore" />).
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>
    ///     Road rules assign OSM highway line features + a road smoothing preset to materials.
    ///     Rules are applied in order; a material claimed by one rule is skipped by later rules.
    /// </summary>
    public List<RoadMaterialRule> RoadRules { get; set; } = new();

    /// <summary>
    ///     Polygon rules assign OSM area features (landuse, natural, ...) to non-road materials.
    ///     Applied after road rules; materials claimed by a road rule are skipped.
    /// </summary>
    public List<PolygonMaterialRule> PolygonRules { get; set; } = new();

    /// <summary>
    ///     Material list ordering applied after the assignments.
    /// </summary>
    public MaterialOrderingOptions Ordering { get; set; } = new();

    /// <summary>
    ///     Creates the built-in default rule matrix for vanilla BeamNG material names.
    /// </summary>
    public static OsmMaterialAutoAssignConfig CreateDefault()
    {
        return new OsmMaterialAutoAssignConfig
        {
            Version = CurrentVersion,
            RoadRules =
            {
                new RoadMaterialRule
                {
                    Name = "Asphalt roads",
                    MaterialNameContains = { "asphalt" },
                    MaterialNameExcludes = { "wet" },
                    // Only one asphalt material available: it takes the whole paved road network
                    // with the rural road preset.
                    SingleMaterialAssignment = new RoadTypeAssignment
                    {
                        Preset = "OsmRuralRoad",
                        PaintPriority = 20,
                        HighwayTypes =
                        {
                            "motorway", "motorway_junction", "motorway_link",
                            "trunk", "trunk_link",
                            "primary", "primary_link",
                            "secondary", "secondary_link",
                            "tertiary", "tertiary_link",
                            "residential", "living_street", "road", "service",
                            "raceway"
                        }
                    },
                    // Two or more asphalt materials: split into a highway tier and a rural tier.
                    // Each highway type appears in exactly one tier so the same OSM way is never
                    // painted/smoothed by two materials.
                    MultiMaterialAssignments =
                    {
                        new RoadTypeAssignment
                        {
                            Preset = "OsmHighway",
                            PaintPriority = 30,
                            HighwayTypes =
                            {
                                "motorway", "motorway_junction", "motorway_link",
                                "trunk", "trunk_link",
                                "primary", "primary_link",
                                "raceway"
                            }
                        },
                        new RoadTypeAssignment
                        {
                            Preset = "OsmRuralRoad",
                            PaintPriority = 20,
                            HighwayTypes =
                            {
                                "secondary", "secondary_link",
                                "tertiary", "tertiary_link",
                                "residential", "living_street", "road", "service"
                            }
                        }
                    }
                },
                new RoadMaterialRule
                {
                    Name = "Dirt tracks",
                    MaterialNameContains = { "dirt" },
                    MaterialNameExcludes = { "grass" },
                    SingleMaterialAssignment = new RoadTypeAssignment
                    {
                        Preset = "OsmDirtRoad",
                        PaintPriority = 10,
                        HighwayTypes = { "track" }
                    },
                    // Even with several dirt materials only the first receives the tracks —
                    // duplicating the same ways across materials creates double splines.
                    MultiMaterialAssignments =
                    {
                        new RoadTypeAssignment
                        {
                            Preset = "OsmDirtRoad",
                            PaintPriority = 10,
                            HighwayTypes = { "track" }
                        }
                    }
                }
            },
            PolygonRules =
            {
                new PolygonMaterialRule
                {
                    Name = "Forest",
                    MaterialNameContains = { "forest" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "landuse", SubCategory = "forest" },
                        new OsmTypeReference { Category = "natural", SubCategory = "wood" }
                    }
                },
                // The lowest-numbered grass material becomes the base material (index 0, no
                // features needed — see Ordering.BaseMaterial). The REMAINING grass materials
                // are used for heath, scrub and meadow, in numeric name order.
                new PolygonMaterialRule
                {
                    Name = "Heath",
                    MaterialNameContains = { "grass" },
                    MaterialNameExcludes = { "dirt" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "heath" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Scrub",
                    MaterialNameContains = { "grass" },
                    MaterialNameExcludes = { "dirt" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "scrub" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Meadow",
                    MaterialNameContains = { "grass" },
                    MaterialNameExcludes = { "dirt" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "landuse", SubCategory = "meadow" },
                        new OsmTypeReference { Category = "landuse", SubCategory = "grass" },
                        new OsmTypeReference { Category = "landuse", SubCategory = "village_green" },
                        new OsmTypeReference { Category = "natural", SubCategory = "grassland" }
                    }
                },
                // dirt_grass = rough grass full of nettles: nitrogen-rich disturbed ground
                // around farmyards and fallow land.
                new PolygonMaterialRule
                {
                    Name = "Nettles / rough ground",
                    MaterialNameContains = { "dirt_grass", "dirtgrass" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "landuse", SubCategory = "farmyard" },
                        new OsmTypeReference { Category = "landuse", SubCategory = "brownfield" },
                        new OsmTypeReference { Category = "landuse", SubCategory = "allotments" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Sand",
                    MaterialNameContains = { "sand" },
                    MaterialNameExcludes = { "dirt" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "sand" },
                        new OsmTypeReference { Category = "natural", SubCategory = "beach" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Water",
                    MaterialNameContains = { "rocks_large" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "water" },
                        new OsmTypeReference { Category = "natural", SubCategory = "scree" },
                    }
                },                
                new PolygonMaterialRule
                {
                    Name = "Rock",
                    MaterialNameContains = { "rock" },
                    MaterialNameExcludes = { "dirt", "rocks_large" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "bare_rock" },
                        new OsmTypeReference { Category = "natural", SubCategory = "rock" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Mud",
                    MaterialNameContains = { "mud" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "mud" },
                        new OsmTypeReference { Category = "natural", SubCategory = "wetland" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Gravel",
                    MaterialNameContains = { "gravel", "pebbles" },
                    MaterialNameExcludes = { "wet" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "shingle" },
                        new OsmTypeReference { Category = "landuse", SubCategory = "quarry" }
                    }
                },
                new PolygonMaterialRule
                {
                    Name = "Snow/Ice",
                    MaterialNameContains = { "snow", "ice" },
                    OsmTypes =
                    {
                        new OsmTypeReference { Category = "natural", SubCategory = "glacier" }
                    }
                }
            },
            Ordering = new MaterialOrderingOptions
            {
                EnableAutoOrdering = true,
                BaseMaterial = new BaseMaterialRule
                {
                    Name = "Base grass",
                    MaterialNameContains = { "grass" },
                    MaterialNameExcludes = { "dirt" }
                }
            }
        };
    }
}

/// <summary>
///     Options for reordering the material list after the assignments.
///     Painting rule of the terrain generator: HIGHER index materials paint OVER lower ones.
/// </summary>
public class MaterialOrderingOptions
{
    /// <summary>
    ///     When true (default) the material list is reordered after assigning:
    ///     base material to index 0, road materials to the end sorted by
    ///     <see cref="RoadTypeAssignment.PaintPriority" /> (highest priority last = wins),
    ///     materials without any layer source at the very end (they cannot claim pixels).
    /// </summary>
    public bool EnableAutoOrdering { get; set; } = true;

    /// <summary>
    ///     Selects the default/fallback material moved to index 0. Among the name matches the
    ///     one with the LOWEST numeric suffix wins (grass &lt; grass1 &lt; grass2 ...).
    ///     Null disables the base material move.
    /// </summary>
    public BaseMaterialRule? BaseMaterial { get; set; }
}

/// <summary>
///     Name-part rule selecting the base (index 0) material.
/// </summary>
public class BaseMaterialRule : MaterialNameRule
{
}

/// <summary>
///     Base class for name-part matching of BeamNG terrain material internal names.
/// </summary>
public abstract class MaterialNameRule
{
    /// <summary>
    ///     Display name of the rule (used in log/summary messages).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     A material matches when its internalName contains ANY of these parts (case-insensitive).
    /// </summary>
    public List<string> MaterialNameContains { get; set; } = new();

    /// <summary>
    ///     A material is rejected when its internalName contains ANY of these parts (case-insensitive).
    /// </summary>
    public List<string> MaterialNameExcludes { get; set; } = new();

    /// <summary>
    ///     Checks whether a material internal name matches this rule.
    /// </summary>
    public bool MatchesMaterialName(string internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName))
            return false;

        var matches = MaterialNameContains.Any(part =>
            !string.IsNullOrWhiteSpace(part) &&
            internalName.Contains(part, StringComparison.OrdinalIgnoreCase));

        if (!matches)
            return false;

        return !MaterialNameExcludes.Any(part =>
            !string.IsNullOrWhiteSpace(part) &&
            internalName.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     Rule that assigns OSM highway line features and a road smoothing preset to road materials.
/// </summary>
public class RoadMaterialRule : MaterialNameRule
{
    /// <summary>
    ///     Assignment used when exactly ONE material matches this rule.
    /// </summary>
    public RoadTypeAssignment? SingleMaterialAssignment { get; set; }

    /// <summary>
    ///     Assignments used when TWO OR MORE materials match this rule.
    ///     Entry N is applied to the Nth matching material, sorted by numeric name suffix
    ///     (asphalt before asphalt2) so repeated runs map tiers identically regardless of
    ///     the current list position. Matching materials beyond the number of entries stay
    ///     unassigned.
    /// </summary>
    public List<RoadTypeAssignment> MultiMaterialAssignments { get; set; } = new();
}

/// <summary>
///     A set of OSM highway types plus the road smoothing preset to apply.
/// </summary>
public class RoadTypeAssignment
{
    /// <summary>
    ///     OSM highway=* values to select (e.g. "motorway", "residential", "track").
    /// </summary>
    public List<string> HighwayTypes { get; set; } = new();

    /// <summary>
    ///     Road smoothing preset name. Must match a
    ///     <see cref="Components.TerrainMaterialSettings.RoadPresetType" /> value
    ///     (e.g. "OsmHighway", "OsmRuralRoad", "OsmDirtRoad").
    /// </summary>
    public string Preset { get; set; } = "OsmRuralRoad";

    /// <summary>
    ///     Texture paint priority used when ordering road materials at the end of the list.
    ///     Works like an overhead projector: higher priority = placed later (higher index) =
    ///     paints over the other road materials where corridors overlap.
    ///     Defaults: dirt track 10, rural road 20, highway 30 (most important road wins).
    /// </summary>
    public int PaintPriority { get; set; } = 20;
}

/// <summary>
///     Rule that assigns OSM polygon features (landuse/natural areas) to non-road materials.
/// </summary>
public class PolygonMaterialRule : MaterialNameRule
{
    /// <summary>
    ///     OSM category/subCategory pairs whose polygon features are assigned.
    /// </summary>
    public List<OsmTypeReference> OsmTypes { get; set; } = new();

    /// <summary>
    ///     How many matching materials may receive this rule's features (in numeric name order,
    ///     grass2 before grass3). Default 1 — assigning the same polygons to several materials
    ///     would paint them twice.
    /// </summary>
    public int MaxMaterials { get; set; } = 1;
}

/// <summary>
///     Reference to an OSM feature type: category tag key + value (e.g. landuse=forest).
/// </summary>
public class OsmTypeReference
{
    /// <summary>
    ///     OSM category tag key (e.g. "landuse", "natural", "highway").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    ///     OSM tag value (e.g. "forest", "wood"). Empty matches every subcategory of the category.
    /// </summary>
    public string SubCategory { get; set; } = string.Empty;
}
