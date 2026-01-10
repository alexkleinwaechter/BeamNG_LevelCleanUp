using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Simplifies BeamNG DAE files for viewing by:
/// 1. Fixing malformed elements (empty init_from, etc.)
/// 2. Extracting only the highest LOD geometry
/// 
/// BeamNG DAE LOD naming rules:
/// - LOD number must be preceded by a letter (e.g., meshname_a2500, meshname_b800)
/// - Higher number = shown at larger pixel sizes = MORE detail
/// - Excluded names: nulldetail, autobillboard, Colmesh (these are special nodes)
/// </summary>
public static class DaeSimplifier
{
    // Pattern to match LOD suffix: any letter followed by digits at the end of a name
    // e.g., "ind_bld_01_a2500" -> captures "2500"
    // e.g., "mesh_b800" -> captures "800"
    // The number MUST be preceded by a letter (not just underscore)
    private static readonly Regex LodPattern = new(@"[a-zA-Z](\d+)$", RegexOptions.Compiled);

    // Names to exclude from display (special BeamNG nodes - collision, LOD control, etc.)
    private static readonly string[] ExcludedNodeNames = 
    {
        "nulldetail",    // LOD control - hides object at distance
        "autobillboard", // Billboard LOD replacement
        "Colmesh",       // Collision mesh
        "ColMesh",       // Collision mesh (case variant)
        "col_",          // Collision prefix
        "COL_",          // Collision prefix (case variant)
        "base00",        // BeamNG hierarchy root
        "start01"        // BeamNG hierarchy start
    };

