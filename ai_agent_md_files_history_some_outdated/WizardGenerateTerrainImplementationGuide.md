# Implementation Guide: Adding GenerateTerrain as Final Wizard Step

This guide provides a comprehensive walkthrough for implementing `GenerateTerrain.razor` as **Step 5** (final step) in the Create Level Wizard. Unlike other wizard steps that copy assets, this step generates terrain files and requires special handling including a completion dialog with next-steps instructions.

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Updated Wizard Flow](#updated-wizard-flow)
3. [Implementation Checklist](#implementation-checklist)
4. [Step 1: Update CreateLevelWizardState](#step-1-update-createlevelwizardstate)
5. [Step 2: Update WizardFooter Component](#step-2-update-wizardfooter-component)
6. [Step 3: Update CopyAssets.razor.cs Navigation](#step-3-update-copyassetsrazorcs-navigation)
7. [Step 4: Create TerrainWizardCompletionDialog Component](#step-4-create-terrainwizardcompletiondialog-component)
8. [Step 5: Update GenerateTerrain.razor](#step-5-update-generateterrainrazor)
9. [Step 6: Update GenerateTerrain.razor.cs](#step-6-update-generateterrainrazorcs)
10. [Step 7: Update CreateLevel.razor](#step-7-update-createlevelrazor)
11. [Testing Checklist](#testing-checklist)
12. [Key Differences from Other Wizard Steps](#key-differences-from-other-wizard-steps)

---

## Architecture Overview

### Key Differences from Copy Steps

| Aspect | Copy Steps (Terrains, Forest, Assets) | Generate Terrain Step |
|--------|---------------------------------------|----------------------|
| **Action** | Copy files from source to target | Generate new files in target |
| **Completion** | Simple success message | Multi-step instruction dialog |
| **Next Action** | Navigate to next step | Show finish button after dialog |
| **State Update** | After copy operation | After terrain generation |
| **Folder Selection** | Hidden in wizard mode | Uses target level folder from wizard |

### Component Hierarchy

```
GenerateTerrain.razor (Wizard Mode)
├── <div class="content wizard-content">
│   ├── MudAlert (Wizard info banner)
│   ├── TerrainPresetImporter (save/load presets)
│   ├── TerrainPresetExporter
│   ├── Terrain Parameters (auto-populated from wizard)
│   ├── Materials Section
│   └── Generate Button
├── TerrainWizardCompletionDialog (NEW - shown after generation)
│   └── Next steps instructions
└── <WizardFooter>
    └── Finish Button (shown after dialog closed)
```

---

## Updated Wizard Flow

```
Step 0: Setup (CreateLevel.razor)
    ↓
Step 1: MissionGroups (CreateLevel.razor)
    ↓
Step 2: Terrain Materials (CopyTerrains.razor?wizardMode=true)
    ↓
Step 3: Forest Brushes (CopyForestBrushes.razor?wizardMode=true)
    ↓
Step 4: Assets (CopyAssets.razor?wizardMode=true)
    ↓
Step 5: Generate Terrain (GenerateTerrain.razor?wizardMode=true) [NEW]
    ├── User configures terrain parameters
    ├── User clicks "Generate Terrain"
    ├── TerrainWizardCompletionDialog shows with instructions
    ├── User closes dialog
    └── Finish button appears in footer
    ↓
Return to CreateLevel.razor (Wizard Complete)
```

---

## Implementation Checklist

- [ ] Update `CreateLevelWizardState` - Add Step 6 properties for terrain generation
- [ ] Update `WizardFooter.razor` - Add Step 5 chip for "Generate Terrain"
- [ ] Update `CopyAssets.razor.cs` - Change navigation to GenerateTerrain instead of CreateLevel
- [ ] Create `TerrainWizardCompletionDialog.razor` - New dialog component with instructions
- [ ] Update `GenerateTerrain.razor` - Add wizard mode UI and WizardFooter
- [ ] Update `GenerateTerrain.razor.cs` - Add wizard state handling and dialog trigger
- [ ] Update `CreateLevel.razor` - Display Step 6 completion status

---

## Step 1: Update CreateLevelWizardState

**File:** `BeamNG_LevelCleanUp\Objects\CreateLevelWizardState.cs`

### Add New Properties

```csharp
// Add after Step5_AssetsSelected property:

/// <summary>
///     Step 6: Terrain generated successfully
/// </summary>
public bool Step6_TerrainGenerated { get; set; }

/// <summary>
///     Path to the generated terrain file (.ter)
/// </summary>
public string GeneratedTerrainPath { get; set; }

/// <summary>
///     Indicates if the wizard completion dialog has been shown and closed
/// </summary>
public bool TerrainCompletionDialogShown { get; set; }
```

### Update Reset() Method

```csharp
public void Reset()
{
    // ... existing resets ...
    Step5_AssetsSelected = false;
    Step6_TerrainGenerated = false;  // ADD THIS
    GeneratedTerrainPath = null;  // ADD THIS
    TerrainCompletionDialogShown = false;  // ADD THIS
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
        4 => Step5_AssetsSelected,
        5 => Step6_TerrainGenerated,  // ADD THIS
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
        Step5_AssetsSelected,
        Step6_TerrainGenerated  // ADD THIS
    }.Count(x => x);
    return $"{completedSteps}/6 steps completed";  // CHANGE 5 to 6
}
```

---

## Step 2: Update WizardFooter Component

**File:** `BeamNG_LevelCleanUp\BlazorUI\Components\WizardFooter.razor`

### Add Step 5 Chip (Generate Terrain)

Update the chip stack to include the Generate Terrain step:

```razor
@* Row 1: Progress Chips and Summary *@
<MudStack Row="true" Justify="Justify.SpaceBetween" AlignItems="AlignItems.Center" Class="mb-2">
    <MudStack Row="true" Spacing="2" Class="flex-wrap">
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(0)" Icon="@Icons.Material.Filled.Settings">Setup</MudChip>
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(1)" Icon="@Icons.Material.Filled.Layers">MissionGroups</MudChip>
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(2)" Icon="@Icons.Material.Filled.Terrain">Terrains</MudChip>
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(3)" Icon="@Icons.Material.Filled.Forest">Forest</MudChip>
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(4)" Icon="@Icons.Material.Filled.ContentCopy">Assets</MudChip>
        <MudChip T="string" Size="Size.Small" Color="@GetStepColor(5)" Icon="@Icons.Material.Filled.Landscape">Generate</MudChip> @* ADD THIS *@
    </MudStack>
    
    <MudText Typo="Typo.caption" Color="Color.Primary">
        @WizardState.GetProgressSummary() @(string.IsNullOrEmpty(WizardState.LevelName) ? "" : $" | Target: {WizardState.LevelName}")
    </MudText>
</MudStack>
```

---

## Step 3: Update CopyAssets.razor.cs Navigation

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyAssets.razor.cs`

### Update FinishWizard() Method

Change to navigate to GenerateTerrain instead of CreateLevel:

```csharp
/// <summary>
///     Navigates to terrain generation step (next step after assets)
/// </summary>
private void FinishWizard()
{
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Assets step completed! Copied {_totalCopiedAssetsCount} asset(s). Proceeding to terrain generation.");

    // Navigate to terrain generation step instead of finishing
    Navigation.NavigateTo("/GenerateTerrain?wizardMode=true");
}
```

### Update GetNextButtonText() Method

```csharp
private string GetNextButtonText()
{
    if (!WizardMode) return "";
    return _copyCompleted ? "Continue to Terrain Generation" : "Generate Terrain";
}
```

### Update GetShowFinishButton() Method

Change to return false (finish is now on GenerateTerrain page):

```csharp
private bool GetShowFinishButton() => false; // Finish is now on GenerateTerrain page
```

### Update ShowNextButton logic in CopyAssets.razor

```razor
ShowNextButton="@(WizardMode && (_copyCompleted || WizardState?.Step5_AssetsSelected == true))"
ShowFinishButton="@false"
```

---

## Step 4: Create TerrainWizardCompletionDialog Component

**File:** `BeamNG_LevelCleanUp\BlazorUI\Components\TerrainWizardCompletionDialog.razor` (NEW FILE)

```razor
@using MudBlazor

<MudDialog>
    <DialogContent>
        <MudStack Spacing="4">
            <MudAlert Severity="Severity.Success" Variant="Variant.Filled" Icon="@Icons.Material.Filled.CheckCircle">
                <MudText Typo="Typo.h6">Terrain Generated Successfully!</MudText>
            </MudAlert>

            <MudText Typo="Typo.body1">
                Your terrain file has been created. Follow these steps to complete your level setup:
            </MudText>

            <MudPaper Class="pa-4" Elevation="2">
                <MudStack Spacing="3">
                    <MudStack Row="true" AlignItems="AlignItems.Center">
                        <MudAvatar Color="Color.Primary" Size="Size.Small">1</MudAvatar>
                        <MudText Typo="Typo.body1">
                            <strong>Save Your Preset</strong> - At the top of the Generate Terrain page, save your current
                            settings as a preset so you can reload them later for refinements.
                        </MudText>
                    </MudStack>

                    <MudStack Row="true" AlignItems="AlignItems.Center">
                        <MudAvatar Color="Color.Primary" Size="Size.Small">2</MudAvatar>
                        <MudText Typo="Typo.body1">
                            <strong>Finish the Wizard</strong> - Click the "Finish Wizard" button below to return to
                            the Create Level page and copy your level to the BeamNG levels folder.
                        </MudText>
                    </MudStack>

                    <MudStack Row="true" AlignItems="AlignItems.Center">
                        <MudAvatar Color="Color.Primary" Size="Size.Small">3</MudAvatar>
                        <MudText Typo="Typo.body1">
                            <strong>Test & Refine</strong> - Launch BeamNG to test your terrain. Come back to this page
                            anytime to load your level and preset to regenerate the terrain with adjustments.
                        </MudText>
                    </MudStack>
                </MudStack>
            </MudPaper>

            @if (!string.IsNullOrEmpty(TerrainFilePath))
            {
                <MudAlert Severity="Severity.Info" Dense="true" Icon="@Icons.Material.Filled.FolderOpen">
                    <MudText Typo="Typo.caption">
                        <strong>Terrain file:</strong> @TerrainFilePath
                    </MudText>
                </MudAlert>
            }
        </MudStack>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Close" 
                   Variant="Variant.Filled" 
                   Color="Color.Primary"
                   StartIcon="@Icons.Material.Filled.Check">
            Got It - Show Finish Button
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter]
    private IMudDialogInstance MudDialog { get; set; }

    /// <summary>
    /// Path to the generated terrain file (optional, for display)
    /// </summary>
    [Parameter]
    public string TerrainFilePath { get; set; }

    private void Close()
    {
        MudDialog.Close(DialogResult.Ok(true));
    }
}
```

---

## Step 5: Update GenerateTerrain.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor`

### Add Required Usings and Inject NavigationManager

At the top of the file, add:

```razor
@using BeamNG_LevelCleanUp.BlazorUI.Components
@inject NavigationManager Navigation
```

### Update Content Div with Wizard Mode Class

Change:
```razor
<div class="content">
```
To:
```razor
<div class="content @(WizardMode ? "wizard-content" : "")">
```

### Add Wizard Mode Info Banner

Add this immediately after `<h3>Generate Terrain</h3>`:

```razor
@if (WizardMode && WizardState != null)
{
    <MudAlert Severity="Severity.Info" Class="mb-4" Icon="@Icons.Material.Filled.Assistant">
        <MudText Typo="Typo.h6">Create Level Wizard - Generate Terrain</MudText>
        <MudText Typo="Typo.body2">
            Target Level: <b>@WizardState.LevelName</b>
        </MudText>
        <MudText Typo="Typo.body2" Class="mt-2">
            @if (_terrainGeneratedInWizard)
            {
                <text>Terrain generated! Save your preset above, then finish the wizard.</text>
            }
            else
            {
                <text>Configure terrain settings and click "Generate Terrain" to create the terrain file for your new level.</text>
            }
        </MudText>
    </MudAlert>
    
    @if (_terrainGeneratedInWizard)
    {
        <MudAlert Severity="Severity.Success" Class="mb-4" Icon="@Icons.Material.Filled.CheckCircle">
            <MudText Typo="Typo.h6">Terrain Generated Successfully!</MudText>
            <MudText Typo="Typo.body2">
                Your terrain has been created. Remember to save your preset above before finishing.
            </MudText>
        </MudAlert>
    }
}
```

### Hide Folder Selection in Wizard Mode

Wrap the folder selection panel:

```razor
@if (!WizardMode)
{
    <!-- Folder Selection -->
    <MudExpansionPanels @ref="FileSelect">
        <MudExpansionPanel Text="@GetWorkingDirectoryTitle()" Expanded="@(!_hasWorkingDirectory)">
            <FileSelectComponent OnFileSelected="OnWorkingDirectorySelected"
                                 SelectFolder="true"
                                 Description="Select the unpacked level folder (contains info.json and art/terrains)">
            </FileSelectComponent>
        </MudExpansionPanel>
    </MudExpansionPanels>
}
```

### Replace the Existing Footer with WizardFooter

Replace the existing `<footer>` block with:

```razor
@* Unified Footer Component *@
<WizardFooter WizardMode="@WizardMode"
              WizardState="@WizardState"
              BackButtonText="@GetBackButtonText()"
              NextButtonText="@GetNextButtonText()"
              CanProceed="@GetCanProceed()"
              ShowNextButton="@false"
              ShowFinishButton="@GetShowFinishButton()"
              ShowSkipButton="@GetShowSkipButton()"
              OnBackClick="@OnBackClicked"
              OnSkipClick="@SkipStep"
              OnFinishClick="@FinishWizard">
    <SelectionInfo>
        @if (_hasWorkingDirectory)
        {
            <MudText Typo="Typo.caption">
                Level: @_levelName | Materials: @_terrainMaterials.Count | Size: @_terrainSize px
            </MudText>
        }
    </SelectionInfo>
    <ActionButtons>
        @* Error/Warning/Message buttons remain the same *@
        @if (_errors.Any())
        {
            <MudButton Color="Color.Error" Size="Size.Small"
                       OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Error))">
                Errors (@_errors.Count)
            </MudButton>
        }
        @if (_warnings.Any())
        {
            <MudButton Color="Color.Warning" Size="Size.Small"
                       OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Warning))">
                Warnings (@_warnings.Count)
            </MudButton>
        }
        @if (_messages.Any())
        {
            <MudButton Color="Color.Info" Size="Size.Small"
                       OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Info))">
                Messages (@_messages.Count)
            </MudButton>
        }
    </ActionButtons>
    <FooterLinks>
        @if (!string.IsNullOrEmpty(_workingDirectory))
        {
            <MudButton @onclick="OpenWorkingDirectory"
                       StartIcon="@Icons.Material.Filled.FolderOpen"
                       Variant="Variant.Text"
                       Color="Color.Primary"
                       Size="Size.Small">
                Working Directory
            </MudButton>
        }
    </FooterLinks>
</WizardFooter>
```

---

## Step 6: Update GenerateTerrain.razor.cs

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor.cs`

### Add Wizard Mode Properties and Navigation Injection

Add at the top of the class:

```csharp
// ========================================
// WIZARD MODE PROPERTIES
// ========================================

[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }

/// <summary>
///     Wizard state reference when in wizard mode
/// </summary>
public CreateLevelWizardState WizardState { get; private set; }

/// <summary>
///     Indicates if terrain has been generated during this wizard session
/// </summary>
private bool _terrainGeneratedInWizard { get; set; }

[Inject]
private NavigationManager Navigation { get; set; }
```

### Add OnParametersSetAsync Method

Add this method to handle wizard state loading:

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
        WizardState.CurrentStep = 5;

        // Auto-load level from wizard state
        await LoadLevelFromWizardState();
    }
}
```

### Add LoadLevelFromWizardState Method

```csharp
/// <summary>
///     Loads the target level from wizard state for terrain generation
/// </summary>
private async Task LoadLevelFromWizardState()
{
    if (WizardState == null) return;

    try
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            "Wizard mode: Loading level for terrain generation...");

        var targetLevelRootPath = WizardState.TargetLevelRootPath;
        
        if (string.IsNullOrEmpty(targetLevelRootPath) || !Directory.Exists(targetLevelRootPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Target level path not found: {targetLevelRootPath}");
            return;
        }

        // Use the material service to load the level
        await Task.Run(() =>
        {
            var result = _materialService.LoadLevelFromFolder(targetLevelRootPath);

            if (!result.Success)
            {
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    PubSubChannel.SendMessage(PubSubMessageType.Error, result.ErrorMessage);
                return;
            }

            _workingDirectory = result.LevelPath;
            _hasWorkingDirectory = true;
            _levelName = result.LevelName;

            // Apply loaded materials
            _terrainMaterials.Clear();
            _terrainMaterials.AddRange(result.Materials);

            // Apply existing terrain settings if found
            if (result.ExistingTerrainSize.HasValue)
            {
                _terrainSize = result.ExistingTerrainSize.Value;
                _hasExistingTerrainSettings = true;
            }
            else if (WizardState.TerrainSize > 0)
            {
                // Use terrain size from wizard state
                _terrainSize = WizardState.TerrainSize;
            }

            if (!string.IsNullOrEmpty(result.TerrainName))
                _terrainName = result.TerrainName;
            if (result.MetersPerPixel.HasValue)
                _metersPerPixel = result.MetersPerPixel.Value;
        });

        await InvokeAsync(StateHasChanged);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Wizard mode: Level loaded successfully - {_terrainMaterials.Count} terrain materials found");
    }
    catch (Exception ex)
    {
        ShowException(ex);
        PubSubChannel.SendMessage(PubSubMessageType.Error,
            $"Failed to load level in wizard mode: {ex.Message}");
    }
}
```

### Add Wizard Footer Helper Methods

```csharp
// ========================================
// WIZARD FOOTER HELPER METHODS
// ========================================

private string GetBackButtonText() => WizardMode ? "Back to Assets" : "";

private string GetNextButtonText() => "";  // Not used - this is the last step

private bool GetCanProceed() => WizardMode && _terrainGeneratedInWizard && 
                                 WizardState?.TerrainCompletionDialogShown == true;

private bool GetShowFinishButton() => WizardMode && _terrainGeneratedInWizard && 
                                       WizardState?.TerrainCompletionDialogShown == true;

private bool GetShowSkipButton() => WizardMode && !_terrainGeneratedInWizard;

private void OnBackClicked()
{
    if (WizardMode)
    {
        Navigation.NavigateTo("/CopyAssets?wizardMode=true");
    }
}

private void SkipStep()
{
    if (WizardState != null)
    {
        // Mark as skipped (not generated) and finish wizard
        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            "Terrain generation skipped. You can generate terrain later from this page.");
    }
    Navigation.NavigateTo("/CreateLevel");
}

private void FinishWizard()
{
    if (WizardState != null)
    {
        WizardState.Step6_TerrainGenerated = true;
    }
    
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Create Level Wizard completed! Your level '{WizardState?.LevelName}' is ready.");
    
    Navigation.NavigateTo("/CreateLevel");
}
```

### Add UpdateWizardStateAfterGeneration Method

```csharp
/// <summary>
/// Updates wizard state after successful terrain generation
/// </summary>
private void UpdateWizardStateAfterGeneration()
{
    if (WizardState == null) return;

    WizardState.GeneratedTerrainPath = GetOutputPath();
    WizardState.Step6_TerrainGenerated = true;
    _terrainGeneratedInWizard = true;

    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Terrain generated for {WizardState.LevelName}");
}
```

### Add ShowTerrainWizardCompletionDialog Method

```csharp
/// <summary>
/// Shows the wizard completion dialog with next-steps instructions
/// </summary>
private async Task ShowTerrainWizardCompletionDialog()
{
    var options = new DialogOptions
    {
        CloseButton = false,
        CloseOnEscapeKey = false,
        BackdropClick = false,
        MaxWidth = MaxWidth.Medium
    };

    var parameters = new DialogParameters<TerrainWizardCompletionDialog>
    {
        { x => x.TerrainFilePath, GetOutputPath() }
    };

    var dialog = await DialogService.ShowAsync<TerrainWizardCompletionDialog>(
        "Terrain Generation Complete",
        parameters,
        options);

    var result = await dialog.Result;

    // When dialog is closed, mark it as shown and trigger UI update
    if (WizardState != null)
    {
        WizardState.TerrainCompletionDialogShown = true;
    }

    await InvokeAsync(StateHasChanged);
}
```

### Update ExecuteTerrainGeneration Method

Modify the success handling to show the wizard dialog:

```csharp
private async Task ExecuteTerrainGeneration()
{
    if (!CanGenerate()) return;

    // ... existing reordering and setup code ...

    _isGenerating = true;
    StateHasChanged();

    try
    {
        // Execute terrain generation via orchestrator
        var result = await _generationOrchestrator.ExecuteAsync(_state);

        if (result.Success)
        {
            Snackbar.Add($"Terrain generated successfully: {GetOutputPath()}", Severity.Success);
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Terrain file saved to: {GetOutputPath()}");

            // Run post-generation tasks
            await _generationOrchestrator.RunPostGenerationTasksAsync(_state, result.Parameters);
            Snackbar.Add("Post-processing complete!", Severity.Success);

            // Write log files
            _generationOrchestrator.WriteGenerationLogs(_state);

            // WIZARD MODE: Update state and show completion dialog
            if (WizardMode && WizardState != null)
            {
                UpdateWizardStateAfterGeneration();
                await ShowTerrainWizardCompletionDialog();
            }
        }
        else
        {
            Snackbar.Add("Terrain generation failed. Check errors for details.", Severity.Error);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                ShowException(new Exception(result.ErrorMessage));
        }
    }
    catch (Exception ex)
    {
        ShowException(ex);
        Snackbar.Add($"Error generating terrain: {ex.Message}", Severity.Error);
    }
    finally
    {
        _isGenerating = false;
        StateHasChanged();
    }
}
```

---

## Step 7: Update CreateLevel.razor

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CreateLevel.razor`

### Add Step 6 Completion Display

Add this section after the Step 5 (Assets) completion display:

```razor
@if (_wizardState.Step6_TerrainGenerated)
{
    <MudPaper Class="pa-4 mt-4" Elevation="2">
        <MudText Typo="Typo.h6" Class="mb-4">
            <MudIcon Icon="@Icons.Material.Filled.Landscape" Color="Color.Success" Class="mr-2"/>
            Terrain Generated
        </MudText>
        
        @if (!string.IsNullOrEmpty(_wizardState.GeneratedTerrainPath))
        {
            <MudText Typo="Typo.body2" Class="mb-2">
                <strong>Terrain file:</strong> @_wizardState.GeneratedTerrainPath
            </MudText>
        }
        
        <MudDivider Class="my-4"/>
        
        <MudAlert Severity="Severity.Success" Class="mb-4">
            <b>🎉 Level Creation Complete!</b> Your level is ready with terrain materials, forest brushes, 
            assets, and generated terrain.
        </MudAlert>
        
        <MudStack Row="true" Class="mt-4" Spacing="2">
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
            
            <MudButton OnClick="@(() => Navigation.NavigateTo("/GenerateTerrain"))" 
                      Color="Color.Secondary" 
                      Variant="Variant.Outlined"
                      StartIcon="@Icons.Material.Filled.Terrain">
                Refine Terrain
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

### Update Previous Step Display Conditions

Update Step 5 (Assets) display to only show if Step 6 is NOT completed:

```razor
@if (_wizardState.Step5_AssetsSelected && !_wizardState.Step6_TerrainGenerated)
{
    // Existing assets completion display
    // Update the alert text:
    <MudAlert Severity="Severity.Info" Class="mb-4">
        <b>Next Step:</b> Generate terrain for your level. Click "Generate Terrain" in the wizard below.
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
5. [ ] Navigate through CopyTerrains, CopyForestBrushes, CopyAssets
6. [ ] On CopyAssets, click "Continue to Terrain Generation"
7. [ ] Verify GenerateTerrain loads with wizard mode
8. [ ] Verify target level auto-loaded (materials visible)
9. [ ] Configure terrain parameters
10. [ ] Click "Generate Terrain"
11. [ ] Verify TerrainWizardCompletionDialog appears
12. [ ] Read instructions and click "Got It"
13. [ ] Verify Finish button now visible in footer
14. [ ] Click "Finish Wizard"
15. [ ] Verify return to CreateLevel with all 6 steps complete

### Wizard State Tests

1. [ ] Verify wizard state persists across page navigations
2. [ ] Verify terrain generation status tracked correctly
3. [ ] Verify Back button returns to CopyAssets
4. [ ] Verify Skip button works (navigates to CreateLevel without generating)
5. [ ] Verify Reset wizard clears all state including Step 6

### Dialog Tests

1. [ ] Verify dialog cannot be closed by clicking backdrop
2. [ ] Verify dialog cannot be closed with Escape key
3. [ ] Verify dialog shows terrain file path
4. [ ] Verify Finish button only appears after dialog closed

### Standalone Mode Tests

1. [ ] Verify GenerateTerrain still works without wizard mode
2. [ ] Verify no wizard UI elements appear in standalone mode
3. [ ] Verify folder selection works normally
4. [ ] Verify no completion dialog in standalone mode

---

## Key Differences from Other Wizard Steps

| Aspect | Copy Steps | Generate Terrain Step |
|--------|-----------|----------------------|
| Source Selection | Hidden in wizard, uses wizard state | Hidden in wizard, uses target from wizard state |
| Main Action | Copy button | Generate Terrain button |
| Success Handling | Simple state update | State update + completion dialog |
| Finish Button | Shows after copy complete | Shows after dialog closed |
| State Property | `CopiedXxx` lists | `GeneratedTerrainPath` |
| Post-Action | Navigate to next step | Stay on page, show finish button |
| Skip Behavior | Marks step complete | Navigates to CreateLevel without completing |

---

## Files to Create/Modify Summary

| File | Action | Description |
|------|--------|-------------|
| `Objects/CreateLevelWizardState.cs` | MODIFY | Add Step6 properties |
| `BlazorUI/Components/WizardFooter.razor` | MODIFY | Add Generate step chip |
| `BlazorUI/Pages/CopyAssets.razor` | MODIFY | Update navigation to GenerateTerrain |
| `BlazorUI/Pages/CopyAssets.razor.cs` | MODIFY | Update FinishWizard navigation |
| `BlazorUI/Components/TerrainWizardCompletionDialog.razor` | **CREATE** | New dialog component |
| `BlazorUI/Pages/GenerateTerrain.razor` | MODIFY | Add wizard mode UI and WizardFooter |
| `BlazorUI/Pages/GenerateTerrain.razor.cs` | MODIFY | Add wizard state handling |
| `BlazorUI/Pages/CreateLevel.razor` | MODIFY | Add Step 6 completion display |

---

## Notes

- **DO NOT** modify standalone (non-wizard) functionality
- The completion dialog is critical for user guidance
- Finish button must only appear after dialog is acknowledged
- Terrain generation can be done multiple times (user can refine)
- Skip should be available for users who want to generate terrain later

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024 | Initial implementation guide |
