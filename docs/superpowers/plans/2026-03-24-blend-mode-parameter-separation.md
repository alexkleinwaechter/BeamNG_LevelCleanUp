# Blend Mode Parameter Separation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple `SideMaxSlopeDegrees` and `FalloffExponent` so each blend mode uses only its relevant parameter — Exponential uses only `FalloffExponent`, all others use only `SideMaxSlopeDegrees`.

**Architecture:** The exponential blend function `(1-t)^exp` naturally reaches 0 at the DOI boundary, so no slope constraint or DOI auto-extension is needed. Non-exponential modes (Linear/Cosine/Cubic/Quintic) keep the existing slope constraint + DOI auto-extension behavior unchanged. The UI conditionally shows only the relevant parameter.

**Tech Stack:** C# / .NET 9 / Blazor / MudBlazor v8

---

### Task 1: Pipeline — SinglePassBlender blend loop

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs:148-181`

- [ ] **Step 1: Make DOI auto-extension conditional on non-Exponential**

In the per-pixel blend loop (lines 148-160), wrap the slope-based DOI auto-extension so it only runs for non-Exponential blend functions:

```csharp
// Calculate effective DOI for this pixel.
var doi = sp.DOI;

// DOI auto-extension: only for non-Exponential blend functions.
// Exponential's (1-t)^exp naturally reaches 0 at DOI boundary — no cliff.
// Non-exponential modes need slope-based DOI extension to prevent cliffs.
if (sp.BlendFunction != BlendFunctionType.Exponential)
{
    var elevDiff = MathF.Abs(ribbonZ - originalHeightMap[y, x]);
    if (sp.MaxSlopeDeg > 0.1f && sp.MaxSlopeDeg < 89f && elevDiff > 0.1f)
    {
        var tanMaxSlope = MathF.Tan(sp.MaxSlopeDeg * MathF.PI / 180f);
        var slopeRequiredDist = elevDiff / tanMaxSlope;
        doi = MathF.Max(doi, slopeRequiredDist);
    }
}
```

- [ ] **Step 2: Make EnforceSideMaxSlope conditional on non-Exponential**

In the blend calculation (lines 175-181), skip the slope clamp for Exponential:

```csharp
// Blend: road elevation × w + original terrain × (1 - w)
var blendedH = ribbonZ * w + originalHeightMap[y, x] * (1f - w);

// Enforce max side slope constraint — only for non-Exponential modes.
// Exponential curve shape is controlled entirely by FalloffExponent.
if (sp.BlendFunction != BlendFunctionType.Exponential)
{
    blendedH = EnforceSideMaxSlope(
        ribbonZ, originalHeightMap[y, x], blendedH,
        dist, doi, sp.MaxSlopeDeg);
}
```

- [ ] **Step 3: Update early-rejection cutoff to respect blend mode**

The global `maxDoi` calculation (lines 54-75) currently uses `minSlopeDeg` from ALL splines. Update it to only consider slope-based extension for non-Exponential splines:

```csharp
// Global early-rejection cutoff.
var maxUserDoi = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);
var maxDoi = maxUserDoi;

// Only non-Exponential splines need slope-based DOI extension
var nonExpSplines = network.Splines
    .Where(s => s.Parameters.BlendFunctionType != BlendFunctionType.Exponential)
    .ToList();

if (nonExpSplines.Count > 0)
{
    var minSlopeDeg = nonExpSplines.Min(s => s.Parameters.SideMaxSlopeDegrees);
    if (minSlopeDeg > 0.1f && minSlopeDeg < 89f)
    {
        var minElev = float.MaxValue;
        var maxElev = float.MinValue;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var h = originalHeightMap[y, x];
            if (h < minElev) minElev = h;
            if (h > maxElev) maxElev = h;
        }
        var elevRange = maxElev - minElev;
        var maxSlopeDist = elevRange / MathF.Tan(minSlopeDeg * MathF.PI / 180f);
        maxDoi = MathF.Max(maxUserDoi, MathF.Min(maxSlopeDist, 500f));
    }
}
```

- [ ] **Step 4: Build and verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs
git commit -m "fix: decouple blend parameters — Exponential skips slope constraint"
```

