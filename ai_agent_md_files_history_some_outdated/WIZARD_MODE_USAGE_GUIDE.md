# Wizard Mode Usage Guide - CopyTerrains Integration

## Overview

The `CopyTerrains.razor` page now supports **Wizard Mode**, allowing users to select and copy terrain materials as part of the Create Level wizard workflow. This guide explains how to use the wizard mode and its benefits.

## User Workflow

### Complete Wizard Flow (End-to-End)

#### Step 1: Initialize New Level (CreateLevel Page)
1. Open the **Create Level** page
2. Fill in level details:
   - Level Name (e.g., "My Custom Map")
   - Level Path (e.g., "my_custom_map")
   - Select source level (e.g., "driver_training")
3. Click **"Initialize New Level"** button
4. Wizard assistant panel appears at bottom of page
5. MissionGroup objects are automatically copied

#### Step 2: Review Progress (CreateLevel Page)
1. Wizard assistant shows Step 1 complete (? Setup)
2. Progress summary shows "1/3 steps completed"
3. Review copied MissionGroup assets
4. Click **"Select Terrain Materials"** button in assistant panel

#### Step 3: Select Terrain Materials (CopyTerrains Page - Wizard Mode)
1. **Automatic Navigation**: Page navigates to `/CopyTerrains?wizardMode=true`
2. **Auto-Load**: Source and target levels load automatically
3. **Wizard Banner**: Shows context information:
   ```
   Create Level Wizard - Step 3: Select Terrain Materials
   Source: driver_training ? Target: My Custom Map
   Select the terrain materials you want to copy to your new level.
   ```
4. **Material Table**: Displays all terrain materials from source level
5. **Selection**:
   - Check materials you want to copy
   - Customize base color (optional)
   - Adjust roughness preset (optional)
   - Choose "Add New Material" or "Replace: existing_material"
6. **Preview**: Bottom summary shows selected count and total size
7. **Actions**:
   - Click **"Copy Selected Materials (X)"** to proceed
   - Click **"Back to Create Level"** to return without changes

#### Step 4: Confirm and Copy
1. Confirmation dialog appears:
   ```
   Copy X new material(s) and replace Y existing material(s)?
   ```
2. Click **"Copy Materials"** to confirm
3. Progress message: "Copying terrain materials..."
4. **Automatic Navigation**: Returns to CreateLevel page
5. Wizard state updated with copied materials

#### Step 5: Complete Wizard (CreateLevel Page)
1. Wizard assistant shows Step 3 complete (? Terrain Materials)
2. Progress summary shows "3/3 steps completed"
3. View summary of copied materials
4. Click **"Finish"** to complete wizard and build deployment
5. Or click **"Build Deployment"** to create ZIP file

---

## Wizard Mode Features

### 1. Automatic Level Loading
**Standard Mode**: User must manually select source and target ZIP files  
**Wizard Mode**: Levels are automatically loaded from wizard state

### 2. Streamlined UI
**Hidden in Wizard Mode**:
- File selection panels
- Reset button
- Build deployment button

**Visible in Wizard Mode**:
- Wizard context banner
- Material selection table
- Wizard-specific footer buttons
- Assistant panel (bottom right)

### 3. Wizard-Specific Actions
**Buttons**:
- **Back to Create Level**: Return to wizard overview without changes
- **Copy Selected Materials**: Copy materials and update wizard state

**Behavior**:
- Simpler confirmation dialog (no scary warnings)
- Automatic navigation after copy
- Wizard state automatically updated

### 4. Assistant Panel Integration
**Features**:
- Shows current step (Step 3: Terrain Materials)
- Displays progress chips (Setup, MissionGroups, Terrain Materials)
- Quick navigation between wizard steps
- Progress summary (X/3 steps completed)

---

## Comparison: Standard vs. Wizard Mode

| Feature | Standard Mode | Wizard Mode |
|---------|---------------|-------------|
| **Navigation** | Direct URL: `/CopyTerrains` | From wizard: `/CopyTerrains?wizardMode=true` |
| **File Selection** | Manual (file picker) | Automatic (from wizard state) |
| **UI Panels** | All visible | Streamlined (file selection hidden) |
| **Context Banner** | None | Wizard step banner |
| **Footer Buttons** | Copy, Build Zipfile, Reset | Copy, Back to Wizard |
| **Assistant Panel** | Not visible | Visible (bottom right) |
| **After Copy** | Stay on page | Return to CreateLevel |
| **State Update** | None | Updates wizard state |
| **Use Case** | Standalone terrain copying | Part of level creation workflow |