    /// <summary>
    /// Creates a simplified version of a BeamNG DAE file for viewing.
    /// Fixes common issues and optionally extracts only highest LOD.
    /// </summary>
    /// <param name="sourceDaePath">Path to the original DAE file</param>
    /// <param name="extractHighestLod">If true, only keeps the highest LOD geometry</param>
    /// <returns>Path to the simplified temp DAE file, or null if simplification failed</returns>
    public static string? CreateSimplifiedDae(string sourceDaePath, bool extractHighestLod = true)
    {
        try
        {
            if (!File.Exists(sourceDaePath))
                return null;

            var content = File.ReadAllText(sourceDaePath);
            
            // Fix 1: Fix empty <init_from> elements that cause Assimp to fail
            // BeamNG DAEs often have: <init_from></init_from> or <init_from/>
            content = FixEmptyInitFrom(content);
            
            // Fix 2: Fix empty <surface> elements
            content = FixEmptySurfaceElements(content);

            // Create temp file
            var tempDir = Path.Combine(Path.GetTempPath(), "BeamNG_Viewer");
            Directory.CreateDirectory(tempDir);
            
            var tempFileName = $"{Path.GetFileNameWithoutExtension(sourceDaePath)}_simplified.dae";
            var tempPath = Path.Combine(tempDir, tempFileName);

            // If we want to extract highest LOD, parse and modify the XML
            if (extractHighestLod)
            {
                content = ExtractHighestLodGeometry(content);
            }

            File.WriteAllText(tempPath, content);
            return tempPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Fixes empty init_from elements that cause Assimp to fail.
    /// </summary>
    private static string FixEmptyInitFrom(string content)
    {
        // Pattern 1: <init_from /> (self-closing)
        content = Regex.Replace(
            content,
            @"<init_from\s*/\s*>",
            "<init_from>placeholder.png</init_from>",
            RegexOptions.IgnoreCase);

        // Pattern 2: <init_from></init_from> (empty element)
        content = Regex.Replace(
            content,
            @"<init_from>\s*</init_from>",
            "<init_from>placeholder.png</init_from>",
            RegexOptions.IgnoreCase);

        return content;
    }

    /// <summary>
    /// Fixes empty surface elements.
    /// </summary>
    private static string FixEmptySurfaceElements(string content)
    {
        // Fix empty surface type="2D" elements
        content = Regex.Replace(
            content,
            @"<surface\s+type=""2D"">\s*</surface>",
            @"<surface type=""2D""><init_from>placeholder.png</init_from></surface>",
            RegexOptions.IgnoreCase);

        return content;
    }

    /// <summary>
    /// Checks if a node name should be excluded from display.
    /// Excludes collision meshes, LOD control nodes, and hierarchy markers.
    /// </summary>
    public static bool ShouldExcludeNode(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var excluded in ExcludedNodeNames)
        {
            if (name.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts the LOD level from a geometry/node name.
    /// Returns null if no valid LOD pattern found or if name is excluded.
    /// Higher number = more detail (shown at larger pixel sizes).
    /// </summary>
    public static int? ExtractLodLevel(string? name)
    {
        if (string.IsNullOrEmpty(name) || ShouldExcludeNode(name))
            return null;

        var match = LodPattern.Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var lodLevel))
        {
            return lodLevel;
        }
        return null;
    }

    /// <summary>
    /// Gets the base name without LOD suffix.
    /// e.g., "ind_bld_01_a2500" -> "ind_bld_01_"
    /// </summary>
    public static string GetBaseName(string name)
    {
        var match = LodPattern.Match(name);
        if (match.Success)
        {
            // Remove the letter+digits suffix, keeping everything before
            return name.Substring(0, match.Index);
        }
        return name;
    }

    /// <summary>
    /// Extracts only the highest LOD geometry from the DAE.
    /// BeamNG uses naming like: meshname_a30, meshname_a120, meshname_a2500
    /// Higher number = more detail (shown at larger pixel sizes).
    /// </summary>
    private static string ExtractHighestLodGeometry(string content)
    {
        try
        {
            var doc = XDocument.Parse(content);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            // Find all geometry elements
            var geometries = doc.Descendants(ns + "geometry").ToList();
            if (geometries.Count <= 1)
                return content; // Nothing to simplify

            // Group geometries by base name (without LOD suffix)
            var lodGroups = new Dictionary<string, List<(XElement element, int lodLevel, string name)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var geom in geometries)
            {
                var id = geom.Attribute("id")?.Value ?? "";
                var name = geom.Attribute("name")?.Value ?? id;

                // Skip excluded names (collision meshes, LOD control nodes, etc.)
                if (ShouldExcludeNode(name))
                    continue;

                var lodLevel = ExtractLodLevel(name);
                if (lodLevel.HasValue)
                {
                    // Has LOD suffix - group by base name
                    var baseName = GetBaseName(name);
                    if (!lodGroups.ContainsKey(baseName))
                        lodGroups[baseName] = [];
                    lodGroups[baseName].Add((geom, lodLevel.Value, name));
                }
                // Non-LOD geometries are kept as-is (not added to groups for removal)
            }

            // For each group, keep only the highest LOD (highest number = most detail)
            var geometriesToRemove = new List<XElement>();
            foreach (var group in lodGroups.Values)
            {
                if (group.Count <= 1)
                    continue;

                // Sort by LOD level descending, keep highest
                var sorted = group.OrderByDescending(g => g.lodLevel).ToList();
                
                // Mark all but the highest for removal
                for (int i = 1; i < sorted.Count; i++)
                {
                    geometriesToRemove.Add(sorted[i].element);
                }
            }

            // Remove lower LOD geometries
            foreach (var geom in geometriesToRemove)
            {
                var geomId = geom.Attribute("id")?.Value;
                
                // Also remove references to this geometry in visual_scenes
                if (!string.IsNullOrEmpty(geomId))
                {
                    RemoveGeometryReferences(doc, ns, geomId);
                }
                
                geom.Remove();
            }

            // Clean up unused nodes
            CleanupUnusedNodes(doc, ns);

            return doc.ToString();
        }
        catch
        {
            // If XML parsing fails, return the fixed but unfiltered content
            return content;
        }
    }

    /// <summary>
    /// Removes references to a geometry from visual_scene nodes.
    /// </summary>
    private static void RemoveGeometryReferences(XDocument doc, XNamespace ns, string geometryId)
    {
        var geometryUrl = $"#{geometryId}";
        
        // Find instance_geometry elements referencing this geometry
        var instances = doc.Descendants(ns + "instance_geometry")
            .Where(ig => ig.Attribute("url")?.Value == geometryUrl)
            .ToList();

        foreach (var instance in instances)
        {
            var parent = instance.Parent;
            instance.Remove();
            
            // If parent node is now empty, remove it too
            if (parent != null && !parent.HasElements && parent.Name.LocalName == "node")
            {
                parent.Remove();
            }
        }
    }

    /// <summary>
    /// Removes nodes that no longer have any geometry references.
    /// </summary>
    private static void CleanupUnusedNodes(XDocument doc, XNamespace ns)
    {
        bool removed;
        do
        {
            removed = false;
            var emptyNodes = doc.Descendants(ns + "node")
                .Where(n => !n.HasElements && 
                           string.IsNullOrWhiteSpace(n.Value) &&
                           n.Parent?.Name.LocalName != "library_nodes")
                .ToList();

            foreach (var node in emptyNodes)
            {
                node.Remove();
                removed = true;
            }
        } while (removed);
    }

    /// <summary>
    /// Cleans up temp DAE files older than the specified age.
    /// </summary>
    public static void CleanupTempFiles(TimeSpan maxAge)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "BeamNG_Viewer");
            if (!Directory.Exists(tempDir))
                return;

            var cutoff = DateTime.Now - maxAge;
            foreach (var file in Directory.GetFiles(tempDir, "*_simplified.dae"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // Ignore individual file deletion errors
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
