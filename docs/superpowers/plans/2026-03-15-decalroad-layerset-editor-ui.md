# DecalRoad Layer Set Editor UI Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reusable Blazor UI component for editing DecalRoad layer sets, integrated into GenerateTerrain page (defaults editor) and TerrainMaterialSettings (per-material override).

**Architecture:** A reusable `DecalRoadLayerSetEditor` component renders accordion layer cards with drag-to-reorder inside a `DecalRoadLayerSetEditorDialog` full-screen dialog. The dialog operates in two modes: multi-set (defaults editor with sidebar) and single-set (per-material override). Deep copy on open, explicit Save/Cancel.

**Tech Stack:** .NET 9, Blazor WebView, MudBlazor v8, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-03-15-decalroad-layerset-editor-ui-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Reusable layer set editor: header + accordion layer cards with drag-to-reorder |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Code-behind: layer CRUD, expand/collapse tracking, drag-drop handling |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor` | Full-screen dialog: two-pane (multi-set) or single-pane (single-set) layout |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs` | Code-behind: deep copy, save/cancel, sidebar selection, modified flags |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | Update DecalRoad section: add "Edit Default Layer Sets" button, NodeSpacing/JunctionMargin fields |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | Add `OpenDefaultLayerSetsDialog()` handler |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Add "DecalRoad Layers" section below Master Spline Export |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Add `DecalRoadSettings` parameter, dialog handler, use-defaults toggle |

---

## Chunk 1: Reusable Layer Set Editor Component

