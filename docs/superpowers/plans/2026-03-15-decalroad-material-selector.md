# DecalRoad Material Selector Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-text Material field in DecalRoadLayerSetEditor with a searchable dropdown showing BeamNG default decalroad materials (streamed from `art_shapes.zip`) and level-local materials tagged "RoadAndPath", with a "Preview" button that opens a 3D material viewer in a standalone HelixViewerForm window.

**Architecture:** A new `DecalRoadMaterialService` streams `main.materials.json` from BeamNG's `content/art_shapes.zip` without extraction, parses it for decalroad materials, and merges with level-local "RoadAndPath" materials. The UI uses MudBlazor `MudAutocomplete` for searchable selection. A "Preview" button opens HelixViewerForm via `Viewer3DService` in `RoadOnPlane` mode, with textures streamed from content zips using the existing `ZipAssetExtractor` / `GameFileSystem` infrastructure.

**Tech Stack:** .NET 9, Blazor WebView, MudBlazor v8, System.IO.Compression, existing Viewer3D/HelixViewerForm infrastructure

**Spec:** This document is both plan and spec.

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `BeamNG_LevelCleanUp/Utils/DecalRoadMaterialService.cs` | Service: streams `main.materials.json` from `art_shapes.zip`, parses materials, caches results. Also scans level-local materials. Returns merged list of `DecalRoadMaterialInfo` items. |
| `BeamNG_LevelCleanUp/Objects/DecalRoadMaterialInfo.cs` | Lightweight DTO: material name, source (Game/Level), base color map path, material tags. Enough to display in dropdown and open preview. |

### Modified Files

| File | Changes |
|------|---------|
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor` | Replace `MudTextField` for Material (line 124-128) with `MudAutocomplete` + Preview button |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs` | Add material list parameter, search function, preview handler |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor` | Pass `AvailableMaterials` and `OnPreviewMaterial` to editor |
| `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs` | Add material loading, LevelPath parameter, preview handler |
| `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | Pass `WorkingDirectory` as LevelPath to dialog |
| `BeamNG_LevelCleanUp/Viewer3D/TextureLookup.cs` | Handle `MaterialFile` with `File=null` + `OriginalJsonPath` fallback |
| `BeamNG_LevelCleanUp/Utils/LinkFileResolver.cs` | Add `/assets/...` path resolution via `ZipAssetExtractor` |

---

## Key Design Decisions

### ZIP Streaming Strategy

**Problem:** `art_shapes.zip` contains `art/shapes/common/decalroads/main.materials.json`. We must NOT extract the entire zip.

