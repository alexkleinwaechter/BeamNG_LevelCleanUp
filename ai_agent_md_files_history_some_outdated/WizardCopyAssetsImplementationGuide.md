# Implementation Guide: Adding CopyAssets as a Wizard Step

This guide provides a comprehensive walkthrough for implementing `CopyAssets.razor` as **Step 5** in the Create Level Wizard, executed after the forest brushes copy step.

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Current Wizard Flow](#current-wizard-flow)
3. [Implementation Checklist](#implementation-checklist)
4. [Step 1: Update CreateLevelWizardState](#step-1-update-createlevelwizardstate)
5. [Step 2: Update CreateLevelAssistant Component](#step-2-update-createlevelassistant-component)
6. [Step 3: Update CopyForestBrushes.razor.cs](#step-3-update-copyforestbrushesrazorcs)
7. [Step 4: Update CopyAssets.razor](#step-4-update-copyassetsrazor)
8. [Step 5: Update CopyAssets.razor.cs](#step-5-update-copyassetsrazorcs)
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
| `CopyForestBrushes.razor(.cs)` | `BlazorUI/Pages/` | Step 4: Forest brush selection |
| `CopyAssets.razor(.cs)` | `BlazorUI/Pages/` | Step 5 (to implement): General asset selection |

### State Management Pattern

```
CreateLevel.razor
    ?? static CreateLevelWizardState _wizardState
           ?? Accessed via CreateLevel.GetWizardState() from other pages
           ?? Passed as CascadingValue to CreateLevelAssistant
```

### Navigation Pattern

```
Page Navigation uses query parameter: ?wizardMode=true
    ?? Each page checks WizardMode parameter
           ?? If true: Load state from CreateLevel.GetWizardState()
           ?? Auto-load source/target from wizard state
```

---

## Current Wizard Flow

```
Step 0: Setup (CreateLevel.razor)
    ?? Select source map
    ?? Configure level name, path, terrain size
    ?? Click "Initialize New Level"
    
Step 1: MissionGroups (CreateLevel.razor)
    ?? Automatic - copies essential scene objects
    ?? Sets Step1_SetupComplete = true
    ?? Sets Step2_MissionGroupsCopied = true

Step 2: Terrain Materials (CopyTerrains.razor?wizardMode=true)
    ?? Select materials from source
    ?? Copy to target
    ?? Sets Step3_TerrainMaterialsSelected = true
    ?? Can select additional sources
    ?? "Continue" proceeds to forest brushes

Step 3: Forest Brushes (CopyForestBrushes.razor?wizardMode=true)
    ?? Select brushes from source
    ?? Copy to target
    ?? Sets Step4_ForestBrushesSelected = true
    ?? Can select additional sources
    ?? "Continue" proceeds to assets

[NEW] Step 4: Copy Assets (CopyAssets.razor?wizardMode=true)
    ?? Select assets (decalroads, decals, DAE models) from source
    ?? Copy to target
    ?? Sets Step5_AssetsSelected = true
    ?? Can select additional sources
    ?? "Finish" completes wizard
```

---

## Implementation Checklist

- [ ] Update `CreateLevelWizardState` - Add Step 5 properties
- [ ] Update `CreateLevelAssistant.razor` - Add Step 5 chip and navigation
- [ ] Update `CopyForestBrushes.razor.cs` - Redirect to assets instead of finish
- [ ] Update `CopyAssets.razor` - Add wizard mode UI
- [ ] Update `CopyAssets.razor.cs` - Add wizard state handling and update logic
- [ ] Update `CreateLevel.razor` - Display Step 5 completion status

---

## Step 1: Update CreateLevelWizardState

**File:** `BeamNG_LevelCleanUp\Objects\CreateLevelWizardState.cs`

### Add New Properties

```csharp
/// <summary>
///     Assets copied to the new level (decalroads, decals, DAE files)
/// </summary>
public List<string> CopiedAssets { get; set; } = new();

/// <summary>
///     Step 5: Assets selected and copied
/// </summary>
public bool Step5_AssetsSelected { get; set; }
```

### Update Reset() Method

```csharp
public void Reset()
{
    // ... existing resets ...
    Step4_ForestBrushesSelected = false;
    Step5_AssetsSelected = false;  // ADD THIS
    CopiedAssets = new List<string>();  // ADD THIS
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
        3 => Step4_ForestBrushesSelected,
        4 => Step5_AssetsSelected,  // ADD THIS
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
        Step4_ForestBrushesSelected,
        Step5_AssetsSelected  // ADD THIS
    }.Count(x => x);
    return $"{completedSteps}/5 steps completed";  // CHANGE 4 to 5
}
```

---

## Step 2: Update CreateLevelAssistant Component

**File:** `BeamNG_LevelCleanUp\BlazorUI\Components\CreateLevelAssistant.razor`

### Add Step 5 Chip

In the `MudStack` containing chips, add:

```razor
<MudStack Row="true" Spacing="4">
    <MudChip T="string" Color="@GetStepColor(0)" Icon="@Icons.Material.Filled.Settings">Setup</MudChip>
    <MudChip T="string" Color="@GetStepColor(1)" Icon="@Icons.Material.Filled.Layers">MissionGroups</MudChip>
    <MudChip T="string" Color="@GetStepColor(2)" Icon="@Icons.Material.Filled.Terrain">Terrain Materials</MudChip>
    <MudChip T="string" Color="@GetStepColor(3)" Icon="@Icons.Material.Filled.Forest">Forest Brushes</MudChip>
    <MudChip T="string" Color="@GetStepColor(4)" Icon="@Icons.Material.Filled.ContentCopy">Assets</MudChip> @* ADD THIS *@
</MudStack>
```

### Add Step 5 Status Display

In the status display section, add after the `CurrentStep == 3` block:

```razor
else if (WizardState.CurrentStep == 4)
{
    @if (WizardState.Step5_AssetsSelected)
    {
        <MudText Typo="Typo.body2">
            @WizardState.CopiedAssets.Count asset(s) copied
        </MudText>
    }
    else
    {
        <MudText Typo="Typo.body2">
            Ready to select assets (decalroads, decals, models)
        </MudText>
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
    else if (WizardState.CurrentStep == 3)
    {
        Navigation.NavigateTo("/CopyTerrains?wizardMode=true");
    }
    else if (WizardState.CurrentStep == 4)  // ADD THIS
    {
        Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
    }
    
    WizardState.CurrentStep = Math.Max(0, WizardState.CurrentStep - 1);
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
        Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
        WizardState.CurrentStep = 3;
    }
    else if (WizardState.CurrentStep == 3 && WizardState.Step4_ForestBrushesSelected)
    {
        // CHANGE: Navigate to assets instead of CreateLevel
        Navigation.NavigateTo("/CopyAssets?wizardMode=true");
        WizardState.CurrentStep = 4;
    }
    else if (WizardState.CurrentStep == 4 && WizardState.Step5_AssetsSelected)  // ADD THIS
    {
        Navigation.NavigateTo("/CreateLevel");
    }
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
        3 => WizardState.Step4_ForestBrushesSelected,
        4 => WizardState.Step5_AssetsSelected,  // ADD THIS
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
        2 => "Select Forest Brushes",
        3 => "Select Assets",  // CHANGE from "Back to Review"
        4 => "Back to Review",  // ADD THIS
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
          Style="@(WizardState.Step5_AssetsSelected ? "" : "display:none")"> @* CHANGE condition *@
    Finish
</MudButton>
```

---

## Step 3: Update CopyForestBrushes.razor.cs

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyForestBrushes.razor.cs`

### Update FinishWizard() Method

Change to navigate to CopyAssets instead of CreateLevel:

```csharp
/// <summary>
///     Navigates to assets step (next step after forest brushes)
/// </summary>
private void FinishWizard()
{
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Forest brushes step completed! Copied {_totalCopiedBrushesCount} brush(es). Proceeding to assets.");

    // Navigate to assets step instead of finishing
    Navigation.NavigateTo("/CopyAssets?wizardMode=true");
}
```

---

## Step 4: Update CopyAssets.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyAssets.razor`

### Add Parameter and State Properties

At the top of the file, add:

```razor
@page "/CopyAssets"
@using System.Diagnostics.CodeAnalysis
@using System.IO.Compression
@using System.Reflection
@using BeamNG_LevelCleanUp.BlazorUI.Components
@using BeamNG_LevelCleanUp.Communication
@using BeamNG_LevelCleanUp.Logic
@using BeamNG_LevelCleanUp.Objects
@using BeamNG_LevelCleanUp.Utils
@inject ISnackbar Snackbar
@inject IDialogService DialogService
@inject NavigationManager Navigation  @* ADD THIS *@
```

### Add Wizard Mode Info Alert

After the expansion panels and before the Reset button, add:

```razor
@* Wizard Mode Info Alert *@
@if (WizardMode && WizardState != null)
{
    <MudAlert Severity="Severity.Info" Class="mt-4 mb-4" Icon="@Icons.Material.Filled.Info">
        <MudText Typo="Typo.h6">
            <MudIcon Icon="@Icons.Material.Filled.AutoAwesome" Class="mr-2"/>
            Wizard Mode: Copy Assets
        </MudText>
        <MudText Typo="Typo.body2">
            <b>Target Level:</b> @WizardState.LevelName
        </MudText>
        <MudText Typo="Typo.body2">
            <b>Source Level:</b> @_levelNameCopyFrom
        </MudText>
        <MudText Typo="Typo.body2" Class="mt-2">
            @if (_copyCompleted)
            {
                <text>Assets copied! You can select another source map or finish the wizard.</text>
            }
            else
            {
                <text>Select the assets (decalroads, decals, 3D models) you want to copy to your new level.</text>
            }
        </MudText>
    </MudAlert>
}
```

### Update Copy Completed Alert for Wizard Mode

Replace the existing `_copyCompleted` alert section with conditional rendering:

```razor
@if (_copyCompleted)
{
    <MudAlert Severity="Severity.Success" Class="mt-4 mb-4" Icon="@Icons.Material.Filled.CheckCircle" ShowCloseIcon="true" CloseIconClicked="@(() => _copyCompleted = false)">
        <MudText Typo="Typo.h6">Copy Completed Successfully!</MudText>
        <MudText Typo="Typo.body2">
            Copied <b>@_copiedAssetsCount</b> asset(s) from <b>@_lastCopiedSourceName</b> to <b>@_levelName</b>.
        </MudText>
        @if (WizardMode && WizardState != null)
        {
            <MudText Typo="Typo.body2" Class="mt-2">
                You can select another source map to copy additional assets, or click "Finish" to complete the wizard.
            </MudText>
            <MudStack Row="true" Spacing="2" Class="mt-3">
                <MudButton @onclick="ResetSourceMapWizardMode" 
                          Color="Color.Primary" 
                          Variant="Variant.Filled"
                          StartIcon="@Icons.Material.Filled.AddCircle">
                    Select Another Source Map
                </MudButton>
                <MudButton @onclick="FinishWizard" 
                          Color="Color.Success" 
                          Variant="Variant.Filled"
                          StartIcon="@Icons.Material.Filled.Check">
                    Finish Wizard
                </MudButton>
            </MudStack>
        }
        else
        {
            <MudText Typo="Typo.body2" Class="mt-2">
                You can now select another source map to copy additional assets, or build the zip file.
            </MudText>
            <MudButton @onclick="ResetSourceMap" 
                      Color="Color.Primary" 
                      Variant="Variant.Filled"
                      StartIcon="@Icons.Material.Filled.AddCircle" 
                      Class="mt-3">
                Select Another Source Map
            </MudButton>
        }
    </MudAlert>
}
```

### Update Footer for Wizard Mode

Replace the footer section with conditional rendering:

```razor
<footer>
    @if (_selectedItems.Any())
    {
        <MudText>Files: @BindingListCopy?.Count, Selected: @_selectedItems.Count(), Sum Size MB: @Math.Round(_selectedItems.Sum(x => x.SizeMb), 2)</MudText>
    }
    <MudStack Row="true" Justify="Justify.SpaceBetween">
        @if (WizardMode && WizardState != null)
        {
            @* Wizard Mode Footer *@
            @if (_copyCompleted)
            {
                <MudButton @onclick="ResetSourceMapWizardMode" 
                          Color="Color.Primary" 
                          Variant="Variant.Outlined"
                          StartIcon="@Icons.Material.Filled.AddCircle">
                    Select Another Source
                </MudButton>
                <MudButton @onclick="FinishWizard" 
                          Color="Color.Success" 
                          Variant="Variant.Filled"
                          StartIcon="@Icons.Material.Filled.Check">
                    Finish
                </MudButton>
            }
            else
            {
                <MudButton @onclick="CancelWizard" 
                          Color="Color.Default" 
                          Variant="Variant.Outlined"
                          StartIcon="@Icons.Material.Filled.ArrowBack">
                    Back to Forest Brushes
                </MudButton>
                @if (_selectedItems.Any())
                {
                    <MudButton @onclick="CopyDialogWizardMode" 
                              Color="Color.Primary" 
                              Variant="Variant.Filled"
                              StartIcon="@Icons.Material.Filled.ContentCopy">
                        Copy @_selectedItems.Count Asset(s)
                    </MudButton>
                }
                else
                {
                    <MudButton @onclick="SkipStep" 
                              Color="Color.Default" 
                              Variant="Variant.Outlined"
                              StartIcon="@Icons.Material.Filled.SkipNext">
                        Skip (No Assets)
                    </MudButton>
                    <MudButton @onclick="FinishWizard" 
                              Color="Color.Success" 
                              Variant="Variant.Filled"
                              StartIcon="@Icons.Material.Filled.Check"
                              Disabled="@(!WizardState.Step4_ForestBrushesSelected)">
                        Finish Without Assets
                    </MudButton>
                }
            }
        }
        else
        {
            @* Standard Mode Footer *@
            @if (_selectedItems.Any())
            {
                <MudButton @onclick="CopyDialog" Color="Color.Primary">Copy Assets</MudButton>
            }
            @if (_errors.Any())
            {
                <MudButton Color="Color.Error" OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Error))">Errors</MudButton>
            }
            @if (_warnings.Any())
            {
                <MudButton Color="Color.Warning" OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Warning))">Warnings</MudButton>
            }
            @if (_messages.Any())
            {
                <MudButton Color="Color.Info" OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Info))">Messages</MudButton>
            }
            @if (_showDeployButton)
            {
                <MudSelect Dense T="CompressionLevel" Label="Compression Level" AnchorOrigin="Origin.TopCenter"
                           @bind-Value="_compressionLevel">
                    <MudSelectItem T="CompressionLevel" Value="CompressionLevel.Fastest"/>
                    <MudSelectItem T="CompressionLevel" Value="CompressionLevel.NoCompression"/>
                    <MudSelectItem T="CompressionLevel" Value="CompressionLevel.Optimal"/>
                    <MudSelectItem T="CompressionLevel" Value="CompressionLevel.SmallestSize"/>
                </MudSelect>
                <MudButton @onclick="ZipAndDeploy" Color="Color.Primary">Build Zipfile</MudButton>
            }
        }
    </MudStack>
    @if (!string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory))
    {
        <MudButton @onclick="ZipFileHandler.OpenExplorer" StartIcon="@Icons.Material.Filled.FolderOpen" Variant="Variant.Text" Color="Color.Primary">Working Directory: @ZipFileHandler.WorkingDirectory</MudButton>
        <MudButton @onclick="ZipFileHandler.OpenExplorerLogs" StartIcon="@Icons.Material.Filled.FolderOpen" Variant="Variant.Text" Color="Color.Primary">Logfiles</MudButton>
    }
</footer>

@* Add Wizard Assistant at the bottom *@
@if (WizardMode && WizardState != null)
{
    <CascadingValue Value="WizardState">
        <CreateLevelAssistant />
    </CascadingValue>
}
```

---

## Step 5: Update CopyAssets.razor.cs

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyAssets.razor.cs`

### Add Wizard-Related Fields and Properties

Add these at the top of the class:

```csharp
// Wizard mode properties
[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }

/// <summary>
///     Wizard state reference when in wizard mode
/// </summary>
public CreateLevelWizardState WizardState { get; private set; }

// Wizard tracking
private int _totalCopiedAssetsCount { get; set; }
```

### Add OnParametersSetAsync Method

```csharp
protected override async Task OnParametersSetAsync()
{
    if (WizardMode)
    {
        WizardState = CreateLevel.GetWizardState();
        if (WizardState == null || !WizardState.IsActive)
        {
            // Invalid wizard state - redirect to CreateLevel
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                "Wizard state not found. Please start the wizard from Create Level page.");
            Navigation.NavigateTo("/CreateLevel");
            return;
        }

        // Set the current wizard step
        WizardState.CurrentStep = 4;

        // Auto-load levels from wizard state
        await LoadLevelsFromWizardState();
    }
}
```

### Add LoadLevelsFromWizardState Method

```csharp
/// <summary>
///     Loads source and target levels from wizard state
/// </summary>
private async Task LoadLevelsFromWizardState()
{
    if (WizardState == null) return;

    try
    {
        // Set target level from wizard state
        _levelPath = WizardState.TargetLevelRootPath;
        _levelName = WizardState.LevelName;

        // Set source level from wizard state
        _levelPathCopyFrom = WizardState.SourceLevelPath;
        _levelNameCopyFrom = WizardState.SourceLevelName;

        // Set working directory
        if (!string.IsNullOrEmpty(_levelPath))
        {
            ZipFileHandler.WorkingDirectory = Path.GetDirectoryName(_levelPath);
            _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;
        }

        // Initialize reader and scan assets if both levels are set
        if (!string.IsNullOrEmpty(_levelPath) && !string.IsNullOrEmpty(_levelPathCopyFrom))
        {
            await ScanAssets();
            
            // Collapse all panels in wizard mode (files already loaded)
            if (FileSelect != null)
            {
                await FileSelect.CollapseAllAsync();
            }
        }

        _showDeployButton = true;
        StateHasChanged();
    }
    catch (Exception ex)
    {
        ShowException(ex);
    }
}
```

### Add UpdateWizardStateAfterCopy Method

```csharp
/// <summary>
///     Updates wizard state after copying assets
/// </summary>
private void UpdateWizardStateAfterCopy()
{
    if (WizardState == null) return;

    // Collect copied asset names
    var copiedAssetNames = _selectedItems
        .Select(x => $"{x.AssetType}: {x.FullName}")
        .ToList();

    // Accumulate assets (allow multiple copy operations)
    WizardState.CopiedAssets.AddRange(copiedAssetNames);
    WizardState.Step5_AssetsSelected = true;
    WizardState.CurrentStep = 4;

    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Copied {copiedAssetNames.Count} asset(s) to {WizardState.LevelName}");
}
```

### Add CopyDialogWizardMode Method

```csharp
/// <summary>
///     Copy dialog for wizard mode with state updates
/// </summary>
private async Task CopyDialogWizardMode()
{
    var options = new DialogOptions { CloseOnEscapeKey = true };
    var parameters = new DialogParameters();

    var messageText = $"Copy {_selectedItems.Count} asset(s) to {WizardState.LevelName}?";

    parameters.Add("ContentText", messageText);
    parameters.Add("ButtonText", "Copy Assets");
    parameters.Add("Color", Color.Primary);

    var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Assets", parameters, options);
    var result = await dialog.Result;

    if (!result.Canceled)
    {
        _staticSnackbar = Snackbar.Add("Copying assets...", Severity.Normal,
            config => { config.VisibleStateDuration = int.MaxValue; });

        var copyCount = _selectedItems.Count;
        var sourceName = _levelNameCopyFrom;

        await Task.Run(() =>
        {
            var selected = _selectedItems.Select(y => y.Identifier).ToList();
            Reader.DoCopyAssets(selected);
        });

        Snackbar.Remove(_staticSnackbar);

        // Handle duplicate materials warning
        var duplicateMaterialsPath = Reader.GetDuplicateMaterialsLogFilePath();
        if (!string.IsNullOrEmpty(duplicateMaterialsPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Duplicate Materials found. See logfile {duplicateMaterialsPath}");
        }

        // UPDATE WIZARD STATE
        UpdateWizardStateAfterCopy();

        _copyCompleted = true;
        _copiedAssetsCount = copyCount;
        _totalCopiedAssetsCount += copyCount;
        _lastCopiedSourceName = sourceName;

        StateHasChanged();
    }
}
```

### Add ResetSourceMapWizardMode Method

```csharp
/// <summary>
///     Resets source map state in wizard mode to allow selecting another source
/// </summary>
private async Task ResetSourceMapWizardMode()
{
    // Reset source-related state while keeping target
    _levelNameCopyFrom = null;
    _levelPathCopyFrom = null;
    _vanillaLevelSourceSelected = null;
    BindingListCopy = new List<GridFileListItem>();
    _selectedItems = new HashSet<GridFileListItem>();
    _copyCompleted = false;
    _searchString = string.Empty;

    // Clear messages but keep target context
    _errors = new List<string>();
    _warnings = new List<string>();

    // Set flag and re-enable selection
    _isSelectingAnotherSource = true;
    _fileSelectDisabled = false;
    _isLoadingMap = false;

    // Expand source panel
    if (FileSelect?.Panels?.Count > 1)
    {
        await FileSelect.CollapseAllAsync();
        await Task.Delay(100);
        await FileSelect.Panels[1].ExpandAsync();
    }

    StateHasChanged();
    Snackbar.Add($"Ready to select another source map. Target: {_levelName}", Severity.Info);
}
```

### Add FinishWizard Method

```csharp
/// <summary>
///     Finishes the wizard and navigates back to CreateLevel
/// </summary>
private void FinishWizard()
{
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Wizard completed! Copied {_totalCopiedAssetsCount} asset(s) to {WizardState?.LevelName}.");

    // Ensure state is marked complete
    if (WizardState != null)
    {
        WizardState.Step5_AssetsSelected = true;
    }

    Navigation.NavigateTo("/CreateLevel");
}
```

### Add CancelWizard Method

```csharp
/// <summary>
///     Cancels current step and navigates back to forest brushes
/// </summary>
private void CancelWizard()
{
    Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
}
```

### Add SkipStep Method

```csharp
/// <summary>
///     Skips the assets step (marks as complete without copying)
/// </summary>
private void SkipStep()
{
    if (WizardState != null)
    {
        WizardState.Step5_AssetsSelected = true;
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Assets step skipped.");
    }
    
    Navigation.NavigateTo("/CreateLevel");
}
```

### Update InitializeVariables Method

Add wizard reset handling:

```csharp
protected void InitializeVariables()
{
    // ... existing code ...
    
    // Reset wizard tracking (but don't reset WizardState itself)
    _totalCopiedAssetsCount = 0;
}
```

---

## Step 6: Update CreateLevel.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CreateLevel.razor`

### Add Step 5 Completion Display

After the Step 4 (forest brushes) completion section, add:

```razor
@if (_wizardState.Step5_AssetsSelected)
{
    <MudPaper Class="pa-4 mt-4" Elevation="2">
        <MudText Typo="Typo.h6" Class="mb-4">
            <MudIcon Icon="@Icons.Material.Filled.ContentCopy" Color="Color.Success" Class="mr-2"/>
            Assets Copied
        </MudText>
        
        @if (_wizardState.CopiedAssets.Any())
        {
            <MudList T="string" Dense="true">
                @foreach(var asset in _wizardState.CopiedAssets.Take(10))
                {
                    <MudListItem T="string" Icon="@Icons.Material.Filled.InsertDriveFile">
                        @asset
                    </MudListItem>
                }
                @if (_wizardState.CopiedAssets.Count > 10)
                {
                    <MudListItem T="string" Icon="@Icons.Material.Filled.MoreHoriz">
                        ... and @(_wizardState.CopiedAssets.Count - 10) more asset(s)
                    </MudListItem>
                }
            </MudList>
        }
        else
        {
            <MudText Typo="Typo.body2" Color="Color.Default">
                No additional assets copied (step was skipped)
            </MudText>
        }
        
        <MudDivider Class="my-4"/>
        
        <MudAlert Severity="Severity.Success" Class="mb-4">
            <b>Level Creation Complete!</b> Your level is ready with terrain materials, forest brushes, and assets.
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

### Update Step 4 Completion Display Logic

Change the Step 4 (forest brushes) completion section to only show if Step 5 is NOT completed:

```razor
@if (_wizardState.Step4_ForestBrushesSelected && !_wizardState.Step5_AssetsSelected)
{
    // Existing forest brushes completion display
    // Update the alert text:
    <MudAlert Severity="Severity.Info" Class="mb-4">
        <b>Next Step:</b> Select additional assets for your level (optional). Click "Select Assets" in the wizard below.
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
8. [ ] Click "Select Forest Brushes"
9. [ ] Verify CopyForestBrushes loads with wizard mode
10. [ ] Select and copy forest brushes
11. [ ] Click "Select Assets"
12. [ ] Verify CopyAssets loads with wizard mode
13. [ ] Verify source/target auto-populated from wizard state
14. [ ] Select and copy assets (decalroads, decals, models)
15. [ ] Verify "Select Another Source Map" works
16. [ ] Click "Finish"
17. [ ] Verify return to CreateLevel with all steps complete

### State Persistence Tests

1. [ ] Verify wizard state persists across page navigations
2. [ ] Verify asset count accumulates with multiple copy operations
3. [ ] Verify Back button returns to forest brushes
4. [ ] Verify Reset wizard clears all state including Step 5

### Skip Functionality Tests

1. [ ] Verify "Skip (No Assets)" works correctly
2. [ ] Verify "Finish Without Assets" works correctly
3. [ ] Verify step is marked complete even when skipped

### Standalone Mode Tests

1. [ ] Verify CopyAssets still works without wizard mode
2. [ ] Verify no wizard UI elements appear in standalone mode
3. [ ] Verify file selection works normally
4. [ ] Verify "Build Zipfile" button works in standalone mode

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

### Pattern 6: Accumulating State

```csharp
// Allow multiple copy operations - accumulate rather than replace
WizardState.CopiedAssets.AddRange(copiedAssetNames);
WizardState.Step5_AssetsSelected = true;
```

---

## Asset Types Reference

The CopyAssets page handles the following asset types from `CopyAssetType` enum:

| Asset Type | Description | Source Files |
|------------|-------------|--------------|
| `DecalRoad` | Road and marking decals | `.level.json` files |
| `Decal` | Ground decals | `main.decals.json` |
| `Dae` | Collada 3D models | `.dae` files |
| `Material` | Material definitions | `.materials.json` |
| `Prefab` | Prefab instances | `.prefab` files |

---

## Notes

- **DO NOT** modify standalone (non-wizard) functionality
- The CopyAssets page has existing infrastructure for source/target selection - leverage it
- Assets step is optional - users can skip by clicking "Finish Without Assets"
- Test thoroughly after each change before proceeding
- Ensure working directory is properly set from wizard state
- The page already has "Select Another Source Map" functionality - adapt it for wizard mode
