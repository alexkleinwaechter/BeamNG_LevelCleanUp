# Phase 3 Implementation Plan - CopyTerrains Wizard Mode Integration

## Overview
Modify the existing `CopyTerrains.razor` page to support wizard mode when invoked from the Create Level wizard. This allows seamless terrain material selection as part of the level creation workflow.

## Objectives

1. **Detect Wizard Mode**: Recognize when the page is opened from the Create Level wizard
2. **Auto-load Levels**: Automatically load source and target levels from wizard state
3. **Streamlined UX**: Hide unnecessary UI elements in wizard mode
4. **State Synchronization**: Update wizard state after terrain material copying
5. **Navigation**: Return to CreateLevel page after completion

## Current State Analysis

### Existing CopyTerrains.razor Structure
```
- File selection panels (source + target)
- Terrain material table with selection
- Copy/Replace mode selection
- Build deployment button
- Standard footer with messages
```

### Wizard Mode Requirements
```
- Skip file selection (auto-load from wizard state)
- Show wizard context banner
- Update wizard state after copy
- Navigate back to CreateLevel on completion
- Maintain assistant panel visibility
```

## Implementation Tasks

### Task 1: Add Query Parameter Detection

**Location**: `CopyTerrains.razor` - `@code` section

**Add**:
```csharp
[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }

[CascadingParameter]
public CreateLevelWizardState WizardState { get; set; }
```

**Purpose**: Detect when page is in wizard mode and receive wizard state

---

### Task 2: Auto-load Levels in Wizard Mode

**Location**: `CopyTerrains.razor` - `OnInitializedAsync()` or `OnParametersSet()`

**Add Method**:
```csharp
protected override async Task OnParametersSetAsync()
{
    if (WizardMode && WizardState != null)
    {
        await LoadLevelsFromWizardState();
    }
    
    await base.OnParametersSetAsync();
}

private async Task LoadLevelsFromWizardState()
{
    try
    {
        // 1. Set source level (from wizard state)
        _levelPathCopyFrom = WizardState.SourceLevelPath;
        _levelNameCopyFrom = WizardState.SourceLevelName;
        
        // 2. Set target level (newly created level)
        _levelPath = WizardState.TargetLevelRootPath;
        _levelName = WizardState.TargetLevelPath;
        
        // 3. Initialize BeamFileReader with both paths
        Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);
        
        // 4. Scan assets (call existing method)
        await ScanAssets();
        
        PubSubChannel.SendMessage(PubSubMessageType.Info, 
            "Wizard mode: Levels loaded automatically");
    }
    catch (Exception ex)
    {
        ShowException(ex);
    }
}
```

**Purpose**: Load source and target levels automatically without user file selection

---

### Task 3: Modify UI for Wizard Mode

**Location**: `CopyTerrains.razor` - Razor markup

**Changes**:

1. **Hide File Selection Panels in Wizard Mode**:
```razor
@if (!WizardMode)
{
    <MudExpansionPanels @ref="FileSelect">
        <!-- Existing file selection panels -->
    </MudExpansionPanels>
}
```

2. **Add Wizard Mode Banner**:
```razor
@if (WizardMode && WizardState != null)
{
    <MudAlert Severity="Severity.Info" Class="mb-4" Icon="@Icons.Material.Filled.Assistant">
        <MudText Typo="Typo.h6">Create Level Wizard - Step 3: Select Terrain Materials</MudText>
        <MudText Typo="Typo.body2">
            Source: <b>@WizardState.SourceLevelName</b> ? Target: <b>@WizardState.LevelName</b>
        </MudText>
        <MudText Typo="Typo.body2" Class="mt-2">
            Select the terrain materials you want to copy to your new level.
        </MudText>
    </MudAlert>
}
```

3. **Modify Footer Buttons**:
```razor
<footer>
    @if (WizardMode)
    {
        <!-- Wizard mode: Different button layout -->
        <MudStack Row="true" Justify="Justify.SpaceBetween">
            <MudButton OnClick="@CancelWizard" 
                      Variant="Variant.Outlined"
                      StartIcon="@Icons.Material.Filled.ArrowBack">
                Back to Create Level
            </MudButton>
            
            @if (_selectedItems.Any())
            {
                <MudButton @onclick="CopyDialogWizardMode" 
                          Color="Color.Primary"
                          Variant="Variant.Filled"
                          StartIcon="@Icons.Material.Filled.Check">
                    Copy Selected Materials (@_selectedItems.Count)
                </MudButton>
            }
        </MudStack>
    }
    else
    {
        <!-- Standard mode: Existing footer -->
        <MudStack Row="true" Justify="Justify.SpaceBetween">
            <!-- Existing buttons -->
        </MudStack>
    }
    
    <!-- Common elements (messages, working directory) -->
</footer>
```

---

### Task 4: Update Wizard State After Copy

**Location**: `CopyTerrains.razor` - New method