---

### Task 2: UI — Conditional parameter visibility

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor:406-446`

- [ ] **Step 1: Make SideMaxSlopeDegrees visible only for non-Exponential**

Change the `SideMaxSlopeDegrees` field (lines 406-416) to use `display:none` when Exponential is selected (same pattern as FalloffExponent already uses for the inverse):

```razor
<MudItem xs="12" sm="6" Style="@(Material.BlendFunctionType != BlendFunctionType.Exponential ? "" : "display:none")">
    <div class="d-flex align-start gap-1">
        <MudNumericField @bind-Value="Material.SideMaxSlopeDegrees"
                         Label="Max Side Slope (°)"
                         Variant="Variant.Outlined"
                         Min="0.0f" Max="90.0f" Step="1.0f"
                         HelperText="Maximum embankment slope (non-exponential modes)"
                         Class="flex-grow-1" />
        <HelpAdornment TooltipText="@SideMaxSlopeDegrees" />
    </div>
</MudItem>
```

- [ ] **Step 2: Build and verify compilation**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor
git commit -m "ui: hide SideMaxSlopeDegrees when Exponential blend selected"
```

---

### Task 3: Update tooltips and doc comments

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs:203-260`
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs:143-170`

- [ ] **Step 1: Update SideMaxSlopeDegrees tooltip**

In `RoadParameterTooltips.cs`, update the `SideMaxSlopeDegrees` tooltip to clarify it only applies to non-Exponential blend modes:

```csharp
public const string SideMaxSlopeDegrees = """
                                          Default: 45.0 | Range: 0.0 to 90.0
                                          Status: ACTIVELY USED (non-Exponential blend modes only)

                                          Maximum embankment (road shoulder) slope angle.
                                          Controls how sharply terrain transitions from road edge to natural terrain.

                                          Only applies when blend function is Linear, Cosine, Cubic, or Quintic.
                                          When Exponential is selected, the FalloffExponent controls the transition shape instead.

                                          Also auto-extends the blend zone (DOI) when the elevation difference
                                          between road and terrain requires more distance to satisfy this slope limit.

                                          Typical values:
                                          - 20-25° Gentle embankment (1:2.5 ratio)
                                          - 30° Standard embankment (1:1.7 ratio)
                                          - 35-40° Steep embankment (1:1.2 ratio)
                                          """;
```

- [ ] **Step 2: Update FalloffExponent tooltip**

In `RoadParameterTooltips.cs`, update the `FalloffExponentTooltip` to mention it fully controls the transition:

Find the existing tooltip and add a line clarifying that no slope constraint is applied — the exponent alone shapes the curve.

- [ ] **Step 3: Update RoadSmoothingParameters doc comments**

In `RoadSmoothingParameters.cs`, update the XML doc on `SideMaxSlopeDegrees` (line 143) to note it's only used for non-Exponential modes, and `FalloffExponent` (line 164) to note it fully controls the transition with no slope override.

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs
git commit -m "docs: clarify blend mode parameter separation in tooltips and comments"
```

---

### Task 4: Verify no stale references in validation

**Files:**
- Review: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs:360-431` (Validate method)
- Review: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs:144-270` (GetValidationWarnings)

- [ ] **Step 1: Check Validate() in RoadSmoothingParameters**

The existing validation at line 383 (`SideMaxSlopeDegrees < 0 || > 90`) should remain — it's valid regardless of blend mode since the property still exists and presets may switch modes. No change needed.

- [ ] **Step 2: Check GetValidationWarnings() in TerrainMaterialSettings.razor.cs**

Verify no warnings reference SideMaxSlopeDegrees in a way that's misleading for Exponential mode. If any do, add a blend-mode condition.

- [ ] **Step 3: Build full solution**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj /p:EnableWindowsTargeting=true`
Expected: Build succeeded

- [ ] **Step 4: Commit (if changes needed)**

Only if validation code needed updating.
