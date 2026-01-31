# Implementation Guide: Adding CopyForestBrushes as a Wizard Step

This guide provides a comprehensive walkthrough for implementing `CopyForestBrushes.razor` as **Step 4** in the Create Level Wizard, executed after the terrain materials copy step.

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Current Wizard Flow](#current-wizard-flow)
3. [Implementation Checklist](#implementation-checklist)
4. [Step 1: Update CreateLevelWizardState](#step-1-update-createlevelwizardstate)
5. [Step 2: Update CreateLevelAssistant Component](#step-2-update-createlevelassistant-component)
6. [Step 3: Update CopyTerrains.razor](#step-3-update-copyterrainsrazor)
7. [Step 4: Update CopyForestBrushes.razor](#step-4-update-copyforestbrushesrazor)
8. [Step 5: Update CopyForestBrushes.razor.cs](#step-5-update-copyforestbrushesrazorcs)
9. [Step 6: Update CreateLevel.razor](#step-6-update-createlevelrazor)
10. [Testing Checklist](#testing-checklist)
11. [Key Patterns to Follow](#key-patterns-to-follow)

---

## Architecture Overview

### Core Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `CreateLevel.razor(.cs)` | `BlazorUI/Pages/` | Main wizard orchestrator, holds static wizard state |
| `CreateLevelWizardState` | `Objects/` | State object tracking wizard progress |
| `CreateLevelAssistant.razor` | `BlazorUI/Components/` | Bottom navigation panel showing progress |
| `CopyTerrains.razor(.cs)` | `BlazorUI/Pages/` | Step 3: Terrain material selection |
| `CopyForestBrushes.razor(.cs)` | `BlazorUI/Pages/` | Step 4 (to implement): Forest brush selection |

### State Management Pattern

```
CreateLevel.razor
    ??? static CreateLevelWizardState _wizardState
           ??? Accessed via CreateLevel.GetWizardState() from other pages
           ??? Passed as CascadingValue to CreateLevelAssistant
```

### Navigation Pattern

```
Page Navigation uses query parameter: ?wizardMode=true
    ??? Each page checks WizardMode parameter
           ??? If true: Load state from CreateLevel.GetWizardState()
           ??? Auto-load source/target from wizard state
```

---

## Current Wizard Flow

```
Step 0: Setup (CreateLevel.razor)
    ??? Select source map
    ??? Configure level name, path, terrain size
    ??? Click "Initialize New Level"
    
Step 1: MissionGroups (CreateLevel.razor)
    ??? Automatic - copies essential scene objects
    ??? Sets Step1_SetupComplete = true
    ??? Sets Step2_MissionGroupsCopied = true

Step 2: Terrain Materials (CopyTerrains.razor?wizardMode=true)
    ??? Select materials from source
    ??? Copy to target
    ??? Sets Step3_TerrainMaterialsSelected = true
    ??? Can select additional sources
    ??? "Finish Wizard" returns to CreateLevel

[NEW] Step 3: Forest Brushes (CopyForestBrushes.razor?wizardMode=true)
    ??? Select brushes from source (initially same as terrain source)
    ??? Copy to target
    ??? Sets Step4_ForestBrushesSelected = true
    ??? Can select additional sources
    ??? "Finish" completes wizard
```

---

## Implementation Checklist

- [ ] Update `CreateLevelWizardState` - Add Step 4 properties
- [ ] Update `CreateLevelAssistant.razor` - Add Step 4 chip and navigation
- [ ] Update `CopyTerrains.razor(.cs)` - Redirect to forest brushes instead of finish
- [ ] Update `CopyForestBrushes.razor` - Add wizard mode UI (partially done)
- [ ] Update `CopyForestBrushes.razor.cs` - Add wizard state update logic
- [ ] Update `CreateLevel.razor` - Display Step 4 completion status

---

## Step 1: Update CreateLevelWizardState

**File:** `BeamNG_LevelCleanUp\Objects\CreateLevelWizardState.cs`

### Add New Properties

```csharp
/// <summary>
///     Forest brushes copied to the new level
/// </summary>
public List<string> CopiedForestBrushes { get; set; } = new();

/// <summary>
///     Step 4: Forest brushes selected and copied
/// </summary>
public bool Step4_ForestBrushesSelected { get; set; }
```

### Update Reset() Method

```csharp
public void Reset()
{
    // ... existing resets ...
    Step3_TerrainMaterialsSelected = false;
    Step4_ForestBrushesSelected = false;  // ADD THIS
    CopiedForestBrushes = new List<string>();  // ADD THIS
}
```

### Update IsStepComplete() Method

```csharp
public bool IsStepComplete(int stepIndex)
{
    return stepIndex switch
    {
        0 => Step1_SetupComplete,
        1 => Step2_MissionGroupsCopied,
        2 => Step3_TerrainMaterialsSelected,
        3 => Step4_ForestBrushesSelected,  // ADD THIS
        _ => false
    };
}
```

### Update GetProgressSummary() Method

```csharp
public string GetProgressSummary()
{
    var completedSteps = new[] 
    { 
        Step1_SetupComplete, 
        Step2_MissionGroupsCopied, 
        Step3_TerrainMaterialsSelected,
        Step4_ForestBrushesSelected  // ADD THIS
    }.Count(x => x);
    return $"{completedSteps}/4 steps completed";  // CHANGE 3 to 4
}
```

---

## Step 2: Update CreateLevelAssistant Component

**File:** `BeamNG_LevelCleanUp\BlazorUI\Components\CreateLevelAssistant.razor`

### Add Step 4 Chip

In the `MudStack` containing chips, add:

```razor
<MudStack Row="true" Spacing="4">
    <MudChip T="string" Color="@GetStepColor(0)" Icon="@Icons.Material.Filled.Settings">Setup</MudChip>
    <MudChip T="string" Color="@GetStepColor(1)" Icon="@Icons.Material.Filled.Layers">MissionGroups</MudChip>
    <MudChip T="string" Color="@GetStepColor(2)" Icon="@Icons.Material.Filled.Terrain">Terrain Materials</MudChip>
    <MudChip T="string" Color="@GetStepColor(3)" Icon="@Icons.Material.Filled.Forest">Forest Brushes</MudChip> @* ADD THIS *@
</MudStack>
```

### Add Step 4 Status Display

```razor
else if (WizardState.CurrentStep == 3)
{
    @if (WizardState.Step4_ForestBrushesSelected)
    {
        <MudText Typo="Typo.body2">
            @WizardState.CopiedForestBrushes.Count brush(es) selected
        </MudText>
    }
    else
    {
        <MudText Typo="Typo.body2">
            Ready to select forest brushes
        </MudText>
    }
}
```

### Update NextStep() Method

```csharp
private void NextStep()
{
    if (WizardState.CurrentStep == 0 && WizardState.Step1_SetupComplete)
    {
        Navigation.NavigateTo("/CreateLevel");
        WizardState.CurrentStep = 1;
    }
    else if (WizardState.CurrentStep == 1 && WizardState.Step2_MissionGroupsCopied)
    {
        Navigation.NavigateTo("/CopyTerrains?wizardMode=true");
        WizardState.CurrentStep = 2;
    }
    else if (WizardState.CurrentStep == 2 && WizardState.Step3_TerrainMaterialsSelected)
    {
        // CHANGE: Navigate to forest brushes instead of back to CreateLevel
        Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
        WizardState.CurrentStep = 3;
    }
    else if (WizardState.CurrentStep == 3 && WizardState.Step4_ForestBrushesSelected)
    {
        Navigation.NavigateTo("/CreateLevel");
    }
}
```

### Update PreviousStep() Method

```csharp
private void PreviousStep()
{
    if (WizardState.CurrentStep == 1)
    {
        Navigation.NavigateTo("/CreateLevel");
    }
    else if (WizardState.CurrentStep == 2)
    {
        Navigation.NavigateTo("/CreateLevel");
    }
    else if (WizardState.CurrentStep == 3)  // ADD THIS
    {
        Navigation.NavigateTo("/CopyTerrains?wizardMode=true");
    }
    
    WizardState.CurrentStep = Math.Max(0, WizardState.CurrentStep - 1);
}
```

### Update CanProceedToNextStep()

```csharp
private bool CanProceedToNextStep()
{
    return WizardState.CurrentStep switch
    {
        0 => WizardState.Step1_SetupComplete,
        1 => WizardState.Step2_MissionGroupsCopied,
        2 => WizardState.Step3_TerrainMaterialsSelected,
        3 => WizardState.Step4_ForestBrushesSelected,  // ADD THIS
        _ => false
    };
}
```

### Update GetNextButtonText()

```csharp
private string GetNextButtonText()
{
    return WizardState.CurrentStep switch
    {
        0 => "Review",
        1 => "Select Terrain Materials",
        2 => "Select Forest Brushes",  // CHANGE from "Back to Review"
        3 => "Back to Review",  // ADD THIS
        _ => "Next"
    };
}
```

### Update Finish Button Visibility

```razor
<MudButton OnClick="@FinishWizard"
          Variant="Variant.Filled"
          Color="Color.Success"
          StartIcon="@Icons.Material.Filled.Check"
          Style="@(WizardState.Step4_ForestBrushesSelected ? "" : "display:none")"> @* CHANGE condition *@
    Finish
</MudButton>
```

---

## Step 3: Update CopyTerrains.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyTerrains.razor`

### Update Wizard Info Text (line ~34)

Change the text about next steps after copy completion:

```razor
<MudText Typo="Typo.body2" Class="mt-2">
    @if (_copyCompleted)
    {
        <text>Materials copied! You can select another source map or continue to forest brushes.</text>
    }
    else
    {
        <text>Select the terrain materials you want to copy to your new level.</text>
    }
</MudText>
```

### Update Success Alert Text

```razor
<MudText Typo="Typo.body2" Class="mt-2">
    You can select another source map to copy additional materials, or continue to forest brushes.
</MudText>
```

---

## Step 4: Update CopyTerrains.razor.cs

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyTerrains.razor.cs`

### Update FinishWizard() Method

Change to navigate to forest brushes instead of CreateLevel:

```csharp
/// <summary>
///     Navigates to forest brushes step (next step after terrain materials)
/// </summary>
private void FinishWizard()
{
    // Clear wizard terrain size
    PathResolver.WizardTerrainSize = null;

    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Terrain materials step completed! Copied {_totalCopiedMaterialsCount} material(s). Proceeding to forest brushes.");

    // Navigate to forest brushes step instead of finishing
    Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
}
```

---

## Step 5: Update CopyForestBrushes.razor.cs

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyForestBrushes.razor.cs`

The code-behind already has most wizard functionality. Add/update these methods:

### Add UpdateWizardStateAfterCopy() Method

```csharp
/// <summary>
/// Updates wizard state after copying forest brushes
/// </summary>
private void UpdateWizardStateAfterCopy()
{
    if (WizardState == null) return;

    // Collect copied brush names
    var copiedBrushNames = _selectedItems
        .Where(x => x.CopyAsset?.CopyAssetType == CopyAssetType.ForestBrush)
        .Select(x => x.FullName)
        .ToList();

    // Accumulate brushes (allow multiple copy operations)
    WizardState.CopiedForestBrushes.AddRange(copiedBrushNames);
    WizardState.Step4_ForestBrushesSelected = true;
    WizardState.CurrentStep = 3;

    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Copied {copiedBrushNames.Count} forest brush(es) to {WizardState.LevelName}");
}
```

### Update CopyDialogWizardMode() Method

Ensure it calls `UpdateWizardStateAfterCopy()` after successful copy:

```csharp
private async Task CopyDialogWizardMode()
{
    var options = new DialogOptions { CloseOnEscapeKey = true };
    var parameters = new DialogParameters();

    var messageText = $"Copy {_selectedItems.Count} forest brush(es) to {WizardState.LevelName}?";

    parameters.Add("ContentText", messageText);
    parameters.Add("ButtonText", "Copy Brushes");
    parameters.Add("Color", Color.Primary);

    var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Forest Brushes", parameters, options);
    var result = await dialog.Result;

    if (!result.Canceled)
    {
        _staticSnackbar = Snackbar.Add("Copying forest brushes...", Severity.Normal,
            config => { config.VisibleStateDuration = int.MaxValue; });

        var copyCount = _selectedItems.Count;
        var sourceName = _levelNameCopyFrom;

        await Task.Run(() =>
        {
            var selected = _selectedItems.Select(y => y.Identifier).ToList();
            Reader.DoCopyAssets(selected);
        });

        Snackbar.Remove(_staticSnackbar);

        // UPDATE WIZARD STATE
        UpdateWizardStateAfterCopy();

        _copyCompleted = true;
        _copiedBrushesCount = copyCount;
        _totalCopiedBrushesCount += copyCount;
        _lastCopiedSourceName = sourceName;

        StateHasChanged();
    }
}
```

### Update FinishWizard() Method

```csharp
/// <summary>
/// Finishes the wizard and navigates back to CreateLevel
/// </summary>
private void FinishWizard()
{
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Wizard completed! Copied {_totalCopiedBrushesCount} forest brush(es) to {WizardState?.LevelName}.");

    // Ensure state is marked complete
    if (WizardState != null)
    {
        WizardState.Step4_ForestBrushesSelected = true;
    }

    Navigation.NavigateTo("/CreateLevel");
}
```

### Update CancelWizard() to Go Back to Terrains

```csharp
private void CancelWizard()
{
    // Navigate back to terrain materials (previous step)
    Navigation.NavigateTo("/CopyTerrains?wizardMode=true");
}
```

---

## Step 6: Update CreateLevel.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CreateLevel.razor`

### Add Step 4 Completion Display

After the Step 3 completion section (`@if (_wizardState.Step3_TerrainMaterialsSelected)`), add:

```razor
@if (_wizardState.Step4_ForestBrushesSelected)
{
    <MudPaper Class="pa-4 mt-4" Elevation="2">
        <MudText Typo="Typo.h6" Class="mb-4">
            <MudIcon Icon="@Icons.Material.Filled.Forest" Color="Color.Success" Class="mr-2"/>
            Forest Brushes Copied
        </MudText>
        
        <MudList T="string" Dense="true">
            @foreach(var brush in _wizardState.CopiedForestBrushes)
            {
                <MudListItem T="string" Icon="@Icons.Material.Filled.Grass">
                    @brush
                </MudListItem>
            }
        </MudList>
        
        <MudDivider Class="my-4"/>
        
        <MudAlert Severity="Severity.Success" Class="mb-4">
            <b>Level Creation Complete!</b> Your level is ready with terrain materials and forest brushes.
        </MudAlert>
        
        <MudStack Row="true" Class="mt-4">
            <MudButton OnClick="@ZipAndDeploy" 
                      Color="Color.Primary" 
                      Variant="Variant.Filled"
                      StartIcon="@Icons.Material.Filled.Archive">
                Build Deployment ZIP
            </MudButton>
            
            <MudButton OnClick="@CopyToLevelsFolder" 
                      Color="Color.Success" 
                      Variant="Variant.Filled"
                      StartIcon="@Icons.Material.Filled.Folder">
                Copy to BeamNG Levels Folder
            </MudButton>
            
            <MudButton OnClick="@ResetWizard" 
                      Color="Color.Default" 
                      Variant="Variant.Outlined"
                      StartIcon="@Icons.Material.Filled.Refresh">
                Create Another Level
            </MudButton>
        </MudStack>
    </MudPaper>
}
```

### Update the Completion Display Logic

Change the Step 3 completion section to only show if Step 4 is NOT completed:

```razor
@if (_wizardState.Step3_TerrainMaterialsSelected && !_wizardState.Step4_ForestBrushesSelected)
{
    // Existing terrain materials completion display
    // Update the alert text:
    <MudAlert Severity="Severity.Info" Class="mb-4">
        <b>Next Step:</b> Select forest brushes for your level. Click "Select Forest Brushes" in the wizard below.
    </MudAlert>
    // ... rest unchanged ...
}
```

---

## Testing Checklist

### Full Wizard Flow Test

1. [ ] Start Create Level wizard
2. [ ] Select source map and configure level
3. [ ] Click "Initialize New Level"
4. [ ] Verify MissionGroups copied
5. [ ] Click "Select Terrain Materials" 
6. [ ] Verify CopyTerrains loads with wizard mode
7. [ ] Select and copy terrain materials
8. [ ] Click "Select Forest Brushes" (was "Finish Wizard")
9. [ ] Verify CopyForestBrushes loads with wizard mode
10. [ ] Verify source/target auto-populated from wizard state
11. [ ] Select and copy forest brushes
12. [ ] Verify "Select Another Source Map" works
13. [ ] Click "Finish"
14. [ ] Verify return to CreateLevel with all steps complete

### State Persistence Tests

1. [ ] Verify wizard state persists across page navigations
2. [ ] Verify brush count accumulates with multiple copy operations
3. [ ] Verify Back button returns to terrain materials
4. [ ] Verify Reset wizard clears all state including Step 4

### Standalone Mode Tests

1. [ ] Verify CopyForestBrushes still works without wizard mode
2. [ ] Verify no wizard UI elements appear in standalone mode
3. [ ] Verify file selection works normally

---

## Key Patterns to Follow

### Pattern 1: Wizard Mode Parameter

```csharp
[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }
```

### Pattern 2: State Access

```csharp
protected override async Task OnParametersSetAsync()
{
    if (WizardMode)
    {
        WizardState = CreateLevel.GetWizardState();
        if (WizardState == null || !WizardState.IsActive)
        {
            // Handle invalid state
            return;
        }
        await LoadLevelsFromWizardState();
    }
}
```

### Pattern 3: Different UI for Wizard Mode

```razor
@if (WizardMode && WizardState != null)
{
    <!-- Wizard-specific UI -->
}
else
{
    <!-- Standard UI -->
}
```

### Pattern 4: Footer Buttons

```razor
@if (WizardMode)
{
    <MudStack Row="true" Justify="Justify.SpaceBetween">
        @if (_copyCompleted)
        {
            <MudButton OnClick="@ResetSourceMapWizardMode">Select Another Source</MudButton>
            <MudButton OnClick="@FinishWizard">Finish</MudButton>
        }
        else
        {
            <MudButton OnClick="@CancelWizard">Back</MudButton>
            <MudButton OnClick="@CopyDialogWizardMode">Copy Selected</MudButton>
        }
    </MudStack>
}
```

### Pattern 5: CascadingValue for Assistant

```razor
@if (WizardMode && WizardState != null)
{
    <CascadingValue Value="WizardState">
        <CreateLevelAssistant />
    </CascadingValue>
}
```

---

## Notes

- **DO NOT** modify standalone (non-wizard) functionality
- The existing `CopyForestBrushes` wizard UI is already partially implemented - enhance it
- Forest brushes step is optional - users can skip by clicking Finish early (update UI to allow this)
- Test thoroughly after each change before proceeding