### Task 1: Create DecalRoadLayerSetEditor code-behind

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs`

- [ ] **Step 1: Create the code-behind file**

```csharp
// BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class DecalRoadLayerSetEditor
{
    [Parameter] public DecalRoadLayerSet LayerSet { get; set; } = null!;
    [Parameter] public EventCallback LayerSetChanged { get; set; }
    [Parameter] public bool ReadOnly { get; set; }

    private HashSet<int> _expandedIndices = [];

    private bool IsExpanded(int index) => _expandedIndices.Contains(index);

    private void ToggleExpand(int index)
    {
        if (!_expandedIndices.Remove(index))
            _expandedIndices.Add(index);
    }

    private void CollapseAll() => _expandedIndices.Clear();

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

    private async Task OnLayerDropped(MudItemDropInfo<DecalRoadLayerDefinition> dropInfo)
    {
        if (dropInfo.Item == null) return;
        var oldIndex = LayerSet.Layers.IndexOf(dropInfo.Item);
        if (oldIndex < 0) return;

        LayerSet.Layers.RemoveAt(oldIndex);
        var newIndex = Math.Clamp(dropInfo.IndexInZone, 0, LayerSet.Layers.Count);
        LayerSet.Layers.Insert(newIndex, dropInfo.Item);

        // Collapse all on reorder to avoid stale expansion state
        CollapseAll();
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
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded (razor file not yet created, but code-behind alone should compile or we'll create both together)

Note: The code-behind may not compile without the .razor file. Proceed to Task 2 immediately.

---

### Task 2: Create DecalRoadLayerSetEditor razor markup

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor`

- [ ] **Step 1: Create the razor markup**

```razor
@* BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor *@
@using BeamNgTerrainPoc.Terrain.Models.DecalRoad

@if (LayerSet == null) return;

@* Layer Set Header *@
<div class="d-flex align-center justify-space-between mb-2">
    <div class="d-flex align-center gap-3">
        <MudText Typo="Typo.h6">@LayerSet.Name</MudText>
        <MudChip T="string" Size="Size.Small" Color="@(LayerSet.IsEnabled ? Color.Success : Color.Default)"
                 Variant="Variant.Outlined">
            @(LayerSet.IsEnabled ? "Enabled" : "Disabled")
        </MudChip>
    </div>
    <MudSwitch T="bool" @bind-Value="LayerSet.IsEnabled"
               Color="Color.Success" Label="Enabled"
               Disabled="@ReadOnly" />
</div>

<MudGrid Spacing="2" Class="mb-3">
    <MudItem xs="12" sm="4">
        <MudNumericField T="int" @bind-Value="LayerSet.DefaultLaneCount"
                         Label="Default Lane Count"
                         Variant="Variant.Outlined"
                         Min="1" Max="8"
                         Disabled="@ReadOnly" />
    </MudItem>
    <MudItem xs="12" sm="4">
        <MudNumericField T="float" @bind-Value="LayerSet.DefaultLaneWidth"
                         Label="Default Lane Width (m)"
                         Variant="Variant.Outlined"
                         Min="1.0f" Max="10.0f" Step="0.5f"
                         Disabled="@ReadOnly" />
    </MudItem>
</MudGrid>

@* Layer List Header *@
<div class="d-flex align-center justify-space-between mb-2">
    <MudText Typo="Typo.subtitle2">
        Layers (@LayerSet.Layers.Count)
    </MudText>
    @if (!ReadOnly)
    {
        <MudButton Variant="Variant.Text" Color="Color.Primary"
                   StartIcon="@Icons.Material.Filled.Add"
                   Size="Size.Small"
                   OnClick="AddLayer">
            Add Layer
        </MudButton>
    }
</div>

@* Layer Cards with Drag-to-Reorder *@
<MudDropContainer T="DecalRoadLayerDefinition"
                  Items="@LayerSet.Layers"
                  ItemsSelector="@((item, dropzone) => true)"
                  ItemDropped="OnLayerDropped"
                  Class="d-flex flex-column">
    <ChildContent>
        <MudDropZone T="DecalRoadLayerDefinition"
                     Identifier="layers"
                     Class="mud-height-full"
                     AllowReorder="true">
        </MudDropZone>
    </ChildContent>
    <ItemRenderer>
        @{
            var layer = context;
            var layerIndex = LayerSet.Layers.IndexOf(layer);
            var isExpanded = IsExpanded(layerIndex);
        }
        <MudPaper Class="@($"mb-1 {(isExpanded ? "mud-border-primary" : "")}")"
                  Outlined="true" Elevation="0">
            @* Collapsed Header Row *@
            <div class="d-flex align-center pa-2 gap-2" style="cursor:pointer"
                 @onclick="() => ToggleExpand(layerIndex)">
                <MudIcon Icon="@Icons.Material.Filled.DragIndicator"
                         Size="Size.Small" Style="cursor:move;opacity:0.5" />
                <MudChip T="string" Size="Size.Small"
                         Color="@GetLayerTypeColor(layer.LayerType)"
                         Variant="Variant.Filled">
                    @layer.LayerType
                </MudChip>
                <MudText Typo="Typo.body2" Style="font-weight:500;min-width:100px">
                    @layer.Name
                </MudText>
                @if (string.IsNullOrEmpty(layer.Material))
                {
                    <MudChip T="string" Size="Size.Small" Color="Color.Warning"
                             Variant="Variant.Outlined">No material</MudChip>
                }
                else
                {
                    <MudText Typo="Typo.caption" Color="Color.Info">
                        @layer.Material
                    </MudText>
                }
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    @GetWidthDisplay(layer)
                </MudText>
                <MudText Typo="Typo.caption" Color="Color.Secondary">
                    pos:@layer.Position.ToString("F2")
                </MudText>
                <div class="d-flex gap-1 flex-grow-1 justify-end">
                    @if (layer.IsMirrored)
                    {
                        <MudChip T="string" Size="Size.Small" Variant="Variant.Text">mir</MudChip>
                    }
                    @if (layer.InterruptAtJunctions)
                    {
                        <MudChip T="string" Size="Size.Small" Variant="Variant.Text">jnc</MudChip>
                    }
                    @if (layer.IsPerLane)
                    {
                        <MudChip T="string" Size="Size.Small" Variant="Variant.Text">perLn</MudChip>
                    }
                </div>
                <MudIcon Icon="@Icons.Material.Filled.Circle"
                         Size="Size.Small"
                         Color="@(layer.IsEnabled ? Color.Success : Color.Default)"
                         Style="font-size:12px" />
                <MudIcon Icon="@(isExpanded ? Icons.Material.Filled.ExpandLess : Icons.Material.Filled.ExpandMore)"
                         Size="Size.Small" />
            </div>

            @* Expanded Property Grid *@
            @if (isExpanded)
            {
                <MudDivider />
                <div class="pa-3">
                    <MudGrid Spacing="2">
                        @* Row 1: Material + Type *@
                        <MudItem xs="12" sm="8">
                            <MudTextField @bind-Value="layer.Material"
                                          Label="Material"
                                          Variant="Variant.Outlined"
                                          Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="12" sm="4">
                            <MudSelect T="DecalRoadLayerType" @bind-Value="layer.LayerType"
                                       Label="Layer Type"
                                       Variant="Variant.Outlined"
                                       Disabled="@ReadOnly">
                                @foreach (var t in Enum.GetValues<DecalRoadLayerType>())
                                {
                                    <MudSelectItem Value="t">@t</MudSelectItem>
                                }
                            </MudSelect>
                        </MudItem>

                        @* Row 2: Width + Position *@
                        <MudItem xs="12" sm="4">
                            <MudNumericField T="float" @bind-Value="layer.Width"
                                             Label="Width (m)"
                                             Variant="Variant.Outlined"
                                             Min="0.0f" Step="0.05f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="12" sm="4">
                            <MudNumericField T="float" @bind-Value="layer.Position"
                                             Label="Position"
                                             Variant="Variant.Outlined"
                                             Step="0.05f"
                                             HelperText="0=center, ±1=edge, beyond OK"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="12" sm="4">
                            <MudTextField @bind-Value="layer.Name"
                                          Label="Layer Name"
                                          Variant="Variant.Outlined"
                                          Disabled="@ReadOnly" />
                        </MudItem>

                        @* Row 3: TextureLength + RenderPriority *@
                        <MudItem xs="12" sm="4">
                            <MudNumericField T="float" @bind-Value="layer.TextureLength"
                                             Label="Texture Length (m)"
                                             Variant="Variant.Outlined"
                                             Min="0.1f" Max="500.0f" Step="1.0f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="12" sm="4">
                            <MudNumericField T="int" @bind-Value="layer.RenderPriority"
                                             Label="Render Priority"
                                             Variant="Variant.Outlined"
                                             Min="0" Max="100"
                                             Disabled="@ReadOnly" />
                        </MudItem>

                        @* Row 4: Checkboxes *@
                        <MudItem xs="12">
                            <div class="d-flex flex-wrap gap-3">
                                <MudCheckBox T="bool" @bind-Value="layer.IsEnabled"
                                             Label="Enabled" Color="Color.Success"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.IsMirrored"
                                             Label="Mirrored" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.IsPerLane"
                                             Label="Per Lane" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.IsTrackWidth"
                                             Label="Track Width" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.IsLaneWidth"
                                             Label="Lane Width" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.InterruptAtJunctions"
                                             Label="Interrupt @ Junctions" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                            </div>
                        </MudItem>

                        @* Row 5: Fades *@
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="float" @bind-Value="layer.FadeIn"
                                             Label="Fade In (m)"
                                             Variant="Variant.Outlined"
                                             Min="0.0f" Max="500.0f" Step="1.0f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="float" @bind-Value="layer.FadeOut"
                                             Label="Fade Out (m)"
                                             Variant="Variant.Outlined"
                                             Min="0.0f" Max="500.0f" Step="1.0f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="float" Value="layer.DistanceFade[0]"
                                             ValueChanged="v => { layer.DistanceFade[0] = v; }"
                                             Label="Dist Fade Start"
                                             Variant="Variant.Outlined"
                                             Min="0.0f" Max="10000.0f" Step="100.0f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="float" Value="layer.DistanceFade[1]"
                                             ValueChanged="v => { layer.DistanceFade[1] = v; }"
                                             Label="Dist Fade End"
                                             Variant="Variant.Outlined"
                                             Min="0.0f" Max="10000.0f" Step="100.0f"
                                             Disabled="@ReadOnly" />
                        </MudItem>

                        @* Row 6: AI Properties *@
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="float" @bind-Value="layer.Drivability"
                                             Label="Drivability"
                                             Variant="Variant.Outlined"
                                             Min="-1.0f" Max="1.0f" Step="0.1f"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="int" @bind-Value="layer.LanesLeft"
                                             Label="Lanes Left"
                                             Variant="Variant.Outlined"
                                             Min="0" Max="8"
                                             Disabled="@ReadOnly" />
                        </MudItem>
                        <MudItem xs="6" sm="3">
                            <MudNumericField T="int" @bind-Value="layer.LanesRight"
                                             Label="Lanes Right"
                                             Variant="Variant.Outlined"
                                             Min="0" Max="8"
                                             Disabled="@ReadOnly" />
                        </MudItem>

                        @* Row 7: AI flags *@
                        <MudItem xs="12">
                            <div class="d-flex flex-wrap gap-3">
                                <MudCheckBox T="bool" @bind-Value="layer.OneWay"
                                             Label="One Way" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                                <MudCheckBox T="bool" @bind-Value="layer.FlipDirection"
                                             Label="Flip Direction" Color="Color.Primary"
                                             Dense="true" Disabled="@ReadOnly" />
                            </div>
                        </MudItem>

                        @* Row 8: Actions *@
                        @if (!ReadOnly)
                        {
                            <MudItem xs="12">
                                <div class="d-flex justify-end gap-2">
                                    <MudButton Variant="Variant.Text" Color="Color.Primary"
                                               StartIcon="@Icons.Material.Filled.ContentCopy"
                                               Size="Size.Small"
                                               OnClick="() => DuplicateLayer(layerIndex)">
                                        Duplicate
                                    </MudButton>
                                    <MudButton Variant="Variant.Text" Color="Color.Error"
                                               StartIcon="@Icons.Material.Filled.Delete"
                                               Size="Size.Small"
                                               OnClick="() => DeleteLayer(layerIndex)">
                                        Delete
                                    </MudButton>
                                </div>
                            </MudItem>
                        }
                    </MudGrid>
                </div>
            }
        </MudPaper>
    </ItemRenderer>
</MudDropContainer>

@if (LayerSet.Layers.Count == 0)
{
    <MudAlert Severity="Severity.Info" Dense="true" Class="mt-2">
        No layers defined. Click "Add Layer" to create one.
    </MudAlert>
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs
git commit -m "feat: add reusable DecalRoadLayerSetEditor component with accordion layer cards"
```

---

## Chunk 2: Dialog Wrapper Component

### Task 3: Create DecalRoadLayerSetEditorDialog code-behind

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs`

- [ ] **Step 1: Create the dialog code-behind**

```csharp
// BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

public partial class DecalRoadLayerSetEditorDialog
{
    // Multi-set mode (defaults editor)
    [Parameter] public Dictionary<string, DecalRoadLayerSet>? DefaultLayerSets { get; set; }

    // Single-set mode (per-material override)
    [Parameter] public DecalRoadLayerSet? SingleLayerSet { get; set; }
    [Parameter] public string? SingleLayerSetTitle { get; set; }

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    private bool IsMultiSetMode => DefaultLayerSets != null;

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

    private void Save()
    {
        if (IsMultiSetMode)
            MudDialog.Close(DialogResult.Ok(_editingDefaults));
        else
            MudDialog.Close(DialogResult.Ok(_editingSingle));
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
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded (or wait for razor file)

---

### Task 4: Create DecalRoadLayerSetEditorDialog razor markup

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor`

- [ ] **Step 1: Create the dialog razor markup**

```razor
@* BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor *@
@using BeamNgTerrainPoc.Terrain.Models.DecalRoad

<MudDialog>
    <TitleContent>
        <MudText Typo="Typo.h6">
            @if (IsMultiSetMode)
            {
                <text>DecalRoad Default Layer Sets</text>
            }
            else
            {
                <text>DecalRoad Layers — @(SingleLayerSetTitle ?? "Custom")</text>
            }
        </MudText>
    </TitleContent>
    <DialogContent>
        @if (IsMultiSetMode && _editingDefaults != null)
        {
            @* Two-pane layout *@
            <div style="display:flex;min-height:70vh">
                @* Left Sidebar *@
                <MudPaper Style="width:280px;flex-shrink:0;overflow-y:auto"
                          Elevation="0" Class="border-r">
                    <MudText Typo="Typo.overline" Class="pa-3">Road Types</MudText>
                    <MudList T="string" Dense="true">
                        @foreach (var kvp in _editingDefaults)
                        {
                            <MudListItem T="string" Value="@kvp.Key"
                                         OnClick="() => SelectRoadType(kvp.Key)"
                                         Class="@(_selectedKey == kvp.Key ? "mud-selected-item" : "")">
                                <div class="d-flex justify-space-between align-center" style="width:100%">
                                    <div>
                                        <div class="d-flex align-center gap-1">
                                            <MudText Typo="Typo.body2" Style="font-weight:500">
                                                @kvp.Key
                                            </MudText>
                                            @if (_modifiedFlags.TryGetValue(kvp.Key, out var mod) && mod)
                                            {
                                                <MudIcon Icon="@Icons.Material.Filled.Edit"
                                                         Size="Size.Small" Color="Color.Warning"
                                                         Style="font-size:14px" />
                                            }
                                        </div>
                                        <MudText Typo="Typo.caption" Color="Color.Secondary">
                                            @kvp.Value.Layers.Count layers · @kvp.Value.DefaultLaneCount lanes
                                        </MudText>
                                    </div>
                                    <MudIcon Icon="@Icons.Material.Filled.Circle"
                                             Size="Size.Small"
                                             Color="@(kvp.Value.IsEnabled ? Color.Success : Color.Default)"
                                             Style="font-size:12px" />
                                </div>
                            </MudListItem>
                        }
                    </MudList>

                    @* Add Custom Type *@
                    <MudDivider />
                    @if (_showAddTypeInput)
                    {
                        <div class="pa-2">
                            <MudTextField @bind-Value="_newTypeName"
                                          Label="Type Name"
                                          Variant="Variant.Outlined"
                                          Size="Size.Small" />
                            <div class="d-flex gap-1 mt-1">
                                <MudButton Size="Size.Small" Color="Color.Primary"
                                           Variant="Variant.Filled"
                                           OnClick="ConfirmAddType">Add</MudButton>
                                <MudButton Size="Size.Small" Color="Color.Default"
                                           Variant="Variant.Text"
                                           OnClick="CancelAddType">Cancel</MudButton>
                            </div>
                        </div>
                    }
                    else
                    {
                        <MudButton Variant="Variant.Text" Color="Color.Primary"
                                   StartIcon="@Icons.Material.Filled.Add"
                                   Size="Size.Small" Class="ma-2"
                                   OnClick="() => { _showAddTypeInput = true; }">
                            Add Custom Type
                        </MudButton>
                    }
                </MudPaper>

                @* Right Pane *@
                <div style="flex:1;padding:16px;overflow-y:auto">
                    @if (_selectedLayerSet != null && _selectedKey != null)
                    {
                        <div class="d-flex justify-end gap-2 mb-3">
                            @if (IsBuiltInType(_selectedKey))
                            {
                                <MudButton Variant="Variant.Outlined" Color="Color.Warning"
                                           StartIcon="@Icons.Material.Filled.RestartAlt"
                                           Size="Size.Small"
                                           OnClick="ResetToDefault">
                                    Reset to Default
                                </MudButton>
                            }
                            else
                            {
                                <MudButton Variant="Variant.Outlined" Color="Color.Error"
                                           StartIcon="@Icons.Material.Filled.Delete"
                                           Size="Size.Small"
                                           OnClick="DeleteCustomType">
                                    Delete Type
                                </MudButton>
                            }
                        </div>

                        <DecalRoadLayerSetEditor LayerSet="_selectedLayerSet"
                                                 LayerSetChanged="OnLayerSetChanged" />
                    }
                    else
                    {
                        <div class="d-flex align-center justify-center" style="min-height:200px">
                            <MudText Typo="Typo.body1" Color="Color.Secondary">
                                Select a road type from the sidebar
                            </MudText>
                        </div>
                    }
                </div>
            </div>
        }
        else if (_editingSingle != null)
        {
            @* Single-set mode *@
            <div style="min-height:50vh;padding:8px">
                <DecalRoadLayerSetEditor LayerSet="_editingSingle" />
            </div>
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled"
                   OnClick="Save">Save</MudButton>
    </DialogActions>
</MudDialog>
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs
git commit -m "feat: add DecalRoadLayerSetEditorDialog with multi-set and single-set modes"
```

---

## Chunk 3: GenerateTerrain Page Integration

### Task 5: Update GenerateTerrain.razor DecalRoad section

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`

- [ ] **Step 1: Replace the existing DecalRoad section (lines 744-780)**

Find the existing DecalRoad section that starts with `@* DecalRoad Generation Section *@` and replace it entirely:

```razor
                @* DecalRoad Generation Section *@
                <MudPaper Class="pa-4 mt-4" Elevation="1">
                    <MudText Typo="Typo.subtitle2" Class="mb-2">
                        <MudIcon Icon="@Icons.Material.Filled.LinearScale" Size="Size.Small" Class="mr-1" />
                        DecalRoad Generation
                    </MudText>
                    <MudCheckBox @bind-Value="_enableDecalRoads"
                                 Label="Generate road markings and edge blends (DecalRoads)"
                                 Color="Color.Primary"
                                 Disabled="@(!_canFetchOsmData)" />
                    @if (!_canFetchOsmData)
                    {
                        <MudText Typo="Typo.caption" Color="Color.Secondary">
                            Requires GeoTIFF source with OSM data
                        </MudText>
                    }
                    @if (_enableDecalRoads)
                    {
                        <MudText Typo="Typo.body2" Class="mt-2 mb-2">
                            Generates visual road detail layers (edge lines, lane markings, edge blends)
                            projected onto the terrain surface along road splines.
                        </MudText>
                        <MudGrid Spacing="2" Class="mb-3">
                            <MudItem xs="12" sm="4">
                                <MudNumericField T="float"
                                                 Value="@GetDecalRoadNodeSpacing()"
                                                 ValueChanged="SetDecalRoadNodeSpacing"
                                                 Label="Node Spacing (m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.5f" Max="10.0f" Step="0.5f"
                                                 HelperText="Distance between DecalRoad nodes" />
                            </MudItem>
                            <MudItem xs="12" sm="4">
                                <MudNumericField T="float"
                                                 Value="@GetDecalRoadJunctionMargin()"
                                                 ValueChanged="SetDecalRoadJunctionMargin"
                                                 Label="Junction Margin (m)"
                                                 Variant="Variant.Outlined"
                                                 Min="0.0f" Max="20.0f" Step="0.5f"
                                                 HelperText="Extra corridor margin at junctions" />
                            </MudItem>
                        </MudGrid>
                        <div class="d-flex gap-2 align-center">
                            <MudButton Variant="Variant.Outlined"
                                       Color="Color.Primary"
                                       StartIcon="@Icons.Material.Filled.Settings"
                                       OnClick="OpenDefaultLayerSetsDialog">
                                Edit Default Layer Sets
                            </MudButton>
                            <MudButton Variant="Variant.Outlined"
                                       Color="Color.Secondary"
                                       StartIcon="@Icons.Material.Filled.Refresh"
                                       Disabled="@(_state.CachedNetwork == null || _isGenerating)"
                                       OnClick="RegenerateDecalRoads">
                                Re-generate DecalRoads
                            </MudButton>
                        </div>
                        @if (_state.CachedNetwork == null)
                        {
                            <MudText Typo="Typo.caption" Color="Color.Warning" Class="mt-1">
                                Generate terrain first to enable re-generation.
                            </MudText>
                        }
                    }
                </MudPaper>
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded (handler methods added in next step)

---

### Task 6: Add dialog handler and helpers to GenerateTerrain.razor.cs

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

- [ ] **Step 1: Add the using statement**

At the top of the file, add (if not already present):

```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
```

- [ ] **Step 2: Update `_enableDecalRoads` setter to initialize settings**

Find the `_enableDecalRoads` property (around line 129-133) and replace:

```csharp
    private bool _enableDecalRoads
    {
        get => _state.EnableDecalRoads;
        set
        {
            _state.EnableDecalRoads = value;
            if (value) EnsureDecalRoadSettings();
        }
    }
```

- [ ] **Step 3: Add helper methods for DecalRoad settings**

Add after the updated property:

```csharp
    private void EnsureDecalRoadSettings()
    {
        _state.DecalRoadSettings ??= new DecalRoadSettings
        {
            Enabled = true,
            NodeSpacingMeters = 2.0f,
            JunctionExclusionMarginMeters = 0.0f
        };
    }

    private float GetDecalRoadNodeSpacing()
    {
        EnsureDecalRoadSettings();
        return _state.DecalRoadSettings!.NodeSpacingMeters;
    }

    private void SetDecalRoadNodeSpacing(float value)
    {
        EnsureDecalRoadSettings();
        _state.DecalRoadSettings!.NodeSpacingMeters = value;
    }

    private float GetDecalRoadJunctionMargin()
    {
        EnsureDecalRoadSettings();
        return _state.DecalRoadSettings!.JunctionExclusionMarginMeters;
    }

    private void SetDecalRoadJunctionMargin(float value)
    {
        EnsureDecalRoadSettings();
        _state.DecalRoadSettings!.JunctionExclusionMarginMeters = value;
    }

    private async Task OpenDefaultLayerSetsDialog()
    {
        var currentDefaults = DecalRoadDefaultsManager.Load();

        var options = new DialogOptions
        {
            FullScreen = true,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        var parameters = new DialogParameters
        {
            { nameof(DecalRoadLayerSetEditorDialog.DefaultLayerSets), currentDefaults }
        };

        var dialog = await DialogService.ShowAsync<DecalRoadLayerSetEditorDialog>(
            "DecalRoad Default Layer Sets", parameters, options);
        var result = await dialog.Result;

        if (result is { Canceled: false, Data: Dictionary<string, DecalRoadLayerSet> editedDefaults })
        {
            DecalRoadDefaultsManager.Save(editedDefaults);
            Snackbar.Add("Default layer sets saved", Severity.Success);
        }
    }
```

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat: integrate DecalRoad defaults editor dialog into GenerateTerrain page"
```

---

## Chunk 4: Per-Material Override in TerrainMaterialSettings

### Task 7: Add DecalRoad section to TerrainMaterialSettings.razor

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

- [ ] **Step 1: Add the DecalRoad Layers section**

In `TerrainMaterialSettings.razor`, find the closing `</MudItem>` of the last `MudExpansionPanels` block (the Advanced Settings nerd mode section, around line 1189-1191 `</MudItem>`). After the `}` that closes the `@if (Material.IsRoadMaterial)` block (line 1191), add a new section:

```razor
            @* DecalRoad Layer Override *@
            @if (DecalRoadSettings != null && (Material.IsRoadMaterial || Material.EnableRoadPainting))
            {
                <MudItem xs="12">
                    <MudDivider Class="my-2" />
                    <MudText Typo="Typo.subtitle2" Class="mb-2">
                        <MudIcon Icon="@Icons.Material.Filled.LinearScale" Size="Size.Small" Class="mr-1" />
                        DecalRoad Layers
                    </MudText>
                </MudItem>
                <MudItem xs="12">
                    <div class="d-flex align-center gap-1 mb-2">
                        <MudCheckBox T="bool" Value="@IsUsingDecalRoadDefaults()"
                                     ValueChanged="OnUseDecalRoadDefaultsChanged"
                                     Label="Use defaults (resolved via cascade)"
                                     Color="Color.Primary" />
                    </div>
                    @if (IsUsingDecalRoadDefaults())
                    {
                        <MudText Typo="Typo.caption" Color="Color.Secondary">
                            Layer set resolved from defaults based on road type / material name.
                        </MudText>
                    }
                    else
                    {
                        <div class="d-flex align-center gap-2">
                            <MudButton Variant="Variant.Outlined" Color="Color.Primary"
                                       StartIcon="@Icons.Material.Filled.Edit"
                                       Size="Size.Small"
                                       OnClick="OpenDecalRoadLayerSetDialog">
                                Edit Custom Layer Set
                            </MudButton>
                            @{
                                var customSet = GetCustomLayerSet();
                                if (customSet != null)
                                {
                                    <MudText Typo="Typo.caption" Color="Color.Secondary">
                                        Custom: @customSet.Layers.Count layers, @customSet.DefaultLaneCount lanes
                                    </MudText>
                                }
                            }
                        </div>
                    }
                </MudItem>
            }
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: May fail — handler methods not yet added. Proceed to Task 8.

---

### Task 8: Add DecalRoad parameters and handlers to TerrainMaterialSettings.razor.cs

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

- [ ] **Step 1: Add using statement**

At the top of the file, add:

```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNG_LevelCleanUp.Utils;
```

- [ ] **Step 2: Add parameters**

After the existing `[Parameter]` declarations (around line 44, after the `TerrainSize` parameter), add:

```csharp
    [Parameter] public DecalRoadSettings? DecalRoadSettings { get; set; }
```

- [ ] **Step 3: Add helper methods**

Add these methods to the class (at a suitable location, e.g., near the end of the file before the closing brace):

```csharp
    private bool IsUsingDecalRoadDefaults()
    {
        return DecalRoadSettings?.MaterialLayerSets
            .ContainsKey(Material.InternalName) != true;
    }

    private void OnUseDecalRoadDefaultsChanged(bool useDefaults)
    {
        if (DecalRoadSettings == null) return;

        if (useDefaults)
        {
            // Remove custom override — revert to cascade resolution
            DecalRoadSettings.MaterialLayerSets.Remove(Material.InternalName);
        }
        else
        {
            // Create custom override by deep-copying a resolved default.
            // Try cascade: material name match → "primary" fallback → first available → empty
            var defaults = DecalRoadDefaultsManager.Load();
            DecalRoadLayerSet? startingSet = null;
            if (defaults.TryGetValue(Material.InternalName, out var byName))
                startingSet = byName;
            else if (defaults.TryGetValue("primary", out var primary))
                startingSet = primary;
            else if (defaults.Count > 0)
                startingSet = defaults.Values.First();

            startingSet ??= new DecalRoadLayerSet
            {
                Name = Material.InternalName,
                IsEnabled = true,
                DefaultLaneCount = 2,
                DefaultLaneWidth = 3.5f,
                Layers = []
            };

            var json = System.Text.Json.JsonSerializer.Serialize(startingSet, DecalRoadJsonOptions);
            var copy = System.Text.Json.JsonSerializer.Deserialize<DecalRoadLayerSet>(json, DecalRoadJsonOptions)!;
            copy.Name = Material.InternalName;
            DecalRoadSettings.MaterialLayerSets[Material.InternalName] = copy;
        }

        // DecalRoadSettings is mutated in-place (reference type); parent re-renders naturally
    }

    private DecalRoadLayerSet? GetCustomLayerSet()
    {
        if (DecalRoadSettings?.MaterialLayerSets
            .TryGetValue(Material.InternalName, out var set) == true)
            return set;
        return null;
    }

    [Inject] private IDialogService DialogService { get; set; } = null!;
    private async Task OpenDecalRoadLayerSetDialog()
    {
        var customSet = GetCustomLayerSet();
        if (customSet == null) return;

        var options = new DialogOptions
        {
            FullScreen = true,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        var parameters = new DialogParameters
        {
            { nameof(DecalRoadLayerSetEditorDialog.SingleLayerSet), customSet },
            { nameof(DecalRoadLayerSetEditorDialog.SingleLayerSetTitle), Material.InternalName }
        };

        var dialog = await DialogService.ShowAsync<DecalRoadLayerSetEditorDialog>(
            $"DecalRoad Layers — {Material.InternalName}", parameters, options);
        var result = await dialog.Result;

        if (result is { Canceled: false, Data: DecalRoadLayerSet editedSet })
        {
            DecalRoadSettings!.MaterialLayerSets[Material.InternalName] = editedSet;
            if (DecalRoadSettingsChanged.HasDelegate)
                await DecalRoadSettingsChanged.InvokeAsync(DecalRoadSettings);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions DecalRoadJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase) }
    };
```

- [ ] **Step 4: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs
git commit -m "feat: add per-material DecalRoad layer set override to TerrainMaterialSettings"
```

---

## Chunk 5: Wire DecalRoadSettings Parameter Through

### Task 9: Pass DecalRoadSettings from GenerateTerrain to TerrainMaterialSettings

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`

- [ ] **Step 1: Add DecalRoadSettings parameter to TerrainMaterialSettings usage**

In `GenerateTerrain.razor`, find where `TerrainMaterialSettings` is used inside the `MudDropContainer`'s `ItemRenderer` (around line 715-718):

```razor
                                        <TerrainMaterialSettings Material="@context"
                                                                 OnMaterialChanged="OnMaterialSettingsChanged"
                                                                 GeoBoundingBox="@EffectiveBoundingBox"
                                                                 TerrainSize="@_terrainSize" />
```

Add the DecalRoadSettings parameter:

```razor
                                        <TerrainMaterialSettings Material="@context"
                                                                 OnMaterialChanged="OnMaterialSettingsChanged"
                                                                 GeoBoundingBox="@EffectiveBoundingBox"
                                                                 TerrainSize="@_terrainSize"
                                                                 DecalRoadSettings="@(_enableDecalRoads ? _state.DecalRoadSettings : null)" />
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor
git commit -m "feat: pass DecalRoadSettings from GenerateTerrain to TerrainMaterialSettings"
```

---

## Chunk 6: Full Build Verification

### Task 10: Final build and verification

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 2: Run existing tests (if any)**

Run: `dotnet test BeamNgTerrainPoc.Tests -v n`
Expected: All existing tests PASS (no test changes in this plan — UI-only)

- [ ] **Step 3: Commit any final fixes**

If any build issues were found and fixed:

```bash
git add -A
git commit -m "fix: resolve build issues from DecalRoad editor UI integration"
```

---

## Post-Implementation Notes

### Manual Testing Checklist

After implementation, verify manually:
1. Open GenerateTerrain page, enable DecalRoad generation
2. Click "Edit Default Layer Sets" — verify full-screen dialog opens with sidebar
3. Select different road types — verify editor updates
4. Expand/collapse layers — verify accordion behavior
5. Edit a layer property (e.g., width) — verify "modified" indicator appears in sidebar
6. Click "Reset to Default" — verify layer set reverts, indicator clears
7. Add a new custom road type — verify it appears in sidebar
8. Delete a custom road type — verify it's removed
9. Click Save — verify dialog closes, check `decalroad-defaults.json` is updated
10. Click Cancel — verify changes are discarded
11. Drag a layer to reorder — verify it collapses and moves
12. In TerrainMaterialSettings, expand a road material, find "DecalRoad Layers" section
13. Uncheck "Use defaults" — verify "Edit Custom Layer Set" button appears
14. Click edit — verify single-set dialog opens
15. Save custom layer set — verify it persists across preset export/import
16. Re-check "Use defaults" — verify custom override is removed
17. Export preset → import preset — verify DecalRoad settings round-trip correctly
18. Node Spacing / Junction Margin fields — verify they update `DecalRoadSettings` values

### What's NOT in this plan (deferred)

1. **Per-OSM-feature override** — uses `OsmLayerSets`, designed for future integration
2. **Material browser** — material names are typed as text for now
3. **Live preview** — visual layer stack preview
4. **Layer templates** — preset layer configurations (like BeamNG's auto-generated layers)
