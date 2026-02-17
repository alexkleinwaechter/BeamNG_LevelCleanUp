using System.Numerics;
using System.Text.Json;
using BeamNG.Procedural3D.Building;
using Grille.BeamNG.IO.Text;
using Grille.BeamNG.SceneTree;
using Grille.BeamNG.SceneTree.Main;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Writes building data into BeamNG scene files:
/// - items.level.json (NDJSON) — SimGroup hierarchy with TSStatic entries per building
/// - materials.json — Material definitions for building wall/roof textures
///
/// Scene hierarchy produced:
///   MissionGroup
///     └─ Buildings (SimGroup)
///         ├─ building_12345 (TSStatic)
///         ├─ building_67890 (TSStatic)
///         └─ ...
///
/// Material format (BeamNG "Material" class with Stages array):
///   {
///     "mtb_brick": {
///       "class": "Material",
///       "name": "mtb_brick",
///       "version": 1.5,
///       "Stages": [{ "baseColorMap": "...", "normalMap": "...", ... }, {}, {}, {}]
///     }
///   }
/// </summary>
public class BuildingSceneWriter
{
    /// <summary>
    /// The name of the SimGroup that contains all building TSStatic objects.
    /// </summary>
    public string GroupName { get; set; } = "MT_buildings";

    /// <summary>
    /// Ensures the SimGroup entry for buildings exists in the parent items.level.json.
    /// If a SimGroup with matching name already exists, it is left untouched (idempotent).
    /// This must be called so BeamNG discovers the Buildings subfolder.
    /// </summary>
    /// <param name="parentItemsPath">Path to the parent items.level.json
    /// (e.g., main/MissionGroup/items.level.json).</param>
    /// <param name="parentGroupName">Name of the parent SimGroup (e.g., "MissionGroup").</param>
    public void EnsureSimGroupInParent(string parentItemsPath, string parentGroupName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(parentItemsPath)!);

        var lines = File.Exists(parentItemsPath)
            ? File.ReadAllLines(parentItemsPath).ToList()
            : new List<string>();