**Add Method**:
```csharp
private async Task CopyDialogWizardMode()
{
    var options = new DialogOptions { CloseOnEscapeKey = true };
    var parameters = new DialogParameters();
    
    var replaceCount = _selectedItems.Count(x => x.CopyAsset.IsReplaceMode);
    var addCount = _selectedItems.Count - replaceCount;
    
    var messageText = $"Copy {addCount} new material(s) and replace {replaceCount} existing material(s)?";
    
    parameters.Add("ContentText", messageText);
    parameters.Add("ButtonText", "Copy Materials");
    parameters.Add("Color", Color.Primary);
    
    var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Terrain Materials", parameters, options);
    var result = await dialog.Result;
    
    if (!result.Canceled)
    {
        _staticSnackbar = Snackbar.Add("Copying terrain materials...", Severity.Normal, 
            config => { config.VisibleStateDuration = int.MaxValue; });
            
        await Task.Run(() =>
        {
            var selected = _selectedItems.Select(y => y.Identifier).ToList();
            Reader.DoCopyAssets(selected);
        });
        
        Snackbar.Remove(_staticSnackbar);
        
        // Update wizard state
        UpdateWizardStateAfterCopy();
        
        // Navigate back to CreateLevel
        Navigation.NavigateTo("/CreateLevel");
    }
}

private void UpdateWizardStateAfterCopy()
{
    if (WizardState == null) return;
    
    // Collect copied terrain materials
    var copiedMaterials = _selectedItems
        .Where(x => x.CopyAsset.CopyAssetType == CopyAssetType.Terrain)
        .SelectMany(x => x.CopyAsset.Materials)
        .ToList();
    
    WizardState.CopiedTerrainMaterials = copiedMaterials;
    WizardState.Step3_TerrainMaterialsSelected = true;
    WizardState.CurrentStep = 2; // Keep on step 2 to show completion
    
    PubSubChannel.SendMessage(PubSubMessageType.Info, 
        $"Copied {copiedMaterials.Count} terrain material(s) to {WizardState.LevelName}");
}
```

**Purpose**: Update wizard state with copied materials and navigate back

---

### Task 5: Add Cancel/Back Navigation

**Location**: `CopyTerrains.razor` - New method

**Add Method**:
```csharp
private void CancelWizard()
{
    // Navigate back to CreateLevel without making changes
    Navigation.NavigateTo("/CreateLevel");
}
```

**Purpose**: Allow user to return to CreateLevel without copying

---

### Task 6: Integrate with Assistant Component

**Location**: `CopyTerrains.razor` - Razor markup (bottom of page)

**Add**:
```razor
@if (WizardMode && WizardState != null)
{
    <!-- Pass wizard state to assistant via CascadingValue -->
    <CascadingValue Value="WizardState">
        <CreateLevelAssistant />
    </CascadingValue>
}
```

**Purpose**: Show wizard assistant panel when in wizard mode

---

### Task 7: Adjust CSS for Assistant Panel

**Location**: `site.css` (already done in Phase 2)

**Verify**:
- `.assistant-panel` class exists
- Proper z-index and positioning
- Content padding adjustment when assistant is visible

---

## Detailed Implementation Steps

### Step 1: Add Using Statements
```csharp
@using BeamNG_LevelCleanUp.BlazorUI.Components
@using BeamNG_LevelCleanUp.Objects
```

### Step 2: Add Parameters and State
```csharp
[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }

[CascadingParameter]
public CreateLevelWizardState WizardState { get; set; }
```

### Step 3: Modify OnInitialized/OnParametersSet
- Add wizard mode detection
- Call LoadLevelsFromWizardState() if in wizard mode
- Skip standard file selection flow

### Step 4: Update UI Markup
- Conditionally hide file selection panels
- Add wizard mode banner
- Modify footer buttons
- Add assistant component

### Step 5: Add Wizard-Specific Methods
- LoadLevelsFromWizardState()
- CopyDialogWizardMode()
- UpdateWizardStateAfterCopy()
- CancelWizard()

### Step 6: Test Integration
- Navigate from CreateLevel with wizardMode=true
- Verify auto-load of source/target
- Test material selection
- Verify wizard state update
- Test navigation back to CreateLevel

---

## Testing Checklist

### Wizard Mode Tests
- [ ] URL parameter `?wizardMode=true` is detected
- [ ] Source level auto-loads from wizard state
- [ ] Target level auto-loads from wizard state
- [ ] File selection panels are hidden
- [ ] Wizard banner displays correct information
- [ ] Terrain materials are scanned automatically
- [ ] Material selection works normally
- [ ] Copy button triggers wizard-specific dialog
- [ ] Wizard state is updated after copy
- [ ] Navigation returns to CreateLevel
- [ ] Assistant panel is visible
- [ ] Assistant panel shows correct step

### Standard Mode Tests (Regression)
- [ ] Standard mode still works without wizard parameter
- [ ] File selection panels are visible
- [ ] Normal copy workflow functions
- [ ] No wizard-specific UI elements shown
- [ ] Build deployment button works
- [ ] No assistant panel shown

---

## Edge Cases to Handle

1. **Wizard State Null**: Check `WizardState != null` before accessing
2. **Invalid Paths**: Handle cases where wizard state paths don't exist
3. **No Materials Found**: Show appropriate message if source has no terrain materials
4. **Copy Failure**: Revert wizard state if copy operation fails
5. **Browser Back Button**: Ensure wizard state persists across navigation
6. **Refresh in Wizard Mode**: Handle page refresh while in wizard mode