**Solution:** Use `System.IO.Compression.ZipFile.OpenRead()` to open the archive, find the entry by path, and `entry.Open()` to get a stream. Read the JSON string from stream, parse with `JsonUtils.GetValidJsonDocumentFromString()` (handles BeamNG's relaxed JSON). This is the same pattern as `ZipAssetExtractor.ExtractFromZip()`.

**ZIP path:** `{GameInstallDir}/content/art_shapes.zip` → entry: `art/shapes/common/decalroads/main.materials.json`

### Texture Preview Path Resolution

When a user clicks "Preview", textures referenced in the material need to be resolved:

- Materials in `art_shapes.zip` reference textures like: `"baseColorMap": "/assets/materials/decalroad/treadmark/m_dirt_tiretracks/t_dirt_tiretracks_b.color.png"`
- These resolve to: `{GameInstallDir}/content/assets/materials/decalroad.zip` → entry with matching path
- The existing `GameFileSystem.GetAbsolutePaths()` already handles `/assets/materials/{category}/*` → `content/assets/materials/{category}.zip` routing
- The existing `TextureLoader` + `LinkFileResolver` in the Viewer3D already handle streaming textures via `ZipAssetExtractor`

**Approach:** Build a `MaterialJson` object from the parsed ZIP data (with `MaterialStage` containing texture paths), then use the existing `Viewer3DService.OpenViewerAsync(Viewer3DRequest)` with `RoadOnPlane` mode. The existing Viewer3D pipeline already resolves `/assets/...` paths through `ZipAssetExtractor`.

For level-local materials, the `MaterialJson` objects already exist from scanning — pass them directly.

### Caching

The service caches the game materials list for the app session (static field). Game content doesn't change during a session. Level materials are re-scanned when the level path changes.

### Material Selection in DecalRoadLayerSetEditor

Replace the `MudTextField` with `MudAutocomplete<string>`:
- Shows all materials matching the search string (name substring match)
- Groups by source: "Game (DecalRoad)" and "Level (RoadAndPath)"
- Shows base color map path as secondary text
- The bound value remains `layer.Material` (a plain string — the material name)
- Users can still type a custom material name not in the list (autocomplete with `CoerceValue="false"`)

---

## Chunk 1: Material Service & DTO

### Task 1: Create DecalRoadMaterialInfo DTO

**Files:**
- Create: `BeamNG_LevelCleanUp/Objects/DecalRoadMaterialInfo.cs`

- [ ] **Step 1: Create the DTO**

```csharp
// BeamNG_LevelCleanUp/Objects/DecalRoadMaterialInfo.cs
namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Lightweight info about a decalroad material for use in dropdowns/selectors.
/// </summary>
public class DecalRoadMaterialInfo
{
    /// <summary>Material name (used as the DecalRoad "material" property in BeamNG).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where this material comes from.</summary>
    public DecalRoadMaterialSource Source { get; set; }

    /// <summary>Base color map path (for preview and display). May be a /assets/... path.</summary>
    public string? BaseColorMap { get; set; }

    /// <summary>Material tags (e.g., "RoadAndPath").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Full MaterialJson if available (for preview). Null for game materials until preview requested.</summary>
    public MaterialJson? MaterialJson { get; set; }

    /// <summary>Display string for the autocomplete dropdown.</summary>
    public string DisplayText => Source switch
    {
        DecalRoadMaterialSource.Game => $"{Name}  [game]",
        DecalRoadMaterialSource.Level => $"{Name}  [level]",
        _ => Name
    };
}

public enum DecalRoadMaterialSource
{
    Game,   // From art_shapes.zip (BeamNG default decalroad materials)
    Level   // From level's materials.json files (tagged RoadAndPath)
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

---

### Task 2: Create DecalRoadMaterialService

**Files:**
- Create: `BeamNG_LevelCleanUp/Utils/DecalRoadMaterialService.cs`

- [ ] **Step 1: Create the service**

```csharp
// BeamNG_LevelCleanUp/Utils/DecalRoadMaterialService.cs
using System.IO.Compression;
using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Provides decalroad material names from two sources:
/// 1. BeamNG game defaults: streamed from art_shapes.zip (no extraction)
/// 2. Level-local: materials tagged "RoadAndPath" from the current level
/// </summary>
public static class DecalRoadMaterialService
{
    // Cached game materials (static — game content doesn't change during session)
    private static List<DecalRoadMaterialInfo>? _cachedGameMaterials;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns all decalroad materials: game defaults + level-local.
    /// Game materials are cached for the session. Level materials are scanned fresh.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetAllMaterials(string? levelPath = null)
    {
        var result = new List<DecalRoadMaterialInfo>();
        result.AddRange(GetGameMaterials());
        if (!string.IsNullOrEmpty(levelPath))
            result.AddRange(GetLevelMaterials(levelPath));
        return result;
    }

    /// <summary>
    /// Returns game decalroad materials from art_shapes.zip.
    /// Cached after first load.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetGameMaterials()
    {
        lock (_lock)
        {
            if (_cachedGameMaterials != null)
                return _cachedGameMaterials;

            _cachedGameMaterials = LoadGameMaterialsFromZip();
            return _cachedGameMaterials;
        }
    }

    /// <summary>
    /// Scans level directory for materials tagged "RoadAndPath".
    /// Not cached — call when level changes.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetLevelMaterials(string levelPath)
    {
        var result = new List<DecalRoadMaterialInfo>();
        try
        {
            // Find all *.materials.json files in the level (not just art/terrains)
            var matFiles = Directory.GetFiles(levelPath, "*.materials.json", SearchOption.AllDirectories);

            foreach (var matFile in matFiles)
            {
                try
                {
                    var jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(matFile);
                    var options = BeamJsonOptions.GetJsonSerializerOptions();

                    foreach (var property in jsonDoc.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var material = property.Value.Deserialize<MaterialJson>(options);
                            if (material == null) continue;

                            // Set name from property key if not set
                            if (string.IsNullOrEmpty(material.Name))
                                material.Name = property.Name;
                            if (string.IsNullOrEmpty(material.InternalName))
                                material.InternalName = property.Name;

                            material.MatJsonFileLocation = matFile;

                            // Only include materials tagged "RoadAndPath"
                            if (!material.IsRoadAndPath) continue;

                            // Extract base color map
                            string? baseColorMap = null;
                            if (material.Stages?.Count > 0)
                            {
                                var stage = material.Stages[0];
                                baseColorMap = stage.BaseColorMap ?? stage.ColorMap ?? stage.DiffuseMap;
                            }

                            result.Add(new DecalRoadMaterialInfo
                            {
                                Name = material.InternalName ?? material.Name,
                                Source = DecalRoadMaterialSource.Level,
                                BaseColorMap = baseColorMap,
                                Tags = material.MaterialTags,
                                MaterialJson = material
                            });
                        }
                        catch
                        {
                            // Skip malformed material entries
                        }
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Failed to scan materials from {matFile}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to scan level materials at {levelPath}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Streams main.materials.json from art_shapes.zip and parses decalroad materials.
    /// </summary>
    private static List<DecalRoadMaterialInfo> LoadGameMaterialsFromZip()
    {
        var result = new List<DecalRoadMaterialInfo>();

        try
        {
            var installDir = GameDirectoryService.GetInstallDirectory();
            if (string.IsNullOrEmpty(installDir))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Cannot load game decalroad materials: BeamNG install directory not configured.");
                return result;
            }

            var zipPath = Path.Combine(installDir, "content", "art_shapes.zip");
            if (!File.Exists(zipPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot find art_shapes.zip at: {zipPath}");
                return result;
            }

            const string entryPath = "art/shapes/common/decalroads/main.materials.json";

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryPath);
            if (entry == null)
            {
                // Try case-insensitive search
                entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));
            }

            if (entry == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot find {entryPath} in art_shapes.zip");
                return result;
            }

            // Read JSON from zip stream
            string jsonString;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                jsonString = reader.ReadToEnd();
            }

            // Parse with BeamNG's relaxed JSON handling
            var jsonDoc = JsonUtils.GetValidJsonDocumentFromString(jsonString, $"art_shapes.zip/{entryPath}");
            var options = BeamJsonOptions.GetJsonSerializerOptions();

            foreach (var property in jsonDoc.RootElement.EnumerateObject())
            {
                try
                {
                    var material = property.Value.Deserialize<MaterialJson>(options);
                    if (material == null) continue;

                    if (string.IsNullOrEmpty(material.Name))
                        material.Name = property.Name;
                    if (string.IsNullOrEmpty(material.InternalName))
                        material.InternalName = property.Name;

                    // Extract base color map for display/preview
                    string? baseColorMap = null;
                    if (material.Stages?.Count > 0)
                    {
                        var stage = material.Stages[0];
                        baseColorMap = stage.BaseColorMap ?? stage.ColorMap ?? stage.DiffuseMap;
                    }

                    // Mark as game asset (no filesystem path)
                    material.MatJsonFileLocation = string.Empty;

                    result.Add(new DecalRoadMaterialInfo
                    {
                        Name = material.InternalName ?? material.Name,
                        Source = DecalRoadMaterialSource.Game,
                        BaseColorMap = baseColorMap,
                        Tags = material.MaterialTags,
                        MaterialJson = material
                    });
                }
                catch
                {
                    // Skip malformed material entries
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Loaded {result.Count} decalroad materials from art_shapes.zip");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to load game decalroad materials: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Clears the cached game materials (e.g., if game directory changes).
    /// </summary>
    public static void ClearCache()
    {
        lock (_lock)
        {
            _cachedGameMaterials = null;
        }
    }
}
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/Objects/DecalRoadMaterialInfo.cs
git add BeamNG_LevelCleanUp/Utils/DecalRoadMaterialService.cs
git commit -m "feat: add DecalRoadMaterialService for streaming materials from art_shapes.zip and level scanning"
```

---

## Chunk 2: Material Autocomplete in Layer Set Editor

### Task 3: Update DecalRoadLayerSetEditor code-behind

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs`

- [ ] **Step 1: Add material list parameter and search method**

Add to the existing parameters section (after `ReadOnly`):

```csharp
    /// <summary>
    /// Available decalroad materials for the autocomplete dropdown.
    /// Loaded by parent (Dialog or page) and passed down.
    /// </summary>
    [Parameter] public List<DecalRoadMaterialInfo> AvailableMaterials { get; set; } = [];

    /// <summary>
    /// Callback when user wants to preview a material in the 3D viewer.
    /// </summary>
    [Parameter] public EventCallback<string> OnPreviewMaterial { get; set; }
```

Add using statement at top:

```csharp
using BeamNG_LevelCleanUp.Objects;
```

Add search method to the class:

```csharp
    /// <summary>
    /// Search function for MudAutocomplete. Filters available materials by name substring.
    /// Returns material names (strings), grouped: game materials first, then level materials.
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
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

---

### Task 4: Update DecalRoadLayerSetEditor razor markup

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor`

- [ ] **Step 1: Add using statement**

Add at top of file (after existing `@using`):

```razor
@using BeamNG_LevelCleanUp.Objects
```

- [ ] **Step 2: Replace the Material MudTextField with MudAutocomplete + Preview button**

Find this block (lines 123-129, inside the expanded property grid):

```razor
                    @* Row 1: Material + Type *@
                    <MudItem xs="12" sm="8">
                        <MudTextField @bind-Value="layer.Material"
                                      Label="Material"
                                      Variant="Variant.Outlined"
                                      Disabled="@ReadOnly" />
                    </MudItem>
```

Replace with:

```razor
                    @* Row 1: Material + Type *@
                    <MudItem xs="12" sm="6">
                        @if (AvailableMaterials.Count > 0)
                        {
                            <MudAutocomplete T="string" @bind-Value="layer.Material"
                                             Label="Material"
                                             Variant="Variant.Outlined"
                                             SearchFunc="SearchMaterials"
                                             CoerceValue="false"
                                             Clearable="true"
                                             Dense="true"
                                             MaxItems="50"
                                             Disabled="@ReadOnly"
                                             AdornmentIcon="@Icons.Material.Filled.Search"
                                             Adornment="Adornment.Start">
                                <ItemTemplate>
                                    <div class="d-flex align-center gap-2">
                                        <MudText Typo="Typo.body2">@context</MudText>
                                        @{
                                            var badge = GetMaterialSourceBadge(context);
                                        }
                                        @if (!string.IsNullOrEmpty(badge))
                                        {
                                            <MudChip T="string" Size="Size.Small"
                                                     Variant="Variant.Outlined"
                                                     Color="@(badge == "game" ? Color.Info : Color.Success)">
                                                @badge
                                            </MudChip>
                                        }
                                    </div>
                                </ItemTemplate>
                            </MudAutocomplete>
                        }
                        else
                        {
                            <MudTextField @bind-Value="layer.Material"
                                          Label="Material"
                                          Variant="Variant.Outlined"
                                          Disabled="@ReadOnly" />
                        }
                    </MudItem>
                    <MudItem xs="12" sm="2">
                        <MudIconButton Icon="@Icons.Material.Filled.Preview"
                                       Color="Color.Primary"
                                       Size="Size.Medium"
                                       Style="margin-top:8px"
                                       Disabled="@(string.IsNullOrEmpty(layer.Material))"
                                       OnClick="() => PreviewMaterial(layer.Material)"
                                       Title="Preview material in 3D viewer" />
                    </MudItem>
```

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditor.razor.cs
git commit -m "feat: replace Material text field with searchable autocomplete and preview button"
```

---

## Chunk 3: Wire Materials into Dialog and Preview

### Task 5: Update DecalRoadLayerSetEditorDialog to load and pass materials

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor`

- [ ] **Step 1: Add material loading and preview to dialog code-behind**

In `DecalRoadLayerSetEditorDialog.razor.cs`, add these using statements (if not present):

```csharp
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNG_LevelCleanUp.Viewer3D;
using BeamNG_LevelCleanUp.BlazorUI.Services;
```

Add these fields/parameters:

```csharp
    /// <summary>
    /// Optional: level path for scanning level-local RoadAndPath materials.
    /// </summary>
    [Parameter] public string? LevelPath { get; set; }

    [Inject] private Viewer3DService Viewer3DService { get; set; } = null!;

    private List<DecalRoadMaterialInfo> _availableMaterials = [];
```

In `OnInitialized()`, add material loading at the end:

```csharp
        // Load available materials for the autocomplete
        _availableMaterials = DecalRoadMaterialService.GetAllMaterials(LevelPath);
```

Add the preview handler:

```csharp
    private async Task PreviewMaterial(string materialName)
    {
        // Find the material info
        var info = _availableMaterials.FirstOrDefault(m =>
            m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));

        if (info?.MaterialJson != null)
        {
            // Build MaterialFiles from MaterialStage for the viewer
            var matJson = info.MaterialJson;
            if (matJson.MaterialFiles.Count == 0 && matJson.Stages?.Count > 0)
            {
                // Build minimal MaterialFiles for preview
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
                        File = null // Viewer will resolve via path
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
```

- [ ] **Step 2: Pass AvailableMaterials and preview handler to DecalRoadLayerSetEditor in dialog markup**

In `DecalRoadLayerSetEditorDialog.razor`, find the two places where `<DecalRoadLayerSetEditor>` is used.

**Multi-set mode** (around line where `<DecalRoadLayerSetEditor LayerSet="_selectedLayerSet"` is):

Replace:
```razor
                        <DecalRoadLayerSetEditor LayerSet="_selectedLayerSet"
                                                 LayerSetChanged="OnLayerSetChanged" />
```

With:
```razor
                        <DecalRoadLayerSetEditor LayerSet="_selectedLayerSet"
                                                 LayerSetChanged="OnLayerSetChanged"
                                                 AvailableMaterials="_availableMaterials"
                                                 OnPreviewMaterial="PreviewMaterial" />
```

**Single-set mode** (around line where `<DecalRoadLayerSetEditor LayerSet="_editingSingle"` is):

Replace:
```razor
                <DecalRoadLayerSetEditor LayerSet="_editingSingle" />
```

With:
```razor
                <DecalRoadLayerSetEditor LayerSet="_editingSingle"
                                         AvailableMaterials="_availableMaterials"
                                         OnPreviewMaterial="PreviewMaterial" />
```

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

---

### Task 6: Pass LevelPath from GenerateTerrain page when opening dialog

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

- [ ] **Step 1: Add LevelPath parameter to dialog invocation**

In `OpenDefaultLayerSetsDialog()`, find the `DialogParameters` block and add the LevelPath:

Find:
```csharp
        var parameters = new DialogParameters
        {
            { nameof(DecalRoadLayerSetEditorDialog.DefaultLayerSets), currentDefaults }
        };
```

Replace with:
```csharp
        var parameters = new DialogParameters
        {
            { nameof(DecalRoadLayerSetEditorDialog.DefaultLayerSets), currentDefaults },
            { nameof(DecalRoadLayerSetEditorDialog.LevelPath), _state.WorkingDirectory }
        };
```

**Note:** `TerrainGenerationState.WorkingDirectory` holds the base level directory path. If terrain hasn't been generated yet this will be null/empty — that's fine, game materials will still work.

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor
git add BeamNG_LevelCleanUp/BlazorUI/Components/DecalRoadLayerSetEditorDialog.razor.cs
git add BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat: wire material list and 3D preview into DecalRoadLayerSetEditorDialog"
```

---

## Chunk 4: Texture Resolution for Game Materials in Viewer3D

**CRITICAL:** Without these changes, clicking "Preview" on a game material will show a blank white plane. The existing Viewer3D pipeline does NOT handle `/assets/...` paths or `MaterialFile` entries where `File` is null.

### Task 7: Fix TextureLookup.Build() to accept game asset paths

**Files:**
- Modify: `BeamNG_LevelCleanUp/Viewer3D/TextureLookup.cs`

**Problem:** `TextureLookup.Build()` skips `MaterialFile` entries where `File == null` (line ~81: `if (file.File == null) continue;`). Game materials from ZIP parsing have `File = null` but `OriginalJsonPath` set to `/assets/materials/decalroad/...`.

- [ ] **Step 1: Modify Build() to use OriginalJsonPath fallback**

In `TextureLookup.Build()`, find the section that iterates `material.MaterialFiles` and skips null files. Replace the null-check logic so that `OriginalJsonPath` is used when `File` is null:

Find the pattern:
```csharp
if (file.File == null)
    continue;
```

Replace with:
```csharp
// Determine the texture path — filesystem file or game asset path
var texturePath = file.File?.FullName;
if (texturePath == null && !string.IsNullOrEmpty(file.OriginalJsonPath))
{
    // Game asset — use the JSON path for resolution via ZipAssetExtractor
    texturePath = file.OriginalJsonPath;
}
if (texturePath == null) continue;
```

Then update all subsequent references to `file.File.FullName` in the same loop body to use `texturePath` instead. Also update the `CanResolve()` / `File.Exists()` check to include a fallback:

Find the pattern that checks file existence (something like):
```csharp
if (!File.Exists(file.File.FullName) && !LinkFileResolver.CanResolve(file.File.FullName))
    continue;
```

Replace with:
```csharp
if (!CanResolveTexturePath(texturePath))
    continue;
```

And add a helper method to the `TextureLookup` class:

```csharp
    private static bool CanResolveTexturePath(string path)
    {
        // Direct file on disk
        if (File.Exists(path)) return true;

        // .link file resolution
        if (LinkFileResolver.CanResolve(path)) return true;

        // /assets/... game asset path — resolve via ZipAssetExtractor
        if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = ZipAssetExtractor.ExtractAsset(path.TrimStart('/'));
            return stream != null;
        }

        return false;
    }
```

- [ ] **Step 2: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

---

### Task 8: Fix LinkFileResolver.GetFileStream() to handle /assets/ paths

**Files:**
- Modify: `BeamNG_LevelCleanUp/Utils/LinkFileResolver.cs`

**Problem:** `LinkFileResolver.GetFileStream()` only handles `.link` files and regular filesystem files. When `TextureLoader.LoadTexture()` calls it with a path like `/assets/materials/decalroad/...`, it falls through and returns null.

- [ ] **Step 1: Add /assets/ path handling**

In `LinkFileResolver.GetFileStream()`, add this block **before** the existing `File.Exists()` check:

```csharp
    // Handle /assets/ game paths by streaming from content ZIPs
    if (filePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
        filePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
    {
        var stream = ZipAssetExtractor.ExtractAsset(filePath.TrimStart('/'));
        if (stream != null) return stream;
    }
```

- [ ] **Step 2: Update CanResolve() to also handle /assets/ paths**

In `LinkFileResolver.CanResolve()`, add a check for `/assets/` paths:

```csharp
    // /assets/ game paths
    if (filePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
        filePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
    {
        using var stream = ZipAssetExtractor.ExtractAsset(filePath.TrimStart('/'));
        return stream != null;
    }
```

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/Viewer3D/TextureLookup.cs
git add BeamNG_LevelCleanUp/Utils/LinkFileResolver.cs
git commit -m "fix: enable game asset texture resolution from content ZIPs in Viewer3D pipeline"
```

**Verification:** After this change, the end-to-end path is:
1. `MaterialFile(File=null, OriginalJsonPath="/assets/materials/decalroad/...")`
2. → `TextureLookup.Build()` uses `OriginalJsonPath` as `texturePath`
3. → `CanResolveTexturePath()` confirms `/assets/` path is resolvable via `ZipAssetExtractor`
4. → `TextureLoader.LoadTexture()` calls `LinkFileResolver.GetFileStream()`
5. → `GetFileStream()` detects `/assets/` prefix, calls `ZipAssetExtractor.ExtractAsset()`
6. → Texture bytes streamed from `{GameInstall}/content/assets/materials/decalroad.zip`

---

## Chunk 5: Full Build & Manual Testing

### Task 9: Final build verification

- [ ] **Step 1: Build entire solution**

Run: `dotnet build`
Expected: Build succeeded (ignore MSB3027/MSB3021 DLL lock warnings from running app)

- [ ] **Step 2: Commit any fixes**

If any build issues were found and fixed:

```bash
git add -A
git commit -m "fix: resolve build issues from DecalRoad material selector integration"
```

---

## Manual Testing Checklist

After implementation, verify manually:

1. **Game materials load**: Open DecalRoad Layer Set Editor → expand any layer → click Material field → verify ~20+ materials appear in dropdown with "[game]" badge
2. **Search works**: Type "tread" → verify filtered list shows tread mark materials
3. **Level materials load**: When editing from a level that has RoadAndPath materials, verify "[level]" badge items appear
4. **Custom input works**: Type a material name not in the list → verify it's accepted (CoerceValue=false)
5. **Preview button**: Select a material → click preview icon → verify HelixViewerForm opens showing the material on a road-aspect-ratio plane
6. **Game texture resolution**: Preview a game material (e.g., `m_tread_marks_clean`) → verify texture loads from content ZIPs (not a blank/white plane)
7. **Fallback**: If no BeamNG install configured, verify dropdown still works but is empty (no crash)
8. **Existing functionality**: Verify all other layer editor features still work (add/delete/duplicate layer, expand/collapse, checkboxes, etc.)

---

## Architecture Reference

### ZIP Path Mapping

| Material Source | ZIP Location | Entry Path |
|---|---|---|
| Game decalroad materials JSON | `{GameInstall}/content/art_shapes.zip` | `art/shapes/common/decalroads/main.materials.json` |
| Game decalroad textures | `{GameInstall}/content/assets/materials/decalroad.zip` | `assets/materials/decalroad/{subfolder}/{file}` |
| Level materials | Filesystem: `{levelPath}/**/*.materials.json` | N/A (direct file read) |
| Level textures | Filesystem: `{levelPath}/art/**/*` | N/A (direct file read) |

### Existing Code Reuse

| Component | Reused For |
|---|---|
| `ZipAssetExtractor.ExtractFromZip()` | Stream JSON from `art_shapes.zip` |
| `JsonUtils.GetValidJsonDocumentFromString()` | Parse BeamNG's relaxed JSON |
| `BeamJsonOptions.GetJsonSerializerOptions()` | Deserialize `MaterialJson` objects |
| `Viewer3DService.OpenViewerAsync()` | Open 3D preview window on STA thread |
| `Viewer3DRequest` with `RoadOnPlane` mode | Road-aspect-ratio material preview |
| `MaterialFactory` | PBR/Phong material creation for preview |
| `TextureLoader` | DDS/PNG texture loading from streams |
| `GameDirectoryService.GetInstallDirectory()` | Resolve BeamNG install path |
| `GameFileSystem.GetAbsolutePaths()` | Route `/assets/...` paths to correct ZIPs |

### What's NOT in This Plan (Deferred)

1. **Texture thumbnail in dropdown** — would require async image loading per dropdown item; MudAutocomplete doesn't support this well
2. **Material browser/gallery view** — a full-screen grid of material previews with filtering
3. **Auto-scan additional ZIP files** — other BeamNG content ZIPs may contain decalroad materials
4. **Hot-reload on game directory change** — currently requires app restart to clear cache
