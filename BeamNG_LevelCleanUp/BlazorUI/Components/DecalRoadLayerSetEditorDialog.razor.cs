using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNG_LevelCleanUp.BlazorUI.Services;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNG_LevelCleanUp.Viewer3D;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using DialogResult = MudBlazor.DialogResult;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class DecalRoadLayerSetEditorDialog
{
    // Multi-set mode (defaults editor)
    [Parameter] public Dictionary<string, DecalRoadLayerSet>? DefaultLayerSets { get; set; }

    // Single-set mode (per-material override)
    [Parameter] public DecalRoadLayerSet? SingleLayerSet { get; set; }
    [Parameter] public string? SingleLayerSetTitle { get; set; }

    /// <summary>
    /// Optional: level path for scanning level-local RoadAndPath materials.
    /// </summary>
    [Parameter] public string? LevelPath { get; set; }

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;
    [Inject] private ISnackbar Snackbar { get; set; } = null!;
    [Inject] private Viewer3DService Viewer3DService { get; set; } = null!;

    private bool IsMultiSetMode => DefaultLayerSets != null;
    private List<DecalRoadMaterialInfo> _availableMaterials = [];

    // Working copies (deep-cloned on init)
    private Dictionary<string, DecalRoadLayerSet>? _editingDefaults;
    private DecalRoadLayerSet? _editingSingle;

    // Sidebar state (multi-set mode)
    private string? _selectedKey;
    private DecalRoadLayerSet? _selectedLayerSet;
    private Dictionary<string, bool> _modifiedFlags = new();

    // Hardcoded defaults for comparison and reset
    private Dictionary<string, DecalRoadLayerSet>? _hardcodedDefaults;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    protected override void OnInitialized()
    {
        if (IsMultiSetMode)
        {
            _editingDefaults = DeepCopy(DefaultLayerSets!);
            _hardcodedDefaults = DecalRoadDefaultLayerSets.GetDefaults();
            ComputeAllModifiedFlags();

            // Select first key
            _selectedKey = _editingDefaults.Keys.FirstOrDefault();
            if (_selectedKey != null)
                _selectedLayerSet = _editingDefaults[_selectedKey];
        }
        else if (SingleLayerSet != null)
        {
            _editingSingle = DeepCopy(SingleLayerSet);
        }

        // Load available materials for the autocomplete
        _availableMaterials = DecalRoadMaterialService.GetAllMaterials(LevelPath);
    }

    private void SelectRoadType(string key)
    {
        _selectedKey = key;
        _selectedLayerSet = _editingDefaults?[key];
    }

    private void OnLayerSetChanged()
    {
        // Mark current road type as modified
        if (_selectedKey != null)
            _modifiedFlags[_selectedKey] = true;
        StateHasChanged();
    }

    private void ResetToDefault()
    {
        if (_selectedKey == null || _editingDefaults == null || _hardcodedDefaults == null) return;

        if (_hardcodedDefaults.TryGetValue(_selectedKey, out var hardcoded))
        {
            var copy = DeepCopy(hardcoded);
            _editingDefaults[_selectedKey] = copy;
            _selectedLayerSet = copy;
            _modifiedFlags[_selectedKey] = false;
        }
    }

    // Inline input for adding custom type names
    private string _newTypeName = string.Empty;
    private bool _showAddTypeInput;

    private void ConfirmAddType()
    {
        if (string.IsNullOrWhiteSpace(_newTypeName) || _editingDefaults == null) return;
        if (_editingDefaults.ContainsKey(_newTypeName)) return;

        var newSet = new DecalRoadLayerSet
        {
            Name = _newTypeName,
            IsEnabled = true,
            DefaultLaneCount = 2,
            DefaultLaneWidth = 3.5f,
            Layers = []
        };
        _editingDefaults[_newTypeName] = newSet;
        _modifiedFlags[_newTypeName] = true;
        SelectRoadType(_newTypeName);
        _newTypeName = string.Empty;
        _showAddTypeInput = false;
    }

    private void CancelAddType()
    {
        _newTypeName = string.Empty;
        _showAddTypeInput = false;
    }

    private void DeleteCustomType()
    {
        if (_selectedKey == null || _editingDefaults == null) return;
        // Only allow deleting custom types (not in hardcoded defaults)
        if (_hardcodedDefaults?.ContainsKey(_selectedKey) == true) return;

        _editingDefaults.Remove(_selectedKey);
        _modifiedFlags.Remove(_selectedKey);
        _selectedKey = _editingDefaults.Keys.FirstOrDefault();
        _selectedLayerSet = _selectedKey != null ? _editingDefaults[_selectedKey] : null;
    }

    private void SaveOnly()
    {
        if (IsMultiSetMode && _editingDefaults != null)
        {
            DecalRoadDefaultsManager.Save(_editingDefaults);
            ComputeAllModifiedFlags();
            Snackbar.Add("Default layer sets saved", Severity.Success);
        }
        else if (_editingSingle != null)
        {
            // Single-set mode: close with result (caller handles persistence)
            MudDialog.Close(DialogResult.Ok(_editingSingle));
        }
    }

    private void SaveAndClose()
    {
        if (IsMultiSetMode && _editingDefaults != null)
        {
            DecalRoadDefaultsManager.Save(_editingDefaults);
            MudDialog.Close(DialogResult.Ok(_editingDefaults));
        }
        else if (_editingSingle != null)
        {
            MudDialog.Close(DialogResult.Ok(_editingSingle));
        }
    }

    private async Task PreviewMaterial(string materialName)
    {
        var info = _availableMaterials.FirstOrDefault(m =>
            m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

        if (info?.MaterialJson != null)
        {
            var matJson = info.MaterialJson;
            if (matJson.MaterialFiles.Count == 0 && matJson.Stages?.Count > 0)
            {
                var stage = matJson.Stages[0];
                var mapPaths = new Dictionary<string, string?>
                {
                    ["baseColorMap"] = stage.BaseColorMap ?? stage.ColorMap ?? stage.DiffuseMap,
                    ["normalMap"] = stage.NormalMap,
                    ["roughnessMap"] = stage.RoughnessMap,
                    ["metallicMap"] = stage.MetallicMap,
                    ["ambientOcclusionMap"] = stage.AmbientOcclusionMap,
                    ["opacityMap"] = stage.OpacityMap,
                    ["emissiveMap"] = stage.EmissiveMap,
                };

                foreach (var (mapType, path) in mapPaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    matJson.MaterialFiles.Add(new MaterialFile
                    {
                        MapType = mapType,
                        MaterialName = matJson.Name,
                        OriginalJsonPath = path,
                        IsGameAsset = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase),
                        File = null
                    });
                }
            }

            var request = new Viewer3DRequest
            {
                Mode = Viewer3DMode.RoadOnPlane,
                Materials = [matJson],
                DisplayName = $"DecalRoad Material: {materialName}",
                LevelPath = LevelPath
            };

            await Viewer3DService.OpenViewerAsync(request);
        }
    }

    private void Cancel() => MudDialog.Cancel();

    private void ComputeAllModifiedFlags()
    {
        _modifiedFlags.Clear();
        if (_editingDefaults == null || _hardcodedDefaults == null) return;

        foreach (var key in _editingDefaults.Keys)
        {
            _modifiedFlags[key] = IsModified(key);
        }
    }

    private bool IsModified(string key)
    {
        if (_editingDefaults == null || _hardcodedDefaults == null) return false;
        if (!_hardcodedDefaults.TryGetValue(key, out var hardcoded)) return true; // Custom type
        if (!_editingDefaults.TryGetValue(key, out var current)) return false;

        var currentJson = JsonSerializer.Serialize(current, JsonOptions);
        var defaultJson = JsonSerializer.Serialize(hardcoded, JsonOptions);
        return currentJson != defaultJson;
    }

    private bool IsBuiltInType(string key) => _hardcodedDefaults?.ContainsKey(key) == true;

    private static T DeepCopy<T>(T source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }
}