        // Check if a SimGroup with our name already exists
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("class", out var cls) && cls.GetString() == "SimGroup" &&
                    root.TryGetProperty("name", out var name) && name.GetString() == GroupName)
                {
                    // Already registered — nothing to do
                    return;
                }
            }
            catch (JsonException) { }
        }

        // Append the SimGroup entry
        var entry = new Dictionary<string, object>
        {
            { "name", GroupName },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", parentGroupName }
        };
        lines.Add(JsonSerializer.Serialize(entry));
        File.WriteAllLines(parentItemsPath, lines);

        Console.WriteLine($"BuildingSceneWriter: Added '{GroupName}' SimGroup to {parentItemsPath}");
    }

    /// <summary>
    /// Writes the building items.level.json file (NDJSON format).
    /// Contains only TSStatic entries — the SimGroup declaration belongs in the parent.
    /// </summary>
    /// <param name="buildings">The buildings to write scene entries for.</param>
    /// <param name="outputPath">Absolute path for the items.level.json file.</param>
    /// <param name="shapePath">BeamNG-relative path prefix for DAE files
    /// (e.g., "/levels/myLevel/art/shapes/MT_buildings/"). Must end with '/'.</param>
    /// <returns>Number of TSStatic entries written.</returns>
    public int WriteSceneItems(
        IReadOnlyList<BuildingData> buildings,
        string outputPath,
        string shapePath)
    {
        if (buildings.Count == 0)
            return 0;

        // Ensure shapePath ends with /
        if (!shapePath.EndsWith('/'))
            shapePath += "/";

        // Ensure output directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Build the NDJSON entries
        // Note: The SimGroup declaration for this folder belongs in the PARENT
        // directory's items.level.json (e.g., main/MissionGroup/items.level.json),
        // not here. This file contains only the leaf TSStatic entries.
        var items = new List<JsonDict>();

        // TSStatic entry for each building
        int count = 0;
        foreach (var building in buildings)
        {
            var entryDict = CreateTSStaticEntry(building, shapePath);
            items.Add(entryDict);
            count++;
        }

        // Write as NDJSON (one JSON object per line, no commas)
        SimItemsJsonSerializer.Save(outputPath, items);

        Console.WriteLine($"BuildingSceneWriter: Wrote {count} TSStatic entries to {outputPath}");
        return count;
    }

    /// <summary>
    /// Writes a materials.json file containing BeamNG Material definitions
    /// for the building textures actually used by the given buildings.
    /// </summary>
    /// <param name="usedMaterials">The material definitions used by the buildings.</param>
    /// <param name="outputPath">Absolute path for the materials.json file.</param>
    /// <param name="texturePath">BeamNG-relative path prefix for texture files
    /// (e.g., "/levels/myLevel/art/shapes/MT_buildings/textures/"). Must end with '/'.</param>
    /// <returns>Number of materials written.</returns>
    public int WriteMaterials(
        IEnumerable<BuildingMaterialDefinition> usedMaterials,
        string outputPath,
        string texturePath)
    {
        // Ensure texturePath ends with /
        if (!texturePath.EndsWith('/'))
            texturePath += "/";

        // Ensure output directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Build material JSON entries
        var materialDicts = new List<JsonDict>();
        var writtenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var matDef in usedMaterials)
        {
            // Skip duplicate material names (e.g., ROOF_DEFAULT and ROOF_TILES may
            // share the same MaterialName "building_roof_tiles")
            if (!writtenNames.Add(matDef.MaterialName))
                continue;

            var matDict = CreateMaterialEntry(matDef, texturePath);
            materialDicts.Add(matDict);
        }

        // Write as keyed JSON object { "materialName": { ... }, ... }
        ArtItemsJsonSerializer.Save(outputPath, materialDicts);

        Console.WriteLine($"BuildingSceneWriter: Wrote {materialDicts.Count} materials to {outputPath}");
        return materialDicts.Count;
    }

    /// <summary>
    /// Writes the clustered items.level.json file (NDJSON format).
    /// One TSStatic entry per cluster instead of per building.
    /// </summary>
    public int WriteClusteredSceneItems(
        IReadOnlyList<BuildingCluster> clusters,
        string outputPath,
        string shapePath)
    {
        if (clusters.Count == 0)
            return 0;

        if (!shapePath.EndsWith('/'))
            shapePath += "/";

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var items = new List<JsonDict>();

        foreach (var cluster in clusters)
        {
            var entryDict = CreateClusterTSStaticEntry(cluster, shapePath);
            items.Add(entryDict);
        }

        SimItemsJsonSerializer.Save(outputPath, items);

        Console.WriteLine($"BuildingSceneWriter: Wrote {clusters.Count} cluster TSStatic entries to {outputPath}");
        return clusters.Count;
    }

    /// <summary>
    /// Creates a JsonDict representing a TSStatic scene entry for a building cluster.
    /// </summary>
    private JsonDict CreateClusterTSStaticEntry(BuildingCluster cluster, string shapePath)
    {
        var dict = new JsonDict();

        dict["class"] = "TSStatic";
        dict["name"] = cluster.SceneName;
        dict["__parent"] = GroupName;

        dict["position"] = new float[]
        {
            cluster.AnchorPosition.X,
            cluster.AnchorPosition.Y,
            cluster.AnchorPosition.Z
        };

        dict["isRenderEnabled"] = false;
        dict["rotationMatrix"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        dict["shapeName"] = shapePath + cluster.FileName;
        dict["useInstanceRenderData"] = true;

        return dict;
    }

    /// <summary>
    /// Creates a JsonDict representing a TSStatic scene entry for a building.
    /// </summary>
    private JsonDict CreateTSStaticEntry(BuildingData building, string shapePath)
    {
        var dict = new JsonDict();

        dict["class"] = "TSStatic";
        dict["name"] = $"building_{building.BuildingType}_{building.OsmId}";
        dict["__parent"] = GroupName;

        // Position: building centroid in BeamNG world coordinates
        dict["position"] = new float[]
        {
            building.WorldPosition.X,
            building.WorldPosition.Y,
            building.WorldPosition.Z
        };

        dict["isRenderEnabled"] = false;

        // Identity rotation — footprint orientation is baked into the DAE geometry
        dict["rotationMatrix"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        // Shape reference (forward-slash path convention)
        dict["shapeName"] = shapePath + $"building_{building.OsmId}.dae";

        dict["useInstanceRenderData"] = true;

        return dict;
    }

    /// <summary>
    /// Creates a JsonDict representing a BeamNG Material entry for a building material.
    /// Uses the standard Material class with 4-stage Stages array.
    /// </summary>
    private static JsonDict CreateMaterialEntry(BuildingMaterialDefinition matDef, string texturePath)
    {
        var dict = new JsonDict();

        dict["class"] = "Material";
        dict["name"] = matDef.MaterialName;
        dict["mapTo"] = matDef.MaterialName;
        dict["internalName"] = matDef.MaterialName;
        dict["persistentId"] = Guid.NewGuid().ToString();
        dict["version"] = 1.5f;

        // Stage 0: base textures
        var stage0 = new JsonDict();
        stage0["baseColorMap"] = texturePath + matDef.ColorMapFile;

        if (matDef.NormalMapFile != null)
            stage0["normalMap"] = texturePath + matDef.NormalMapFile;

        if (matDef.OrmMapFile != null)
        {
            // ORM maps: BeamNG uses compositeMap or roughnessMap depending on version.
            // roughnessMap is the safer choice for DAE-based objects.
            stage0["roughnessMap"] = texturePath + matDef.OrmMapFile;
        }

        // Stages 1-3: empty (serialize as {} in JSON)
        dict["Stages"] = new JsonDict[] { stage0, new JsonDict(), new JsonDict(), new JsonDict() };

        return dict;
    }
}
