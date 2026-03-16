using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class DecalRoadLayerSetEditor
{
    [Parameter] public DecalRoadLayerSet LayerSet { get; set; } = null!;
    [Parameter] public EventCallback LayerSetChanged { get; set; }
    [Parameter] public bool ReadOnly { get; set; }

    /// <summary>
    /// Available decalroad materials for the autocomplete dropdown.
    /// Loaded by parent (Dialog or page) and passed down.
    /// </summary>
    [Parameter] public List<DecalRoadMaterialInfo> AvailableMaterials { get; set; } = [];

    /// <summary>
    /// Callback when user wants to preview a material in the 3D viewer.
    /// </summary>
    [Parameter] public EventCallback<string> OnPreviewMaterial { get; set; }

    private HashSet<int> _expandedIndices = [];
    private DecalRoadLayerSet? _previousLayerSet;
    private int _renderKey;

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(LayerSet, _previousLayerSet))
        {
            _previousLayerSet = LayerSet;
            _expandedIndices.Clear();
            _renderKey++;
        }
    }

    private bool IsExpanded(int index) => _expandedIndices.Contains(index);

    private void ToggleExpand(int index)
    {
        if (!_expandedIndices.Remove(index))
            _expandedIndices.Add(index);
    }

    private async Task NotifyChanged()
    {
        if (LayerSetChanged.HasDelegate)
            await LayerSetChanged.InvokeAsync();
    }

    private async Task AddLayer()
    {
        LayerSet.Layers.Add(new DecalRoadLayerDefinition
        {
            Name = "New Layer",
            LayerType = DecalRoadLayerType.Custom,
            IsEnabled = true,
            Material = string.Empty,
            Width = 0.2f,
            Position = 0.0f
        });
        _expandedIndices.Add(LayerSet.Layers.Count - 1);
        await NotifyChanged();
    }

    private async Task DeleteLayer(int index)
    {
        if (index < 0 || index >= LayerSet.Layers.Count) return;
        LayerSet.Layers.RemoveAt(index);
        _expandedIndices.Remove(index);
        // Adjust expanded indices after removal
        _expandedIndices = _expandedIndices
            .Select(i => i > index ? i - 1 : i)
            .Where(i => i >= 0)
            .ToHashSet();
        await NotifyChanged();
    }

    private async Task DuplicateLayer(int index)
    {
        if (index < 0 || index >= LayerSet.Layers.Count) return;
        var source = LayerSet.Layers[index];
        var copy = DeepCopyLayer(source);
        copy.Name = $"{source.Name} (Copy)";
        LayerSet.Layers.Insert(index + 1, copy);
        _expandedIndices.Add(index + 1);
        await NotifyChanged();
    }

    private static DecalRoadLayerDefinition DeepCopyLayer(DecalRoadLayerDefinition source)
    {
        return new DecalRoadLayerDefinition
        {
            Name = source.Name,
            LayerType = source.LayerType,
            IsEnabled = source.IsEnabled,
            Material = source.Material,
            Width = source.Width,
            TextureLength = source.TextureLength,
            RenderPriority = source.RenderPriority,
            Position = source.Position,
            IsTrackWidth = source.IsTrackWidth,
            IsLaneWidth = source.IsLaneWidth,
            IsMirrored = source.IsMirrored,
            IsPerLane = source.IsPerLane,
            FadeIn = source.FadeIn,
            FadeOut = source.FadeOut,
            DistanceFade = [.. source.DistanceFade],
            InterruptAtJunctions = source.InterruptAtJunctions,
            Drivability = source.Drivability,
            LanesLeft = source.LanesLeft,
            LanesRight = source.LanesRight,
            OneWay = source.OneWay,
            FlipDirection = source.FlipDirection
        };
    }

    private static Color GetLayerTypeColor(DecalRoadLayerType type) => type switch
    {
        DecalRoadLayerType.CenterLine => Color.Warning,
        DecalRoadLayerType.LaneMarking => Color.Tertiary,
        DecalRoadLayerType.EdgeLine => Color.Info,
        DecalRoadLayerType.EdgeBlend => Color.Success,
        DecalRoadLayerType.TreadMarks => Color.Secondary,
        DecalRoadLayerType.AIRoad => Color.Default,
        _ => Color.Default
    };

    private static string GetWidthDisplay(DecalRoadLayerDefinition layer)
    {
        if (layer.IsTrackWidth) return "trk";
        if (layer.IsLaneWidth) return "lane";
        return $"{layer.Width:F2}m";
    }

    /// <summary>
    /// Search function for MudAutocomplete. Filters available materials by name substring.
    /// </summary>
    private Task<IEnumerable<string>> SearchMaterials(string searchText, CancellationToken cancellationToken)
    {
        IEnumerable<DecalRoadMaterialInfo> filtered = AvailableMaterials;

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filtered = filtered.Where(m =>
                m.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        var result = filtered
            .OrderBy(m => m.Source) // Game first, then Level
            .ThenBy(m => m.Name)
            .Select(m => m.Name)
            .Distinct();

        return Task.FromResult(result);
    }

    private async Task PreviewMaterial(string? materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return;
        if (OnPreviewMaterial.HasDelegate)
            await OnPreviewMaterial.InvokeAsync(materialName);
    }

    // O(1) lookup for source badge rendering in dropdown
    private Dictionary<string, DecalRoadMaterialSource> _materialSourceLookup = new(StringComparer.OrdinalIgnoreCase);

    private string GetMaterialSourceBadge(string materialName)
    {
        // Rebuild lookup lazily if stale
        if (_materialSourceLookup.Count == 0 && AvailableMaterials.Count > 0)
        {
            _materialSourceLookup = AvailableMaterials
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Source, StringComparer.OrdinalIgnoreCase);
        }

        if (_materialSourceLookup.TryGetValue(materialName, out var source))
        {
            return source switch
            {
                DecalRoadMaterialSource.Game => "game",
                DecalRoadMaterialSource.Level => "level",
                _ => ""
            };
        }
        return "";
    }
}
