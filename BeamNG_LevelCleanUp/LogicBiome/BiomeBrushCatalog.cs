using BeamNG_LevelCleanUp.LogicCopyForest;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.LogicBiome;

/// <summary>
/// The level's forest palette for the Generate Biome treeview: brushes with their
/// elements, plus all TSForestItemData definitions (including ones no brush references).
/// </summary>
public class BiomeBrushCatalog
{
    public List<ForestBrushInfo> Brushes { get; private init; } = new();

    /// <summary>All item data definitions, keyed by managedItemData key (case-insensitive).</summary>
    public Dictionary<string, ForestItemDataInfo> ItemData { get; private init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Item data keys not referenced by any brush — shown under a synthetic parent in the UI.</summary>
    public List<string> UnbrushedItemNames { get; private init; } = new();

    public static BiomeBrushCatalog Load(string levelPath)
    {
        var brushesPath = ForestBrushCopyScanner.FindForestBrushesFile(levelPath);
        var brushes = string.IsNullOrEmpty(brushesPath)
            ? new List<ForestBrushInfo>()
            : ForestBrushCopyScanner.ParseForestBrushesNdjson(brushesPath);

        var itemDataPath = ForestBrushCopyScanner.FindManagedItemDataFile(levelPath);
        var itemData = ForestBrushCopyScanner.ParseManagedItemData(itemDataPath);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var brush in brushes)
        {
            foreach (var name in brush.ReferencedItemDataNames)
            {
                referenced.Add(name);
            }
        }

        return new BiomeBrushCatalog
        {
            Brushes = brushes.OrderBy(b => b.InternalName, StringComparer.OrdinalIgnoreCase).ToList(),
            ItemData = itemData,
            UnbrushedItemNames = itemData.Keys
                .Where(k => !referenced.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    /// <summary>
    /// The item-type names a brush contributes: its elements' refs, or the single direct
    /// ref for element-less brushes (the scanner already strips the bogus self-reference
    /// that element-carrying brushes have).
    /// </summary>
    public static IEnumerable<string> GetBrushItemNames(ForestBrushInfo brush)
    {
        if (brush.Elements.Count > 0)
        {
            return brush.Elements
                .Select(e => e.ForestItemDataRef)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        return string.IsNullOrEmpty(brush.DirectForestItemData)
            ? Enumerable.Empty<string>()
            : new[] { brush.DirectForestItemData };
    }

    /// <summary>Finds the element carrying the placement parameters for an item within a brush.</summary>
    public ForestBrushElementInfo? FindElement(string brushName, string itemDataName)
    {
        var brush = Brushes.FirstOrDefault(b =>
            b.Name.Equals(brushName, StringComparison.OrdinalIgnoreCase));
        return brush?.Elements.FirstOrDefault(e =>
            e.ForestItemDataRef.Equals(itemDataName, StringComparison.OrdinalIgnoreCase));
    }
}
