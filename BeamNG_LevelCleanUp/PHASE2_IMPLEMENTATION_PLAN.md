# Phase 2 Implementation Plan - Create Level UI Components

## Overview
Create the Blazor UI components for the Create Level wizard, including the main page and the persistent assistant component.

## Components to Create

### 1. CreateLevelAssistant.razor (Wizard Progress Component)
**Location**: `BeamNG_LevelCleanUp/BlazorUI/Components/CreateLevelAssistant.razor`

**Purpose**: 
- Persistent UI component that shows wizard progress
- Displays as a sticky footer when wizard is active
- Shows current step, completed steps, and navigation

**Key Features**:
- MudStepper component for visual progress
- Back/Next navigation buttons
- Step status indicators (checkmarks for completed)
- Display key information (level name, source)
- Auto-navigate between pages based on step

**Integration Points**:
- Receives CreateLevelWizardState as cascading parameter
- Uses NavigationManager for page transitions
- Subscribes to state changes

### 2. CreateLevel.razor (Main Page)
**Location**: `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`

**Purpose**: 
- Main entry point for Create Level wizard
- Handles source map selection and target configuration
- Initiates level creation process
- Reviews copied data

**Workflow Steps**:
1. **Initial State**: Select source map (like CopyAssets.razor)
2. **Configuration**: Enter target level path and name (like RenameMap.razor)
3. **Initialize**: Create directory structure, copy MissionGroup data
4. **Review**: Show what was copied
5. **Navigate**: Proceed to terrain material selection

**Key Features**:
- Source map selection (ZIP file or vanilla level)
- Target level configuration inputs
- MissionGroup data copying
- Empty terrain material file creation
- Review section showing copied assets
- Assistant component integration

**Reusable Patterns From**:
- `CopyAssets.razor`: File selection, vanilla level dropdown
- `RenameMap.razor`: Text input fields for level naming
- `CopyTerrains.razor`: PubSub message handling, error display

## State Management Strategy

### Cascading Parameter Approach
```csharp
// In MainLayout.razor or App.razor
private CreateLevelWizardState _wizardState = new();

<CascadingValue Value="_wizardState">
    @Body
</CascadingValue>
```

### State Persistence
- Use static instance in CreateLevel.razor for session persistence
- Reset on page initialization if not in wizard mode
- Share state with CopyTerrains via cascading value

## UI Layout Structure

```
???????????????????????????????????????????????????
? CreateLevel Page Header                         ?
???????????????????????????????????????????????????
?                                                 ?
? [Source Map Selection Panel]                   ?
?   ?? File Selection Component                  ?
?   ?? Vanilla Level Dropdown                    ?
?                                                 ?
? [Target Level Configuration] (when source set) ?
?   ?? Level Path Input                          ?
?   ?? Display Name Input                        ?
?   ?? [Initialize Button]                       ?
?                                                 ?
? [Review Section] (after initialization)        ?
?   ?? Copied MissionGroup Assets List           ?
?   ?? Progress Messages                         ?
?                                                 ?
???????????????????????????????????????????????????
? Error/Warning/Info Buttons (footer)            ?
???????????????????????????????????????????????????
?                                                 ?
? ??????????????????????????????????????????????? ?
? ? CreateLevelAssistant (Sticky Footer)       ? ?
? ?  [Step 1] ??> [Step 2] ??> [Step 3]       ? ?
? ?  [Back]                          [Next]   ? ?
? ??????????????????????????????????????????????? ?
???????????????????????????????????????????????????
```

## CSS Requirements

Add to `wwwroot/css/app.css`:
```css
.assistant-panel {
    position: fixed;
    bottom: 0;
    left: 0;
    right: 0;
    padding: 16px;
    z-index: 1000;
    background: var(--mud-palette-surface);
    border-top: 2px solid var(--mud-palette-primary);
    box-shadow: 0 -2px 10px rgba(0,0,0,0.1);
}

.content {
    padding-bottom: 120px; /* Space for assistant panel */
}
```

## File References

### Existing Files to Reference
1. `CopyAssets.razor` - Source map selection pattern
2. `CopyTerrains.razor` - Target map handling, wizard mode detection
3. `RenameMap.razor` - Level naming inputs
4. `MyNavMenu.razor` - Navigation menu for new entry

### New Dependencies
- `MissionGroupCopier.cs` (already created)
- `InfoJsonGenerator.cs` (already created)
- `CreateLevelWizardState.cs` (already created)

## Implementation Checklist

- [ ] Create CreateLevelAssistant.razor component
  - [ ] MudStepper UI
  - [ ] State display
  - [ ] Navigation logic
  - [ ] CSS styling

- [ ] Create CreateLevel.razor page
  - [ ] Source map selection
  - [ ] Target configuration inputs
  - [ ] Initialize level logic
  - [ ] Review section
  - [ ] PubSub message handling
  - [ ] Assistant integration

- [ ] Add CSS to app.css
  - [ ] Assistant panel styling
  - [ ] Content padding adjustment

- [ ] Test integration
  - [ ] Verify state sharing
  - [ ] Test navigation flow
  - [ ] Verify MissionGroup copying

## Next Phase Preview

**Phase 3**: Modify CopyTerrains.razor
- Add wizard mode detection
- Auto-load source/target from wizard state
- Return to CreateLevel after copy
- Update wizard state with copied materials