---

## Technical Details

### URL Parameters
**Wizard Mode Detection**:
```
/CopyTerrains?wizardMode=true
```

**Standard Mode** (no parameters):
```
/CopyTerrains
```

### State Flow

```
CreateLevel (Step 1)
    ?
[Initialize New Level]
    ?
WizardState populated with:
    - SourceLevelPath
    - SourceLevelName
    - TargetLevelRootPath
    - LevelName
    ?
[User clicks "Select Terrain Materials"]
    ?
Navigate to /CopyTerrains?wizardMode=true
    ?
CopyTerrains detects wizardMode=true
    ?
Auto-load from WizardState
    ?
User selects materials
    ?
[User clicks "Copy Selected Materials"]
    ?
Materials copied to target level
    ?
WizardState updated:
    - CopiedTerrainMaterials
    - Step3_TerrainMaterialsSelected = true
    ?
Navigate back to /CreateLevel
    ?
CreateLevel shows completion status
```

---

## Error Handling

### Null Wizard State
**Protection**: Code checks `WizardState != null` before accessing
**Fallback**: If null, behaves like standard mode

### Invalid Paths
**Handling**: Try-catch in `LoadLevelsFromWizardState()`
**User Feedback**: Exception shown via `ShowException()` and snackbar

### No Materials Found
**Behavior**: Empty table with warning message
**User Action**: Can return to wizard or select different source

### Copy Failure
**Protection**: Errors logged to drawer
**State**: Wizard state not updated on failure
**Recovery**: User can try again or cancel

---

## Best Practices

### For Users
1. **Always use wizard mode** when creating new levels (recommended workflow)
2. **Review materials** before copying (check sizes and dependencies)
3. **Use Replace mode** to override existing materials with same name
4. **Customize colors/roughness** before copying (saves manual editing later)
5. **Don't refresh** the page during wizard workflow (state may be lost)

### For Developers
1. **Test both modes** when making changes to CopyTerrains.razor
2. **Preserve backward compatibility** for standard mode
3. **Update wizard state** properly after operations
4. **Handle null states** defensively
5. **Provide clear user feedback** for all operations

---

## Troubleshooting

### Issue: Wizard banner not showing
**Cause**: `wizardMode` query parameter missing or `WizardState` is null  
**Solution**: Ensure navigation uses `?wizardMode=true` and CreateLevel provides `CascadingValue`

### Issue: Levels not auto-loading
**Cause**: `WizardState` paths are empty or invalid  
**Solution**: Verify CreateLevel properly initializes wizard state before navigation

### Issue: Can't return to CreateLevel
**Cause**: Navigation service not injected  
**Solution**: Verify `@inject NavigationManager Navigation` in CopyTerrains.razor

### Issue: Wizard state not updating
**Cause**: `UpdateWizardStateAfterCopy()` not called or wizard state is null  
**Solution**: Ensure method is called after successful copy and state is not null

### Issue: Assistant panel not visible
**Cause**: `CascadingValue` not wrapping assistant component  
**Solution**: Verify `@if (WizardMode && WizardState != null)` condition and `CascadingValue` block

---

## Future Enhancements (Ideas)

1. **Material Presets**: Pre-select common material combinations (e.g., "All road materials")
2. **Smart Recommendations**: Suggest materials based on level type
3. **Material Preview**: Show texture previews in table
4. **Dependency Detection**: Auto-select materials referenced by copied MissionGroups
5. **Undo Support**: Allow reverting material copy before building deployment
6. **State Persistence**: Save wizard state to localStorage for browser refresh recovery
7. **Progress Indicator**: Show real-time progress during material copy
8. **Bulk Operations**: Copy all materials with one click

---

## Related Documentation

- **Phase 3 Implementation Plan**: `PHASE3_IMPLEMENTATION_PLAN.md`
- **Phase 3 Completion Report**: `PHASE3_IMPLEMENTATION_COMPLETE.md`
- **Copilot Instructions**: `.github/copilot-instructions.md`
- **BeamNG File Formats**: See "File Format Reference" in copilot-instructions.md

---

**Last Updated**: December 2024  
**Version**: 1.0  
**Status**: ? Production Ready
