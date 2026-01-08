# WizardFooter Component Implementation Guide

This guide explains the unified footer architecture used across wizard pages in the BeamNG Level Tools application. Use this document when adding wizard mode support to new pages or modifying existing wizard functionality.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Component Reference](#component-reference)
4. [CSS Classes](#css-classes)
5. [Implementation Steps](#implementation-steps)
6. [Code Examples](#code-examples)
7. [Best Practices](#best-practices)

---

## Overview

### Problem Solved

Previously, wizard pages had two overlapping UI elements:
1. A page-specific `<footer>` element with action buttons
2. A separate `CreateLevelAssistant` component with `position: fixed` that overlaid the footer

This caused:
- Visual overlap where buttons were hidden
- Inconsistent UI across pages
- Scrolling required to see navigation controls
- Confusing user experience

### Solution

A single **unified `WizardFooter` component** that:
- Renders either wizard mode or standalone mode footer
- Shows progress chips and navigation in wizard mode
- Accepts customizable content via RenderFragments
- Is always visible at the bottom without scrolling
- Provides consistent UI across all wizard pages

---

## Architecture

### Component Hierarchy

```
Page.razor
??? <div class="content wizard-content">
?   ??? [Page content - scrollable]
??? <WizardFooter>
    ??? Progress Chips (wizard mode only)
    ??? SelectionInfo (RenderFragment)
    ??? ActionButtons (RenderFragment)
    ??? Navigation Buttons (Back/Next/Skip/Finish)
    ??? FooterLinks (RenderFragment)
```

### Layout Structure

```
???????????????????????????????????????????????????????
?                   Content Area                       ?
?              (scrollable, with padding)              ?
???????????????????????????????????????????????????????
?                   WizardFooter                       ?
?  ?????????????????????????????????????????????????  ?
?  ? [Setup] [MissionGroups] [Terrains] [Forest]   ?  ? ? Progress Chips
?  ? [Assets]                     5/5 completed    ?  ?
?  ?????????????????????????????????????????????????  ?
?  ? Selection: 5 items | Size: 10 MB              ?  ? ? SelectionInfo
?  ?????????????????????????????????????????????????  ?
?  ? [Back] [Copy Selected] [Errors] [Next ?]      ?  ? ? Buttons
?  ?????????????????????????????????????????????????  ?
?  ? Working Directory | Logfiles                  ?  ? ? FooterLinks
?  ?????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????
```

---

## Component Reference

### WizardFooter.razor

**Location:** `BlazorUI/Components/WizardFooter.razor`

#### Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `WizardMode` | `bool` | `false` | Whether the page is in wizard mode |
| `WizardState` | `CreateLevelWizardState` | `null` | The wizard state object |
| `SelectionInfo` | `RenderFragment` | `null` | Content showing selection info (count, size) |
| `ActionButtons` | `RenderFragment` | `null` | Page-specific action buttons |
| `FooterLinks` | `RenderFragment` | `null` | Links (Working Directory, Logfiles) |
| `BackButtonText` | `string` | `"Back"` | Text for the back button |
| `NextButtonText` | `string` | `"Continue"` | Text for the next button |
| `CanProceed` | `bool` | `false` | Whether user can proceed to next step |
| `ShowSkipButton` | `bool` | `false` | Whether to show skip button |
| `ShowNextButton` | `bool` | `true` | Whether to show next button |
| `ShowFinishButton` | `bool` | `false` | Whether to show finish button |
| `OnBackClick` | `EventCallback` | - | Callback when back is clicked |
| `OnNextClick` | `EventCallback` | - | Callback when next is clicked |
| `OnSkipClick` | `EventCallback` | - | Callback when skip is clicked |
| `OnFinishClick` | `EventCallback` | - | Callback when finish is clicked |

#### Behavior

- **Wizard Mode** (`WizardMode && WizardState?.IsActive`):
  - Shows progress chips with step colors
  - Shows current step info and target level name
  - Shows Back button (hidden on step 0)
  - Shows Next/Skip/Finish buttons based on parameters
  
- **Standalone Mode** (default):
  - Simple footer with just action buttons and links
  - No progress chips or navigation buttons

---

## CSS Classes

### Required CSS (site.css)

```css
/* Standard footer styling */
.main footer,
.main .standalone-footer,
.main .wizard-footer {
    min-height: 7rem;
    background-color: dimgray;
    padding: 12px 16px;
}

/* Wizard footer needs more height for progress chips + buttons */
.main .wizard-footer {
    min-height: 10rem;
    background: var(--mud-palette-surface);
    border-top: 2px solid var(--mud-palette-primary);
}

/* Standard content area */
.main .content {
    height: calc(100vh - 12rem);
    max-height: calc(100vh - 12rem);
    overflow-y: auto;
}

/* When wizard footer is active, content needs more space at bottom */
.main .content.wizard-content {
    height: calc(100vh - 15rem);
    max-height: calc(100vh - 15rem);
}
```

### Class Usage

| Class | When to Use |
|-------|-------------|
| `content` | Always on the main content div |
| `wizard-content` | Add when `WizardMode` is true |
| `wizard-footer` | Applied automatically by WizardFooter in wizard mode |
| `standalone-footer` | Applied automatically by WizardFooter in standalone mode |

---

## Implementation Steps

### Step 1: Add Required Usings

```razor
@using BeamNG_LevelCleanUp.BlazorUI.Components
@using BeamNG_LevelCleanUp.Objects
@inject NavigationManager Navigation
```

### Step 2: Add Wizard Mode Parameter (in .razor.cs)

```csharp
[Parameter]
[SupplyParameterFromQuery(Name = "wizardMode")]
public bool WizardMode { get; set; }

public CreateLevelWizardState WizardState { get; private set; }
```

### Step 3: Load Wizard State in OnParametersSetAsync

```csharp
protected override async Task OnParametersSetAsync()
{
    if (WizardMode)
    {
        WizardState = CreateLevel.GetWizardState();
        if (WizardState == null || !WizardState.IsActive)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "Wizard state not found. Please start the wizard from Create Level page.");
            Navigation.NavigateTo("/CreateLevel");
            return;
        }

        // Set current step (adjust number for your page)
        WizardState.CurrentStep = 2; // e.g., 2 for CopyTerrains

        // Load data from wizard state
        await LoadLevelsFromWizardState();
    }
}
```

### Step 4: Update Content Div

```razor
<div class="content @(WizardMode ? "wizard-content" : "")">
    <!-- Page content -->
</div>
```

### Step 5: Replace Footer with WizardFooter

Remove the old `<footer>` and `<CreateLevelAssistant>` and add:

```razor
<WizardFooter WizardMode="@WizardMode"
              WizardState="@WizardState"
              BackButtonText="@GetBackButtonText()"
              NextButtonText="@GetNextButtonText()"
              CanProceed="@GetCanProceed()"
              ShowNextButton="@GetShowNextButton()"
              ShowFinishButton="@GetShowFinishButton()"
              ShowSkipButton="@GetShowSkipButton()"
              OnBackClick="@OnBackClicked"
              OnNextClick="@OnNextClicked"
              OnSkipClick="@OnSkipClicked"
              OnFinishClick="@OnFinishClicked">
    <SelectionInfo>
        @if (_selectedItems.Any())
        {
            <MudText Typo="Typo.caption">
                Items: @_totalCount | Selected: @_selectedItems.Count() | Size: @_totalSize MB
            </MudText>
        }
    </SelectionInfo>
    <ActionButtons>
        @if (WizardMode)
        {
            <!-- Wizard-specific buttons -->
            @if (_selectedItems.Any())
            {
                <MudButton @onclick="CopyDialogWizardMode" 
                          Color="Color.Primary"
                          Variant="Variant.Filled"
                          Size="Size.Small">
                    Copy Selected (@_selectedItems.Count)
                </MudButton>
            }
        }
        else
        {
            <!-- Standalone buttons -->
            @if (_selectedItems.Any())
            {
                <MudButton @onclick="CopyDialog" Color="Color.Primary" Size="Size.Small">
                    Copy Items
                </MudButton>
            }
            @if (_showDeployButton)
            {
                <MudButton @onclick="ZipAndDeploy" Color="Color.Primary" Size="Size.Small">
                    Build Zipfile
                </MudButton>
            }
        }
        <!-- Common buttons (errors, warnings, messages) -->
        @if (_errors.Any())
        {
            <MudButton Color="Color.Error" Size="Size.Small" 
                      OnClick="@(() => OpenDrawer(Anchor.Bottom, PubSubMessageType.Error))">
                Errors (@_errors.Count)
            </MudButton>
        }
    </ActionButtons>
    <FooterLinks>
        @if (!string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory))
        {
            <MudButton @onclick="ZipFileHandler.OpenExplorer" 
                      StartIcon="@Icons.Material.Filled.FolderOpen" 
                      Variant="Variant.Text" 
                      Color="Color.Primary" 
                      Size="Size.Small">
                Working Directory
            </MudButton>
            <MudButton @onclick="ZipFileHandler.OpenExplorerLogs" 
                      StartIcon="@Icons.Material.Filled.FolderOpen" 
                      Variant="Variant.Text" 
                      Color="Color.Primary" 
                      Size="Size.Small">
                Logfiles
            </MudButton>
        }
    </FooterLinks>
</WizardFooter>
```

### Step 6: Add Helper Methods in @code Block

```razor
@code {
    private string GetBackButtonText() => WizardMode ? "Back to Previous Step" : "";
    
    private string GetNextButtonText()
    {
        if (!WizardMode) return "";
        return _copyCompleted ? "Continue to Next Step" : "Select Next Step";
    }
    
    private bool GetCanProceed() => WizardMode && (_copyCompleted || StepIsComplete());
    
    private bool GetShowNextButton() => WizardMode && !IsLastStep();
    
    private bool GetShowFinishButton() => WizardMode && IsLastStep() && StepIsComplete();
    
    private bool GetShowSkipButton() => WizardMode && !_copyCompleted && !_selectedItems.Any();
    
    private void OnBackClicked()
    {
        if (WizardMode) CancelWizard();
    }
    
    private void OnNextClicked()
    {
        if (WizardMode) FinishWizard();
    }
    
    private void OnSkipClicked()
    {
        if (WizardMode) SkipStep();
    }
    
    private void OnFinishClicked()
    {
        if (WizardMode) FinishWizard();
    }
}
```

---

## Code Examples

### Complete Page Template

```razor
@page "/MyPage"
@using BeamNG_LevelCleanUp.BlazorUI.Components
@using BeamNG_LevelCleanUp.Objects
@inject NavigationManager Navigation

<ErrorBoundary>
    <ChildContent>
        <div class="content @(WizardMode ? "wizard-content" : "")">
            <h3>My Page Title</h3>
            
            @if (WizardMode && WizardState != null)
            {
                <MudAlert Severity="Severity.Info" Class="mb-4">
                    <MudText Typo="Typo.h6">Wizard Mode</MudText>
                    <MudText Typo="Typo.body2">
                        Source: @WizardState.SourceLevelName ? Target: @WizardState.LevelName
                    </MudText>
                </MudAlert>
            }
            
            @if (!WizardMode)
            {
                <!-- Standalone UI (file selection panels, etc.) -->
            }
            
            <!-- Main content (tables, lists, etc.) -->
        </div>
        
        <WizardFooter WizardMode="@WizardMode"
                      WizardState="@WizardState"
                      BackButtonText="@GetBackButtonText()"
                      NextButtonText="@GetNextButtonText()"
                      CanProceed="@GetCanProceed()"
                      ShowNextButton="@ShowNextButton()"
                      ShowFinishButton="@ShowFinishButton()"
                      OnBackClick="@OnBackClicked"
                      OnNextClick="@OnNextClicked">
            <SelectionInfo>
                <!-- Selection info content -->
            </SelectionInfo>
            <ActionButtons>
                <!-- Action buttons -->
            </ActionButtons>
            <FooterLinks>
                <!-- Footer links -->
            </FooterLinks>
        </WizardFooter>
    </ChildContent>
</ErrorBoundary>

@code {
    // Helper methods for WizardFooter parameters
}
```

### Navigation Methods

```csharp
/// <summary>
/// Navigate back to previous wizard step
/// </summary>
private void CancelWizard()
{
    Navigation.NavigateTo("/PreviousPage?wizardMode=true");
}

/// <summary>
/// Navigate to next wizard step
/// </summary>
private void FinishWizard()
{
    PubSubChannel.SendMessage(PubSubMessageType.Info,
        $"Step completed! Proceeding to next step.");
    Navigation.NavigateTo("/NextPage?wizardMode=true");
}

/// <summary>
/// Skip current step and proceed
/// </summary>
private void SkipStep()
{
    if (WizardState != null)
    {
        WizardState.CurrentStepCompleted = true;
    }
    Navigation.NavigateTo("/NextPage?wizardMode=true");
}
```

---

## Best Practices

### Do's ?

1. **Always use `wizard-content` class** when in wizard mode
   ```razor
   <div class="content @(WizardMode ? "wizard-content" : "")">
   ```

2. **Hide standalone UI in wizard mode**
   ```razor
   @if (!WizardMode)
   {
       <MudExpansionPanels>...</MudExpansionPanels>
   }
   ```

3. **Use Size.Small for footer buttons** to keep footer compact
   ```razor
   <MudButton Size="Size.Small">Button</MudButton>
   ```

4. **Check wizard state validity** in `OnParametersSetAsync`
   ```csharp
   if (WizardState == null || !WizardState.IsActive)
   {
       Navigation.NavigateTo("/CreateLevel");
       return;
   }
   ```

5. **Update wizard state after operations**
   ```csharp
   WizardState.CopiedItems.AddRange(newItems);
   WizardState.StepCompleted = true;
   ```

### Don'ts ?

1. **Don't use fixed positioning** for footer elements
   ```css
   /* BAD - causes overlap */
   .my-footer { position: fixed; bottom: 0; }
   ```

2. **Don't render CreateLevelAssistant** in wizard pages
   ```razor
   <!-- BAD - causes duplicate footer -->
   <CreateLevelAssistant />
   ```

3. **Don't hardcode step numbers** - use helper methods
   ```csharp
   // BAD
   if (WizardState.CurrentStep == 2) { }
   
   // GOOD
   if (IsCurrentStep()) { }
   ```

4. **Don't forget to set CurrentStep** when page loads
   ```csharp
   // Required in OnParametersSetAsync
   WizardState.CurrentStep = 2;
   ```

---

## Files Modified for Footer Refactoring

| File | Changes |
|------|---------|
| `BlazorUI/Components/WizardFooter.razor` | **NEW** - Unified footer component |
| `BlazorUI/Components/CreateLevelAssistant.razor` | Converted to inline component (backup) |
| `BlazorUI/Pages/CreateLevel.razor` | Uses WizardFooter |
| `BlazorUI/Pages/CopyTerrains.razor` | Uses WizardFooter |
| `BlazorUI/Pages/CopyForestBrushes.razor` | Uses WizardFooter |
| `BlazorUI/Pages/CopyAssets.razor` | Uses WizardFooter |
| `wwwroot/_content/SharedLibrary/css/site.css` | Footer CSS classes |

---

## Troubleshooting

### Footer Not Visible
- Check that `wizard-content` class is applied to content div
- Verify CSS is loaded (check browser dev tools)
- Ensure no other elements have `position: fixed`

### Buttons Not Working
- Verify EventCallback parameters are bound correctly
- Check that callback methods exist in code-behind
- Ensure WizardState is not null when accessed

### Progress Chips Not Showing
- Verify `WizardMode` is true
- Check `WizardState.IsActive` is true
- Ensure wizard state is loaded in `OnParametersSetAsync`

### Content Overlapping Footer
- Add `wizard-content` class when in wizard mode
- Check CSS calc values match footer height
- Verify no conflicting CSS rules

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2024 | Initial implementation - unified footer replacing overlapping panels |
