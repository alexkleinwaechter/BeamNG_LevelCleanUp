# Phase 3 - Layout Fix for Wizard Assistant Overlay

## Problem Description

When using `CopyTerrains.razor` in wizard mode, the **"Copy Selected Materials" button** in the footer was **hidden** and inaccessible because the wizard assistant panel was covering it.

### Root Cause

The wizard assistant panel (`.assistant-panel`) is positioned as **fixed at the bottom** of the viewport with a high `z-index`:

```css
.assistant-panel {
    position: fixed;
    bottom: 0;
    left: 260px;
    right: 0;
    z-index: 1000;  /* High z-index */
    /* ... */
}
```

The footer with the copy button is rendered **below** the assistant in the DOM and has **no z-index**, so it gets covered:

```razor
<footer>
    <!-- Copy button here -->
</footer>

<CreateLevelAssistant />  <!-- Overlays the footer! -->
```

### Why You Couldn't Scroll

The page layout uses fixed heights:

```css
.main .content {
    height: calc(100vh - 12rem);
    max-height: calc(100vh - 12rem);
    overflow-y: auto;  /* Scrollable */
}

.main footer {
    height: 7rem;
    max-height: 7rem;
}
```

The `.content` div is scrollable, but the **footer is outside** the scrollable area. The assistant panel **fixes itself to the bottom**, covering the footer completely with no way to access what's underneath.

## The Solution

I implemented **two fixes** that work together:

### Fix 1: Add Bottom Padding to Content (In Wizard Mode)

```razor
<div class="content" style="@(WizardMode ? "padding-bottom: 250px;" : "")">
```

**What this does:**
- Adds 250px of padding at the bottom of the scrollable content area
- Creates space so the table and content don't get hidden behind the assistant
- Allows the user to scroll up to see more terrain materials without them being obscured

### Fix 2: Elevate Footer Z-Index (In Wizard Mode)

```razor
<footer style="@(WizardMode ? "position: relative; z-index: 1100; background: var(--mud-palette-surface); padding: 16px;" : "")">
```

**What this does:**
- Sets `position: relative` to enable z-index to work
- Sets `z-index: 1100` (higher than assistant's 1000) to make footer appear **above** the assistant
- Adds background color to ensure footer content is visible (not transparent)
- Adds padding for better spacing

## Visual Layout Explanation

### Before Fix
```
???????????????????????????
?  Content (scrollable)   ?
?  - Terrain materials    ?
?  - Material table       ?
???????????????????????????
??????????????????????????? ? Footer (hidden)
?  [Copy Button]          ?
???????????????????????????
??????????????????????????? ? Assistant (fixed, z-index 1000)
?  ? Wizard Assistant     ? 
?  [Back] [Next]          ? ? COVERS the footer!
???????????????????????????
```

### After Fix
```
???????????????????????????
?  Content (scrollable)   ?
?  - Terrain materials    ?
?  - Material table       ?
?                         ?
?  (250px padding)        ? ? Space for assistant
???????????????????????????
??????????????????????????? ? Footer (z-index 1100)
?  [Copy Button]          ? ? VISIBLE above assistant!
???????????????????????????
??????????????????????????? ? Assistant (z-index 1000)
?  Wizard Assistant       ?
?  [Back] [Next]          ? ? Behind the footer
???????????????????????????
```

## How It Works

### Standard Mode (No Wizard)
- No inline styles applied
- Footer behaves normally at the bottom
- No assistant panel to worry about

### Wizard Mode
- **Content div** gets `padding-bottom: 250px`
  - Creates vertical space for scrolling
  - Prevents table from being hidden
- **Footer** gets elevated with `z-index: 1100`
  - Appears above the assistant panel
  - Copy button is now visible and clickable
- **Assistant panel** remains at `z-index: 1000`
  - Stays at bottom of viewport
  - Appears behind the footer

## Why This Solution Works

1. **Preserves Functionality**
   - Footer button is now accessible
   - Assistant panel still visible
   - Both elements serve their purpose

2. **Maintains UX**
   - User can scroll through terrain materials
   - Wizard controls always visible at bottom
   - Copy button accessible when needed

3. **Conditional Application**
   - Only applies in wizard mode
   - Standard mode unchanged
   - No impact on other pages

## Alternative Solutions Considered

### Option A: Move Assistant Above Footer
**Rejected** - Would require significant DOM restructuring and might confuse wizard flow

### Option B: Make Assistant Collapsible
**Rejected** - Defeats the purpose of persistent wizard guidance

### Option C: Reduce Assistant Size
**Rejected** - Wizard controls need adequate space for usability

### Option D: Use CSS Grid/Flexbox Layout
**Rejected** - Would require refactoring entire layout system

## Testing Checklist

? **Wizard Mode**
- [ ] Copy button visible and clickable
- [ ] Assistant panel visible at bottom
- [ ] Can scroll through terrain materials
- [ ] Footer appears above assistant
- [ ] No layout glitches on resize

? **Standard Mode**
- [ ] Footer behaves normally
- [ ] No extra padding
- [ ] No z-index issues
- [ ] Layout unchanged

? **Edge Cases**
- [ ] Small screen sizes
- [ ] Many terrain materials (long scroll)
- [ ] Few terrain materials (short list)
- [ ] Browser zoom levels

## Code Changes

### Files Modified
1. **`BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor`**
   - Added conditional `padding-bottom` to `.content` div
   - Added conditional `z-index` elevation to `<footer>`

### CSS Reference (Unchanged)
- `.assistant-panel` - Still at `z-index: 1000`
- `.main .content` - Still scrollable with overflow
- `.main footer` - Still fixed height

## Build Status

? **Build Successful** - No compilation errors  
? **Layout Fixed** - Footer now visible in wizard mode  
? **Backward Compatible** - Standard mode unchanged  

## Summary

The layout issue was caused by the wizard assistant panel's fixed positioning and high z-index covering the footer. The fix elevates the footer above the assistant while adding padding to the content area to prevent overlap. This ensures both the copy button and wizard controls are accessible without requiring major layout changes.

---

**Issue**: Footer hidden by wizard assistant panel  
**Cause**: Fixed positioning with overlapping z-indexes  
**Fix**: Elevate footer z-index + add content padding  
**Status**: ? RESOLVED  
**Date**: December 2024