---

## Integration Points

### With CreateLevel.razor
- Receives wizard state via CascadingValue
- Navigates back after material copy
- Updates shared wizard state

### With CreateLevelAssistant.razor
- Shows assistant panel in wizard mode
- Reflects current step (Step 3)
- Allows navigation between wizard steps

### With BeamFileReader
- Uses existing ReadAllForCopy() method
- Leverages existing CopyAssets workflow
- Maintains compatibility with standard mode

---

## Code Patterns to Follow

### Conditional UI Rendering
```razor
@if (WizardMode)
{
    <!-- Wizard-specific UI -->
}
else
{
    <!-- Standard UI -->
}
```

### Safe State Access
```csharp
if (WizardMode && WizardState != null)
{
    // Access wizard state
}
```

### Navigation with State Preservation
```csharp
Navigation.NavigateTo("/CreateLevel");
// Wizard state persists because it's static in CreateLevel.razor
```

---

## Expected User Flow

1. User clicks "Initialize New Level" in CreateLevel
2. Wizard assistant appears at bottom
3. User clicks "Select Terrain Materials" button
4. Navigation.NavigateTo("/CopyTerrains?wizardMode=true")
5. CopyTerrains loads with:
   - Source: driver_training (from wizard)
   - Target: new_level_path (from wizard)
   - Materials auto-scanned
6. User selects desired terrain materials
7. User clicks "Copy Selected Materials"
8. Materials are copied
9. Wizard state updated
10. Navigation back to CreateLevel
11. CreateLevel shows "Terrain Materials Copied" section
12. User can build deployment or create another level

---

## Files to Modify

1. **CopyTerrains.razor** (Primary)
   - Add query parameter
   - Add cascading parameter
   - Modify OnParametersSet
   - Add wizard-specific methods
   - Update UI markup
   - Add assistant component

2. **CreateLevel.razor** (Minor - if needed)
   - Verify CascadingValue wraps assistant
   - Ensure wizard state is static/persistent

3. **CreateLevelAssistant.razor** (Minor - if needed)
   - Verify "Select Terrain Materials" navigation URL includes wizardMode=true

---

## Success Criteria

? Wizard mode is automatically detected  
? Source and target levels load without user input  
? File selection panels are hidden in wizard mode  
? Wizard context banner is displayed  
? Material selection works identically to standard mode  
? Wizard state is correctly updated after copy  
? Navigation returns to CreateLevel page  
? Assistant panel remains visible and functional  
? Standard mode continues to work normally  
? All existing tests pass  
? No regressions in copy terrain functionality  

---

## Potential Issues and Solutions

### Issue 1: Wizard State Not Persisting
**Solution**: Ensure wizard state is static or properly managed at app level

### Issue 2: Auto-load Fails
**Solution**: Add comprehensive error handling in LoadLevelsFromWizardState()

### Issue 3: Assistant Panel Not Showing
**Solution**: Verify CascadingValue is properly configured

### Issue 4: Navigation Doesn't Work
**Solution**: Check that Navigation service is properly injected

### Issue 5: Materials Don't Copy
**Solution**: Ensure BeamFileReader paths are correctly set

---

## Future Enhancements (Post-Phase 3)

- Pre-select recommended terrain materials
- Material preview thumbnails in wizard mode
- Terrain material suggestions based on source level
- Bulk selection presets (e.g., "Copy all road materials")
- Material dependency detection and auto-selection

---

## Phase 3 Completion Checklist

- [ ] Query parameter detection implemented
- [ ] Cascading parameter added
- [ ] Auto-load method created
- [ ] UI conditionally rendered for wizard mode
- [ ] Wizard banner added
- [ ] Footer buttons updated
- [ ] Wizard-specific copy method created
- [ ] Wizard state update logic implemented
- [ ] Cancel/back navigation added
- [ ] Assistant component integrated
- [ ] All tests pass
- [ ] Code reviewed
- [ ] Documentation updated
- [ ] Build successful
- [ ] End-to-end workflow tested

---

## Related Files

- `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`
- `BeamNG_LevelCleanUp/BlazorUI/Components/CreateLevelAssistant.razor`
- `BeamNG_LevelCleanUp/Objects/CreateLevelWizardState.cs`
- `BeamNG_LevelCleanUp/Logic/BeamFileReader.cs`
- `BeamNG_LevelCleanUp/wwwroot/_content/SharedLibrary/css/site.css`

---

## Phase Dependencies

**Requires Phase 1 Complete**:
- ? CreateLevelWizardState object
- ? BeamFileReader.ReadMissionGroupsForCreateLevel()
- ? MissionGroupCopier
- ? InfoJsonGenerator

**Requires Phase 2 Complete**:
- ? CreateLevel.razor page
- ? CreateLevelAssistant.razor component
- ? CSS styling for assistant panel
- ? Navigation menu entry

**Ready for Phase 4**:
- Final testing and polish
- Documentation
- User guide
- Release notes
