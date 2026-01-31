# Phase 3 - Cascading Parameter Fix for Wizard State

## Problem Description

When navigating from `CreateLevel.razor` to `CopyTerrains.razor?wizardMode=true`, the `WizardState` was **null** because Blazor's `[CascadingParameter]` **does not persist across page navigation**.

### Root Cause

```razor
<!-- In CopyTerrains.razor -->
[CascadingParameter]
public CreateLevelWizardState WizardState { get; set; }
```

This approach only works when components are **nested within the same render tree**. When you navigate to a different page:
1. The current page is unmounted
2. A new page is rendered
3. Cascading values from the previous page are **lost**

## The Solution

Instead of relying on `[CascadingParameter]`, we made the wizard state accessible via a **static method** on `CreateLevel.razor`:

### Changes Made

#### 1. Exposed Static Wizard State in `CreateLevel.razor`

```csharp
@code {
    private static CreateLevelWizardState _wizardState = new();
    
    /// <summary>
    ///     Exposes the wizard state for other pages (e.g., CopyTerrains in wizard mode)
    /// </summary>
    public static CreateLevelWizardState GetWizardState() => _wizardState;
    
    // ... rest of the code
}
```

**Why this works:**
- The wizard state is **static**, so it persists across page navigation
- The `GetWizardState()` method provides controlled access to it
- Other pages can call `CreateLevel.GetWizardState()` to retrieve it

#### 2. Updated `CopyTerrains.razor` to Use Static Method

```csharp
@code {
    [Parameter]
    [SupplyParameterFromQuery(Name = "wizardMode")]
    public bool WizardMode { get; set; }

    // Changed from [CascadingParameter] to regular property
    private CreateLevelWizardState WizardState { get; set; }
    
    protected override async Task OnParametersSetAsync()
    {
        if (WizardMode)
        {
            // Get wizard state from CreateLevel's static field
            WizardState = CreateLevel.GetWizardState();
            
            if (WizardState == null || !WizardState.IsActive)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                    "Wizard state not available. Please start from the Create Level page.");
                return;
            }
            
            await LoadLevelsFromWizardState();
        }
        
        await base.OnParametersSetAsync();
    }
}
```

**What changed:**
- Removed `[CascadingParameter]` attribute
- Added code to fetch wizard state from `CreateLevel.GetWizardState()`
- Added validation to ensure wizard is active
- Shows warning if wizard state is not available

## Why Cascading Parameters Don't Work for Navigation

### How Cascading Parameters Work

```razor
<!-- Parent Component -->
<CascadingValue Value="myState">
    <ChildComponent />  <!-- ? Works - same render tree -->
</CascadingValue>
```

The child component receives the cascading value because it's **rendered as part of the parent's render tree**.

### Why Navigation Breaks This

```razor
<!-- CreateLevel.razor -->
<CascadingValue Value="_wizardState">
    <CreateLevelAssistant />  <!-- ? Works - nested component -->
</CascadingValue>

<!-- User clicks button to navigate -->
Navigation.NavigateTo("/CopyTerrains?wizardMode=true");
<!-- ? CopyTerrains.razor renders in a NEW render tree -->
<!-- ? Cascading value from CreateLevel is NOT available -->
```

When Blazor navigates to a new page:
1. The router component switches to the new page
2. The new page has its own independent render tree
3. Cascading values from the previous page are lost

## Alternative Solutions Considered

### Option 1: Scoped Service (Better for Production)

Register wizard state as a scoped service in `Program.cs`:

```csharp
builder.Services.AddScoped<CreateLevelWizardState>();
```

Then inject it in both pages:

```csharp
@inject CreateLevelWizardState WizardState
```

**Pros:**
- More testable
- Better separation of concerns
- Follows Blazor best practices

**Cons:**
- Requires more refactoring
- State resets when browser is refreshed

### Option 2: Browser Storage (Best for Persistence)

Store wizard state in `sessionStorage` or `localStorage`:

```csharp
await JS.InvokeVoidAsync("sessionStorage.setItem", "wizardState", JsonSerializer.Serialize(wizardState));
```

**Pros:**
- Survives page refreshes
- Survives browser back/forward navigation

**Cons:**
- More complex
- Requires serialization/deserialization

### Option 3: Static Field (Current Solution)

Use a static field with accessor method:

```csharp
private static CreateLevelWizardState _wizardState = new();
public static CreateLevelWizardState GetWizardState() => _wizardState;
```

**Pros:**
- Simplest to implement
- No additional dependencies
- Works across page navigation
- Minimal refactoring

**Cons:**
- Static state can be harder to test
- Shared across all users in the same app instance (not an issue for desktop apps)

**Why we chose this:** BeamNG_LevelCleanUp is a **Windows desktop app**, not a multi-user web app. Static state is perfectly acceptable and keeps the solution simple.

## Impact

### What Now Works

? Navigate from CreateLevel to CopyTerrains in wizard mode  
? WizardState is properly initialized and accessible  
? Terrain materials load automatically  
? Wizard assistant shows correct state  
? Navigation back to CreateLevel preserves state  

### What Still Works

? Standard mode (non-wizard) unchanged  
? CreateLevel wizard assistant component still receives cascading value  
? All existing functionality preserved  

## Testing the Fix

### Before Fix
```
1. Initialize level in CreateLevel
2. Click "Select Terrain Materials"
3. Navigate to CopyTerrains?wizardMode=true
4. ? WizardState is null
5. ? Page is empty/broken
```

### After Fix
```
1. Initialize level in CreateLevel
2. Click "Select Terrain Materials"
3. Navigate to CopyTerrains?wizardMode=true
4. ? WizardState retrieved from CreateLevel.GetWizardState()
5. ? Levels auto-load
6. ? Terrain materials display
7. ? Wizard workflow continues normally
```

## Build Status

? **Build Successful** - No compilation errors  
? **Wizard State Accessible** - Static method provides access  
? **Backward Compatible** - Standard mode unchanged  

## Files Modified

1. `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`
   - Added `GetWizardState()` static method

2. `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor`
   - Removed `[CascadingParameter]` attribute
   - Added code to fetch wizard state in `OnParametersSetAsync()`

## Summary

The fix addresses the fundamental limitation of Blazor's cascading parameters not working across page navigation. By exposing the wizard state through a static method, we enable `CopyTerrains.razor` to access the state regardless of how it's navigated to.

This is the **correct approach for desktop applications** where state management across navigation is needed but service injection or browser storage would be overkill.

---

**Issue**: CascadingParameter doesn't work across page navigation  
**Cause**: New pages render in separate render trees  
**Fix**: Static field with accessor method  
**Status**: ? RESOLVED  
**Date**: December 2024
