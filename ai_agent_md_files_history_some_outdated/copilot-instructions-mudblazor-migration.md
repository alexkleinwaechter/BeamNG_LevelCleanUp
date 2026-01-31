# MudBlazor Migration Guide (v6 ? v8) - AI Agent Instructions

## Purpose
This document provides AI coding assistants with systematic instructions for migrating MudBlazor applications from version 6 to version 8 (via v7 breaking changes).

## Target Framework
- **Minimum Required**: .NET 7+
- **Recommended**: .NET 8 or .NET 9
- **.NET 6**: No longer supported

---

## CRITICAL: Pre-Migration Checklist

### 1. Add MudPopoverProvider (MANDATORY)
**Action Required**: Add `<MudPopoverProvider/>` to App.razor or MainLayout.razor

```razor
<!-- BEFORE (v6) -->
<MudThemeProvider/>
<MudDialogProvider/>
<MudSnackbarProvider/>

<!-- AFTER (v7+) - ADD MudPopoverProvider -->
<MudThemeProvider/>
<MudPopoverProvider/>  ? REQUIRED
<MudDialogProvider/>
<MudSnackbarProvider/>
```

**Why Critical**: Application will fail at runtime without this provider.

### 2. Compiler Warning Strategy
- MudBlazor v7+ includes a compile-time analyzer
- **IMPORTANT**: Many parameter renames won't cause compiler errors due to attribute splatting
- **Action**: Enable and resolve ALL analyzer warnings
- **Configuration**: See PR #9031 for analyzer options

### 3. Icon Namespace Changes
**All icon references must be updated**:

```csharp
// BEFORE (v6)
Icons.Filled.*
Icons.Outlined.*

// AFTER (v7+)
Icons.Material.Filled.*
Icons.Material.Outlined.*
Icons.Material.Rounded.*
Icons.Material.Sharp.*
Icons.Material.TwoTone.*
```

**Search Pattern**: `Icons\.(?!Material\.)` (regex)

---

## Systematic Parameter Renaming

### Category 1: Inverted Negative Parameters
**?? CRITICAL**: These require **inverting the boolean value/condition**

| Old Parameter | New Parameter | Inversion Required |
|---------------|---------------|-------------------|
| `DisableRipple` | `Ripple` | ? Invert |
| `DisableGutters` | `Gutters` | ? Invert |
| `DisablePadding` | `Padding` | ? Invert |
| `DisableElevation` | `DropShadow` | ? Invert |
| `DisableUnderLine` | `Underline` | ? Invert |
| `DisableRowsPerPage` | `PageSizeSelector` | ? Invert |
| `DisableBackdropClick` | `BackdropClick` | ? Invert |
| `DisableSidePadding` | `Gutters` | ? Invert |
| `DisableOverlay` | `Overlay` | ? Invert |
| `DisableSliderAnimation` | `SliderAnimation` | ? Invert |
| `DisableModifiers` | `Modifiers` | ? Invert |
| `DisableBorders` | `Outlined` | ? Invert |

**Migration Examples**:
```razor
<!-- BEFORE -->
<MudButton DisableRipple="true">
<MudList DisableGutters="@_hideGutters">
<MudDialog DisableSidePadding="false">

<!-- AFTER -->
<MudButton Ripple="false">
<MudList Gutters="@(!_hideGutters)">
<MudDialog Gutters="true">
```

### Category 2: "Is" Prefix Removal
**No inversion required** - simple rename:

| Old Parameter | New Parameter |
|---------------|---------------|
| `IsEnabled` | `Enabled` |
| `IsVisible` | `Visible` |
| `IsSelected` | `Selected` |
| `IsExpanded` | `Expanded` |
| `IsOpen` | `Open` |
| `IsChecked` | `Checked` |
| `IsHidden` | `Hidden` |
| `IsEditable` | `Editable` |
| `IsExpandable` | `Expandable` |
| `IsCheckable` | `Checkable` |
| `IsInitiallyExpanded` | `Expanded` |

**Migration Examples**:
```razor
<!-- BEFORE -->
<MudExpansionPanel IsInitiallyExpanded="true">
<MudTreeViewItem IsExpanded="@_isExpanded">
<MudDialog IsVisible="@_dialogVisible">

<!-- AFTER -->
<MudExpansionPanel Expanded="true">
<MudTreeViewItem @bind-Expanded="@_isExpanded">
<MudDialog @bind-Visible="@_dialogVisible">
```

### Category 3: Common Parameter Renames

| Component | Old Parameter | New Parameter |
|-----------|---------------|---------------|
| Pickers | `ClassAction` | `ActionsClass` |
| Pickers | `InputIcon` | `AdornmentIcon` |
| Pickers | `InputVariant` | `Variant` |
| All Buttons | `Link` | `Href` |
| MudBadge | `Bottom`, `Left`, `Start` | `Origin` |
| MudFab | `Icon` | `StartIcon` |
| MudCheckBox, MudSwitch | `Checked` | `Value` |
| MudRadio | `Option` | `Value` |
| MudRadioGroup | `SelectedOption` | `Value` |
| MudDialog | `ClassBackground` | `BackgroundClass` |
| MudDialog | `ClassContent` | `ContentClass` |
| MudCarousel | `ShowDelimiters` | `ShowBullets` |
| MudCarousel | `DelimitersColor` | `BulletsColor` |
| MudProgressCircular/Linear | `Minimum` | `Min` |
| MudProgressCircular/Linear | `Maximum` | `Max` |
| MudToggleGroup | `Outline` | `Outlined` |
| MudAlert | `AlertTextPosition` | `ContentAlignment` |
| MudList/MudListItem | `Avatar`, `AvatarClass` | `AvatarContent` (RenderFragment) |
| MudFilePicker | `ButtonTemplate` | `ActivatorContent` |
| MudAutocomplete | `SelectOnClick` | `SelectOnActivation` |
| MudListItem | `AdornmentColor` | `ExpandIconColor` |
| MudListItem | `OnClickHandlerPreventDefault` | `OnClickPreventDefault` |
| MudListItem | `InitiallyExpanded` | `Expanded="true"` |

---

## Component-Specific Migration Rules

### MudExpansionPanels & MudExpansionPanel

**Key Changes**:
```razor
<!-- BEFORE -->
<MudExpansionPanel IsInitiallyExpanded="true">
<MudExpansionPanel DisableBorders="true">

<!-- AFTER -->
<MudExpansionPanel @bind-Expanded="_expanded">  <!-- Set _expanded = true in code -->
<MudExpansionPanel Outlined="false">  <!-- Inverted -->
```

**Method Changes**:
```csharp
// BEFORE
FileSelect?.CollapseAll();
panel.ExpandAll();
panel.Collapse();

// AFTER
await FileSelect?.CollapseAllAsync();
await panel.ExpandAllAsync();
await panel.CollapseAsync();
```

**Removed APIs**:
- `RemovePanel()` - Use data-driven approach instead

---

### MudTreeView & MudTreeViewItem

**Selection Changes**:
```razor
<!-- BEFORE -->
<MudTreeView T="ITree<Asset>" Items="Items" MultiSelection="true">

<!-- AFTER -->
<MudTreeView T="ITree<Asset>" Items="Items" SelectionMode="SelectionMode.MultiSelection">
```

**SelectionMode Mapping**:
| Old Parameters | New SelectionMode |
|----------------|-------------------|
| `MultiSelection="true"` | `SelectionMode.MultiSelection` |
| `MultiSelection="false"` `Mandatory="true"` | `SelectionMode.SingleSelection` |
| `MultiSelection="false"` `Mandatory="false"` | `SelectionMode.ToggleSelection` |

**Default Behavior Change**:
- v6: Not selectable by default
- v7+: Selection active by default (`SelectionMode.SingleSelection`)
- Use `ReadOnly="true"` for non-selectable tree

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudTreeView CanActivate CanHover CanSelect>
    <MudTreeViewItem Actived="@_isActive" ExpandedIcon="@Icons.Filled.Folder">
    </MudTreeViewItem>
</MudTreeView>

<!-- AFTER -->
<MudTreeView Hover="true" SelectionMode="SelectionMode.SingleSelection">
    <MudTreeViewItem Selected="@_isActive" ExpandButtonIcon="@Icons.Material.Filled.Folder">
    </MudTreeViewItem>
</MudTreeView>
```

**Important Distinction**:
- `ExpandButtonIcon`: Icon for the expand/collapse button
- `IconExpanded`: Alternate icon shown when item is expanded (new feature)

**Event Changes**:
- `ActivatedValueChanged` ? `SelectedValueChanged`

**Data Structure (v9+)**:
```csharp
// Use TreeItemData<T> for Items and ServerData
public TreeItemData<T> Items { get; set; }
```

---

### MudList & MudListItem (Now Generic)

**Critical**: MudList is now generic - must specify type parameter

```razor
<!-- BEFORE -->
<MudList>
    <MudListItem>Item 1</MudListItem>
</MudList>

<!-- AFTER -->
<MudList T="string">  <!-- Type parameter REQUIRED -->
    <MudListItem T="string">Item 1</MudListItem>
</MudList>
```

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudList Clickable="false" DisableGutters="true" DisablePadding="true">
    <MudListItem Avatar="@icon" AvatarClass="custom-class">
    </MudListItem>
</MudList>

<!-- AFTER -->
<MudList T="string" ReadOnly="true" Gutters="false" Padding="false">
    <MudListItem T="string">
        <AvatarContent>
            <MudAvatar>@icon</MudAvatar>
        </AvatarContent>
    </MudListItem>
</MudList>
```

**Important Changes**:
- `Clickable` ? `ReadOnly` (inverted)
- `SelectedItem` removed - use `@bind-SelectedValue` only
- `DisablePadding` default changed: **No padding is default in v7+**
- Avatar now uses RenderFragment instead of parameters

---

### MudChipSet & MudChip (Major Breaking Changes)

**Critical**: Now generic with data-driven selection

```razor
<!-- BEFORE -->
<MudChipSet>
    <MudChip>Item 1</MudChip>
    <MudChip>Item 2</MudChip>
</MudChipSet>

<!-- AFTER -->
<MudChipSet T="string" @bind-SelectedValue="_selected">
    <MudChip T="string" Value="@("Item 1")">Item 1</MudChip>
    <MudChip T="string" Value="@("Item 2")">Item 2</MudChip>
</MudChipSet>
```

**Selection Mode Migration**:
```csharp
// BEFORE: MultiSelection + Mandatory parameters
<MudChipSet MultiSelection="false" Mandatory="true">

// AFTER: SelectionMode enum
<MudChipSet T="string" SelectionMode="SelectionMode.SingleSelection">
```

| Old Parameters | New SelectionMode |
|----------------|-------------------|
| `MultiSelection="true"` | `SelectionMode.MultiSelection` |
| `MultiSelection="false"` `Mandatory="true"` | `SelectionMode.SingleSelection` |
| `MultiSelection="false"` `Mandatory="false"` | `SelectionMode.ToggleSelection` |

**Default Behavior Change**:
- v6: Toggle selection (can deselect)
- v7+: `SelectionMode.SingleSelection` (cannot deselect)
- Use `SelectionMode.ToggleSelection` for v6 behavior

**Parameter Changes**:
| Old | New |
|-----|-----|
| `SelectedChip` | `SelectedValue` |
| `SelectedChips` | `SelectedValues` |
| `Filter` | `CheckMark` |
| `Link` | `Href` |
| `Avatar`, `AvatarClass` | `AvatarContent` (RenderFragment) |
| `DisableRipple` | `Ripple` (inverted) |

**Value vs Text**:
```razor
<!-- For T="string", if Text equals Value, can omit Value -->
<MudChip T="string" Text="Option 1">  <!-- Value automatically = "Option 1" -->

<!-- For other types, must set Value explicitly -->
<MudChip T="int" Value="1" Text="First Option">
```

**Event Changes**:
- `IsSelected` ? `Selected`
- `IsSelectedChanged` ? `SelectedChanged`

---

### MudAutocomplete

**Critical Default Behavior Change**: Opens on focus by default in v7+

```razor
<!-- To restore v6 behavior (don't open on focus) -->
<MudAutocomplete T="string" OpenOnFocus="false" ...>  <!-- Available in v7.2.0+ -->
```

**SearchFunc Signature Change**:
```csharp
// BEFORE (v6)
private async Task<IEnumerable<string>> SearchFunc(string value)
{
    // Search logic
}

// AFTER (v7+)
private async Task<IEnumerable<string>> SearchFunc(string value, CancellationToken token)
{
    // Search logic - honor cancellation token
}
```

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudAutocomplete Direction="Direction.Bottom" OffsetX="10" OffsetY="5"
                 SelectOnClick="true">

<!-- AFTER -->
<MudAutocomplete AnchorOrigin="Origin.BottomCenter"
                 TransformOrigin="Origin.TopCenter"
                 SelectOnActivation="true">
```

**Method Changes**:
```csharp
// BEFORE
ChangeMenuAsync()
SelectOption()
Clear()

// AFTER
OpenMenuAsync() / CloseMenuAsync()
await SelectOptionAsync()
await ClearAsync()
```

**Event Changes**:
- `IsOpen` ? `Open`
- `IsOpenChanged` ? `OpenChanged`

**Template Behavior Change**:
- `ItemDisabledTemplate` and `ItemSelectedTemplate` now display even without `ItemTemplate`

---

### MudDialog & Dialog Services

**Namespace Removal**:
```csharp
// BEFORE
using MudBlazor.Dialog;

// AFTER
// Namespace removed - remove this using statement
```

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudDialog ClassBackground="bg-class" ClassAction="action-class"
           ClassContent="content-class" DisableSidePadding="true">

<!-- AFTER -->
<MudDialog BackgroundClass="bg-class" ActionsClass="action-class"
           ContentClass="content-class" Gutters="false">
```

**DialogOptions Changes**:
```csharp
// BEFORE
var options = new DialogOptions 
{ 
    DisableBackdropClick = true 
};

// AFTER
var options = new DialogOptions 
{ 
    BackdropClick = false  // Inverted
};
```

**Method Changes**:
```csharp
// BEFORE
dialog.Show();
dialog.Close();

// AFTER
await dialog.ShowAsync();
await dialog.CloseAsync();
```

**DialogResult Changes**:
```csharp
// BEFORE
if (result.Cancelled)

// AFTER
if (result.Canceled)  // Single 'l'
```

**Removed Properties**:
- `AreParametersRendered` - no replacement needed
- `public DialogService` - now protected

---

### MudDrawer

**Removed Properties**:
```razor
<!-- BEFORE -->
<MudDrawer DrawerWidth="300px" DrawerHeightTop="100px">

<!-- AFTER -->
<MudDrawer Width="300px" Height="100px">
```

**Other Removed**:
- `PreserveOpenState` - no longer needed
- `DrawerHeightBottom` - use `Height`

---

### MudGrid

**Critical Spacing Change**: Default spacing unit halved (8px ? 4px)

**To maintain same visual gaps**:
```razor
<!-- BEFORE: Spacing="3" = 24px -->
<MudGrid Spacing="3">

<!-- AFTER: Double the value to maintain 24px -->
<MudGrid Spacing="6">  <!-- 6 × 4px = 24px -->
```

**Notes**:
- Default `Spacing` value already doubled (6 instead of 3)
- Maximum increased from 10 to 20
- If you didn't explicitly set `Spacing`, no change needed

---

### MudTable & MudDataGrid

**ServerData Signature Change**:
```csharp
// BEFORE (v6)
private async Task<TableData<MyItem>> ServerData(TableState state)
{
    // Load data
}

// AFTER (v7+)
private async Task<TableData<MyItem>> ServerData(TableState state, CancellationToken token)
{
    // Load data - honor cancellation token
}
```

**MudTable Parameter Changes**:
```razor
<!-- BEFORE -->
<MudTable ServerData="@ServerData" DisableRowsPerPage="true">
    <MudTHeadRow IsExpandable="true" IsCheckable="true">
    <MudTFootRow IsExpandable="true" IsCheckable="true">
    <MudTableGroupRow IsExpanded="true" IsCheckable="true">
    <MudTr IsExpandable="true" IsEditable="true">
</MudTable>

<!-- AFTER -->
<MudTable ServerData="@ServerData" HideRowsPerPage="true">
    <MudTHeadRow Expandable="true" Checkable="true">
    <MudTFootRow Expandable="true" Checkable="true">
    <MudTableGroupRow Expanded="true" Checkable="true">
    <MudTr Expandable="true" Editable="true">
</MudTable>
```

**Event Changes**:
- Mouse events ? Pointer events (`MouseEventArgs` ? `PointerEventArgs`)

**Removed**:
- `RightAlignSmall` - automatic alignment now
- `QuickColumns` and `MudColumn` - reflection-based feature removed

**MudDataGrid Changes**:
```csharp
// FilterDefinition changes
// BEFORE
FilterDefinition.GenerateFilterExpression()

// AFTER
FilterExpressionGenerator.GenerateExpression()
```

**TemplateColumn Default Changes**:
| Parameter | v6 Default | v7+ Default |
|-----------|------------|-------------|
| `ShowColumnOptions` | true | false |
| `Resizable` | true | false |
| `DragAndDropEnabled` | true | false |
| `Sortable` | true | false |
| `Filterable` | true | false |

---

### MudPicker Components

**Common Picker Parameter Changes**:
```razor
<!-- BEFORE -->
<MudPicker ClassAction="action-class" InputIcon="@Icons.Filled.Calendar"
           InputVariant="Variant.Outlined" ToolBarClass="toolbar">

<!-- AFTER -->
<MudPicker ActionsClass="action-class" AdornmentIcon="@Icons.Material.Filled.Calendar"
           Variant="Variant.Outlined" ToolbarClass="toolbar">
```

**Method Changes**:
```csharp
// BEFORE
picker.Close();

// AFTER
await picker.CloseAsync();
```

**Obsolete Parameters Removed**:
- `AllowKeyboardInput` - removed, unused

---

### MudColorPicker

**All `Disable*` ? `Show*` with inversion**:
```razor
<!-- BEFORE -->
<MudColorPicker DisableSliders="true" DisablePreview="false"
                DisableModeSwitch="true" DisableInputs="true"
                DisableColorField="false" DisableAlpha="true"
                DisableDragEffect="true">

<!-- AFTER -->
<MudColorPicker ShowSliders="false" ShowPreview="true"
                ShowModeSwitch="false" ShowInputs="false"
                ShowColorField="true" ShowAlpha="false"
                DragEffect="false">
```

---

### MudSelect

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudSelect Direction="Direction.Bottom" OffsetX="10" OffsetY="5">

<!-- AFTER -->
<MudSelect AnchorOrigin="Origin.BottomCenter"
           TransformOrigin="Origin.TopCenter">
```

**Method Changes**:
```csharp
// BEFORE
await select.ClearAsync();

// AFTER
select.Clear();  // No longer async
```

---

### MudMenu & MudMenuItem

**Parameter Removals**:
```razor
<!-- BEFORE -->
<MudMenu Link="/path" Target="_blank" HtmlTag="div" ButtonType="ButtonType.Submit">

<!-- AFTER -->
<MudMenu>  <!-- Parameters removed - had no function -->
```

**Method Changes**:
```csharp
// BEFORE
menu.ToggleMenu();
menu.OpenMenu();
menu.CloseMenu();
menu.Activate();

// AFTER
await menu.ToggleMenuAsync();
await menu.OpenMenuAsync();
await menu.CloseMenuAsync();
// Activate() removed
```

**Event Changes**:
- `OnTouch` removed - use `OnClick`
- `OnAction` removed - use `OnClick`
- `IsOpen` ? `Open`
- `IsOpenChanged` ? `OpenChanged`

**Visibility Changes**:
```csharp
// MudMenuItem properties changed to protected
// UriHelper, JsApiService no longer public
```

---

### MudRadioGroup

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudRadioGroup @bind-SelectedOption="_option">
    <MudRadio Option="1">Option 1</MudRadio>
</MudRadioGroup>

<!-- AFTER -->
<MudRadioGroup @bind-Value="_option">
    <MudRadio Value="1">Option 1</MudRadio>
</MudRadioGroup>
```

**Method Changes**:
```csharp
// BEFORE
radioGroup.ResetValue();

// AFTER
await radioGroup.ResetValueAsync();
```

---

### MudSnackbar & SnackbarService

**Method Changes**:
```csharp
// BEFORE
snackbarService.AddNew("Message", Severity.Info);

// AFTER
snackbarService.Add("Message", Severity.Info);
```

**Security Change - MarkupString**:
```csharp
// BEFORE - Unsafe, removed
snackbarService.Add(new MarkupString("<b>HTML</b>"), ...);

// AFTER - Explicit overload for safety
snackbarService.Add(new MarkupString("<b>HTML</b>"), Severity.Info);
```

---

### MudAvatar

**Breaking Change**: Image parameter removed

```razor
<!-- BEFORE -->
<MudAvatar Image="/images/avatar.jpg" Alt="User">

<!-- AFTER -->
<MudAvatar>
    <MudImage Src="/images/avatar.jpg" Alt="User" />
</MudAvatar>
```

---

### MudSlider

**Type Constraint Change**:
```csharp
// BEFORE - accepted any type
<MudSlider T="decimal">

// AFTER - constrained to INumber
<MudSlider T="decimal">  // Still works, but type must implement INumber
```

**Nullable Value**:
```razor
<!-- BEFORE - for nullable types -->
<MudSlider T="int?" @bind-Value="_nullableValue">

<!-- AFTER - for nullable types -->
<MudSlider T="int" @bind-NullableValue="_nullableValue">
```

**ValueLabelContent Type Change**:
```csharp
// BEFORE
RenderFragment<T> ValueLabelContent

// AFTER
RenderFragment<SliderContext<T>> ValueLabelContent
```

**Parameter Removed**:
- `Text` parameter removed

---

### MudFileUpload & MudFilePicker

**Visual Breaking Change**: Top margin removed from MudFileUpload

**MudFilePicker Template Change**:
```razor
<!-- BEFORE -->
<MudFilePicker T="IBrowserFile">
    <ButtonTemplate>
        <MudButton>Select File</MudButton>
    </ButtonTemplate>
</MudFilePicker>

<!-- AFTER -->
<MudFilePicker T="IBrowserFile">
    <ActivatorContent>
        <MudButton>Select File</MudButton>  <!-- Any button in here activates picker -->
    </ActivatorContent>
</MudFilePicker>
```

**Critical**: All `MudButton` components within `ActivatorContent` will activate the file picker

**HtmlTag Removal**:
```razor
<!-- BEFORE -->
<MudFileUpload HtmlTag="label">

<!-- AFTER -->
<MudFileUpload>  <!-- HtmlTag removed, no longer needed -->
```

---

### MudSwipeArea

**Method Changes**:
```csharp
// BEFORE
GetSwipeDelta()
OnSwipe

// AFTER
// Both removed - use OnSwipeEnd instead
```

---

### MudMessageBox

**Method Changes**:
```csharp
// BEFORE
messageBox.Show();

// AFTER
await messageBox.ShowAsync();
```

**Parameter Changes**:
- `IsVisible` ? `Visible`
- `IsVisibleChanged` ? `VisibleChanged`

---

### MudTooltip

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudTooltip Delayed="500">

<!-- AFTER -->
<MudTooltip Delay="500">
```

**Visibility Changes**:
- `IsVisible` ? `Visible`
- `IsVisibleChanged` ? `VisibleChanged`

---

### MudCarousel

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudCarousel ShowDelimiters="true" DelimitersColor="Color.Primary">
    <MudCarouselItem IsVisible="true">
    </MudCarouselItem>
</MudCarousel>

<!-- AFTER -->
<MudCarousel ShowBullets="true" BulletsColor="Color.Primary">
    <MudCarouselItem Visible="true">
    </MudCarouselItem>
</MudCarousel>
```

---

### MudHidden

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudHidden @bind-IsHidden="_hidden">

<!-- AFTER -->
<MudHidden @bind-Hidden="_hidden">
```

---

### MudCard

**Visual Breaking Change**: Card content now fills remaining vertical space by default

---

### MudTabs

**CSS Class Renaming**:
```css
/* BEFORE */
.mud-tabs-toolbar
.mud-tabs-toolbar-transparent

/* AFTER */
.mud-tabs-tabbar
.mud-tabs-tabbar-transparent
```

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudTabs DisableSliderAnimation="true">

<!-- AFTER -->
<MudTabs SliderAnimation="false">  <!-- Inverted -->
```

---

### MudTimeline

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudTimeline DisableModifiers="true">

<!-- AFTER -->
<MudTimeline Modifiers="false">  <!-- Inverted -->
```

---

### MudButtonGroup

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudButtonGroup VerticalAlign="true">

<!-- AFTER -->
<MudButtonGroup Vertical="true">
```

---

### MudToggleGroup

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudToggleGroup T="string" Outline="true" Dense="true">

<!-- AFTER -->
<MudToggleGroup T="string" Outlined="true" Size="Size.Small">
```

**Note**: `Dense` removed in favor of `Size` parameter

---

### MudDropZone & MudDropContainer

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudDropContainer T="MyItem" ItemIsDisabled="@IsItemDisabled">
    <MudDropZone ItemIsDisabled="@IsItemDisabled">
    </MudDropZone>
</MudDropContainer>

<!-- AFTER -->
<MudDropContainer T="MyItem" ItemDisabled="@IsItemDisabled">
    <MudDropZone ItemDisabled="@IsItemDisabled">
    </MudDropZone>
</MudDropContainer>
```

---

### MudPageContentNavigation & MudPageContentSection

**Method Changes**:
```csharp
// BEFORE
pageContent.Update();

// AFTER
((IMudStateHasChanged)pageContent).StateHasChanged();
```

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudPageContentSection IsActive="true">

<!-- AFTER -->
<MudPageContentSection Active="true">
```

---

### MudPopover

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudPopover Direction="Direction.Bottom" OffsetX="10" OffsetY="5">

<!-- AFTER -->
<MudPopover AnchorOrigin="Origin.BottomCenter"
            TransformOrigin="Origin.TopCenter">
```

---

### MudVirtualize & MudPopoverProvider

**Parameter Changes**:
```razor
<!-- BEFORE -->
<MudVirtualize IsEnabled="false">
<MudPopoverProvider IsEnabled="false">

<!-- AFTER -->
<MudVirtualize Enabled="false">
<MudPopoverProvider Enabled="false">
```

---

### All Button Components

**Title & AriaLabel Removal**:
```razor
<!-- BEFORE -->
<MudButton Title="Click me" AriaLabel="Button label">

<!-- AFTER -->
<MudButton title="Click me" aria-label="Button label">
```

**Note**: Use native HTML attributes (lowercase) instead

**Component List**:
- MudButton
- MudIconButton
- MudFab
- MudToggleIconButton

---

### MudListItem & MudMenuItem (Href Behavior)

**Breaking Change**: When `Href` is set, renders as `<a>` instead of `<div>`

```razor
<!-- AFTER v7+ -->
<MudListItem Href="/page">  <!-- Renders as <a> tag -->
<MudMenuItem Href="/page">  <!-- Renders as <a> tag -->
```

---

## Color & Theme Migration

### Grey ? Gray Rename

**All instances must be renamed**:

```csharp
// BEFORE
Colors.Grey.Default
Colors.Grey.Lighten1

// AFTER
Colors.Gray.Default
Colors.Gray.Lighten1
```

**CSS Classes**:
```css
/* BEFORE */
.mud-grey-text
.mud-grey-background

/* AFTER */
.mud-gray-text
.mud-gray-background
```

**CSS Variables**:
```css
/* BEFORE */
--mud-palette-grey

/* AFTER */
--mud-palette-gray
```

**Search Patterns**:
- Code: `Colors\.Grey` ? `Colors.Gray`
- CSS: `mud-grey` ? `mud-gray`
- CSS: `--mud-palette-grey` ? `--mud-palette-gray`

---

### MudTheme Changes

```csharp
// BEFORE (v6)
var theme = new MudTheme
{
    Palette = new Palette
    {
        Primary = Colors.Blue.Default,
        ChipDefault = Colors.Grey.Lighten1,
        ChipDefaultHover = Colors.Grey.Lighten2
    }
};

// AFTER (v7+)
var theme = new MudTheme
{
    PaletteLight = new PaletteLight  // Renamed
    {
        Primary = Colors.Blue.Default
        // ChipDefault and ChipDefaultHover removed
        // Chips now use normal palette settings
    },
    PaletteDark = new PaletteDark
    {
        // Dark theme settings
    }
};
```

**Changes**:
- `Palette` ? `PaletteLight`
- `Palette` is now abstract
- `ChipDefault` and `ChipDefaultHover` removed from palette
- Type changed from `Palette` to `PaletteLight` to match `PaletteDark`

---

## Service Migration

### Unified Breakpoint Services

**All replaced by `BrowserViewportService`**:

```csharp
// BEFORE (v6) - All deprecated/removed
using MudBlazor.Services;

@inject IResizeService ResizeService
@inject IResizeListenerService ResizeListenerService
@inject IBreakpointService BreakpointService
@inject ResizeBasedService ResizeBasedService

// AFTER (v7+)
using MudBlazor.Services;

@inject IBrowserViewportService BrowserViewport
```

**Usage Example**:
```csharp
// BEFORE
var breakpoint = await BreakpointService.GetBreakpoint();

// AFTER
var breakpoint = await BrowserViewport.GetCurrentBreakpointAsync();
```

---

### Service Configuration Changes

**Action-based configuration**:

```csharp
// BEFORE (v6)
services.AddMudBlazorSnackbar(new SnackbarConfiguration
{
    PositionClass = Defaults.Classes.Position.BottomRight
});

services.AddMudBlazorResizeListener(new ResizeOptions());
services.AddMudPopoverService(new PopoverOptions());

// AFTER (v7+)
services.AddMudBlazorSnackbar(config =>
{
    config.PositionClass = Defaults.Classes.Position.BottomRight;
});

services.AddMudBlazorResizeListener(config => { });
services.AddMudPopoverService(config => { });
```

**Affected Methods**:
- `AddMudBlazorSnackbar`
- `AddMudBlazorResizeListener`
- `AddMudBlazorResizeObserver`
- `AddMudBlazorResizeObserverFactory`
- `AddMudPopoverService`
- `AddMudServices`

---

### ScrollManager Changes

**Async Method Changes**:
```csharp
// BEFORE
scrollManager.ScrollToTop();
scrollManager.ScrollToBottom();

// AFTER
await scrollManager.ScrollToTopAsync();
await scrollManager.ScrollToBottomAsync();
```

**Removed Property**:
- `Selector` - obsolete property removed

---

## Input Components Migration

### Common Input Changes

**Affects**: MudTextField, MudInput, and all inheriting from MudBaseInput

**Event Handler Changes**:
```razor
<!-- BEFORE -->
<MudTextField OnKeyPress="@HandleKeyPress" />

<!-- AFTER -->
<MudTextField OnKeyDown="@HandleKeyDown" />
```

**Method Removal**:
```csharp
// Protected virtual method removed
InvokeKeyPress()  // No replacement
```

**Visual Change**: Inputs without labels automatically shrink by 4px in height

---

### MudFormComponent Changes

**Affects**: All form inputs (MudTextField, MudCheckBox, MudSwitch, MudFileUpload, etc.)

**Method Changes**:
```csharp
// BEFORE
formComponent.Reset();
formComponent.ResetValue();
formComponent.BeginValidate();
formComponent.BeginValidateAfter();

// AFTER
await formComponent.ResetAsync();
await formComponent.ResetValueAsync();
await formComponent.BeginValidateAsync();
await formComponent.BeginValidationAfterAsync();
```

---

### MudForm Changes

**Method Changes**:
```csharp
// BEFORE
form.Reset();

// AFTER
await form.ResetAsync();
```

---

### MudCollapse Changes

**Event Changes**:
```razor
<!-- BEFORE -->
<MudCollapse AnimationEnd="@HandleAnimationEnd">

<!-- AFTER -->
<MudCollapse AnimationEndAsync="@HandleAnimationEndAsync">
```

---

## Component Removals & Replacements

### Removed Components

```razor
<!-- BEFORE -->
<MudAppBarSpacer />
<MudToolBarSpacer />
<MudTextFieldString @bind-Value="_text" />
<MudSparkline Data="_data" />

<!-- AFTER -->
<MudSpacer />  <!-- Unified spacer -->
<MudTextField T="string" @bind-Value="_text" />
<!-- MudSparkline completely removed - no replacement -->
```

---

### Removed Enums/Classes

```csharp
// BEFORE
AlertTextPosition.Start

// AFTER
// Enum removed - use ContentAlignment instead
```

---

### Removed Methods/Properties

```csharp
// Color Management - BEFORE
ColorManager.DoSomething()
ColorTransformation.Transform()

// Color Management - AFTER
MudColor.Parse()  // Use MudColor instead

// Numeric Comparison - BEFORE
NumericConverter.AreEqual(a, b)

// Numeric Comparison - AFTER
DoubleEpsilonEqualityComparer.Default.Equals(a, b)

// Chart Interpolation - BEFORE
ILineInterpolator.InterpolationRequired
NoInterpolation

// Chart Interpolation - AFTER
// Both removed - no replacement

// Task Extension - BEFORE
task.AndForget(showErrorOnEventException);

// Task Extension - AFTER
task.CatchAndLog(showErrorOnEventException);  // Renamed
```

---

## State Management Changes

### Force Render Pattern

```csharp
// BEFORE (v6)
component.ForceRender();
component.Refresh();
component.Update();

// AFTER (v7+)
((IMudStateHasChanged)component).StateHasChanged();
```

**Affected Components**:
- MudRender
- MudDialogInstance
- MudElement
- MudPageContentNavigation

---

## Async Method Naming Convention

**Pattern**: Protected async methods now have `Async` suffix

**Examples**:
```csharp
// BEFORE
protected virtual Task InvokeKeyPress()
protected virtual Task Toggle()
protected virtual Task Reset()
protected virtual Task Select()
protected virtual Task AnimationEnd()

// AFTER
// InvokeKeyPress removed entirely
protected virtual Task ToggleAsync()
protected virtual Task ResetAsync()
protected virtual Task SelectAsync()
protected virtual Task AnimationEndAsync()
```

**Impact**: Only affects custom components inheriting from MudBlazor components

---

## Breaking Behavior Changes

### 1. MudAutocomplete Opens on Focus
**v6**: Menu does not open automatically  
**v7+**: Menu opens when input receives focus

```razor
<!-- To restore v6 behavior (available in v7.2.0+) -->
<MudAutocomplete T="string" OpenOnFocus="false" ...>
```

---

### 2. MudChipSet Default Selection
**v6**: Toggle selection (can deselect all)  
**v7+**: Single selection (must have one selected)

```razor
<!-- To restore v6 behavior -->
<MudChipSet T="string" SelectionMode="SelectionMode.ToggleSelection">
```

---

### 3. MudTreeView Selection Active
**v6**: Not selectable by default  
**v7+**: Selection active with `SelectionMode.SingleSelection`

```razor
<!-- To disable selection -->
<MudTreeView T="MyType" ReadOnly="true">
```

---

### 4. MudGrid Spacing Unit Changed
**v6**: 1 spacing unit = 8px  
**v7+**: 1 spacing unit = 4px

**Action**: Double explicit spacing values or accept default change

---

### 5. MudList Default Padding Changed
**v6**: Padding enabled by default  
**v7+**: No padding by default

```razor
<!-- To restore v6 behavior -->
<MudList T="string" Padding="true">
```

---

### 6. MudCard Content Layout
**v7+**: Card content fills remaining vertical space by default

---

### 7. MudDataGrid TemplateColumn Defaults
Multiple defaults changed from `true` to `false`:
- ShowColumnOptions
- Resizable
- DragAndDropEnabled
- Sortable
- Filterable

---

## Migration Workflow

### Phase 1: Setup & Infrastructure
1. ? Verify .NET 7+ target framework
2. ? Update MudBlazor package to v8
3. ? Add `<MudPopoverProvider/>` to layout
4. ? Remove `using MudBlazor.Dialog;` statements
5. ? Enable MudBlazor compile-time analyzer

---

### Phase 2: Global Find/Replace
Execute these in order:

```regex
# Icons namespace
Icons\.Filled\.          ? Icons.Material.Filled.
Icons\.Outlined\.        ? Icons.Material.Outlined.
Icons\.Rounded\.         ? Icons.Material.Rounded.
Icons\.Sharp\.           ? Icons.Material.Sharp.
Icons\.TwoTone\.         ? Icons.Material.TwoTone.

# Colors
Colors\.Grey\.           ? Colors.Gray.
\.mud-grey               ? .mud-gray
--mud-palette-grey       ? --mud-palette-gray

# Components
<MudAppBarSpacer         ? <MudSpacer
<MudToolBarSpacer        ? <MudSpacer
<MudTextFieldString      ? <MudTextField T="string"

# Common renames (no inversion)
IsEnabled=               ? Enabled=
IsVisible=               ? Visible=
IsSelected=              ? Selected=
IsExpanded=              ? Expanded=
IsOpen=                  ? Open=
IsChecked=               ? Checked=
IsHidden=                ? Hidden=

# Method renames
\.AddNew\(               ? .Add(
\.AndForget\(            ? .CatchAndLog(
```

---

### Phase 3: Component-Specific Migration

**For each file**:

1. **Add Generic Types**:
   ```razor
   <MudChipSet          ? <MudChipSet T="string"
   <MudChip             ? <MudChip T="string"
   <MudList             ? <MudList T="string"
   <MudListItem         ? <MudListItem T="string"
   ```

2. **Inverted Parameters** (Manual review required):
   - Search: `Disable(Ripple|Gutters|Padding|Elevation|UnderLine|RowsPerPage|BackdropClick|SidePadding|Borders)`
   - For each match: Rename and **invert the boolean value/condition**

3. **Fix MudExpansionPanel**:
   ```razor
   IsInitiallyExpanded="true" ? @bind-Expanded="_expanded"
   CollapseAll()              ? await CollapseAllAsync()
   ```

4. **Fix MudTreeView**:
   ```razor
   MultiSelection="true"      ? SelectionMode="SelectionMode.MultiSelection"
   ActivatedValueChanged=     ? SelectedValueChanged=
   ```

5. **Fix Selection Components**:
   - MudChipSet: Add `SelectionMode`, change events
   - MudList: Add `T`, change `Clickable` ? `ReadOnly`
   - MudRadioGroup: `SelectedOption` ? `Value`

6. **Fix Picker Components**:
   ```razor
   ClassAction=              ? ActionsClass=
   InputIcon=                ? AdornmentIcon=
   InputVariant=             ? Variant=
   Close()                   ? await CloseAsync()
   ```

7. **Fix Dialog Usage**:
   ```csharp
   ClassBackground=          ? BackgroundClass=
   DisableBackdropClick=     ? BackdropClick= (inverted)
   dialog.Show()             ? await dialog.ShowAsync()
   result.Cancelled          ? result.Canceled
   ```

8. **Fix Method Signatures**:
   ```csharp
   SearchFunc(string value)  ? SearchFunc(string value, CancellationToken token)
   ServerData(TableState s)  ? ServerData(TableState s, CancellationToken token)
   ```

9. **Fix Avatar Usage**:
   ```razor
   <MudAvatar Image="..." Alt="...">
   ?
   <MudAvatar><MudImage Src="..." Alt="..." /></MudAvatar>
   ```

10. **Fix Color Pickers**:
    ```razor
    DisableSliders="true"     ? ShowSliders="false"
    DisablePreview="false"    ? ShowPreview="true"
    ```

---

### Phase 4: Code-Behind Migration

1. **Async Method Updates**:
   ```csharp
   // Search for these patterns and add Async suffix
   protected.*Task.*Reset\(
   protected.*Task.*Select\(
   protected.*Task.*Toggle\(
   protected.*Task.*Collapse\(
   protected.*Task.*Expand\(
   ```

2. **Service Injection Updates**:
   ```csharp
   @inject IResizeService ? @inject IBrowserViewportService
   @inject IBreakpointService ? @inject IBrowserViewportService
   ```

3. **State Management Updates**:
   ```csharp
   component.ForceRender() ? ((IMudStateHasChanged)component).StateHasChanged()
   ```

---

### Phase 5: Validation

1. ? **Build Project**: Resolve compilation errors
2. ? **Review Analyzer Warnings**: Fix all MudBlazor parameter warnings
3. ? **Visual Inspection**: Check layout changes (Grid spacing, Card layout, Input heights)
4. ? **Test Selection Components**: Verify new default behaviors
5. ? **Test Dialogs**: Ensure all dialogs open/close correctly
6. ? **Test Menus/Dropdowns**: Verify MudAutocomplete, MudSelect, MudMenu
7. ? **Test Tables/Grids**: Verify ServerData with CancellationToken
8. ? **Test Forms**: Verify validation and reset functionality

---

## Common Pitfalls for AI Agents

### ? Pitfall 1: Forgetting Boolean Inversion
```razor
<!-- WRONG -->
DisableRipple="true" ? Ripple="true"

<!-- CORRECT -->
DisableRipple="true" ? Ripple="false"
```

### ? Pitfall 2: Missing Generic Type Parameters
```razor
<!-- WRONG -->
<MudChipSet>
    <MudChip>Item</MudChip>
</MudChipSet>

<!-- CORRECT -->
<MudChipSet T="string">
    <MudChip T="string">Item</MudChip>
</MudChipSet>
```

### ? Pitfall 3: Assuming Compiler Errors
**Issue**: Compiler won't catch unknown parameters due to attribute splatting  
**Solution**: Always review and resolve analyzer warnings

### ? Pitfall 4: Not Updating Method Signatures
```csharp
// WRONG - Missing CancellationToken
private async Task<TableData<T>> ServerData(TableState state)

// CORRECT
private async Task<TableData<T>> ServerData(TableState state, CancellationToken token)
```

### ? Pitfall 5: Ignoring Default Behavior Changes
- MudAutocomplete now opens on focus
- MudChipSet now requires single selection
- MudGrid spacing unit changed
- MudList padding default changed

**Solution**: Explicitly set parameters to restore v6 behavior if needed

### ? Pitfall 6: Not Adding MudPopoverProvider
**Issue**: App will fail at runtime  
**Solution**: Always add to layout before other providers

### ? Pitfall 7: Incorrect Event Name Changes
```razor
<!-- WRONG -->
IsOpenChanged ? OpenedChanged

<!-- CORRECT -->
IsOpenChanged ? OpenChanged
```

### ? Pitfall 8: Missing Await Keywords
```csharp
// WRONG
panel.CollapseAllAsync();
dialog.CloseAsync();

// CORRECT
await panel.CollapseAllAsync();
await dialog.CloseAsync();
```

---

## Quick Reference: Parameter Mapping

### Inverted Parameters (Require Boolean Inversion)
| Old | New | Default Changed |
|-----|-----|-----------------|
| DisableRipple | Ripple | No |
| DisableGutters | Gutters | No |
| DisablePadding | Padding | **Yes** (MudList) |
| DisableElevation | DropShadow | No |
| DisableUnderLine | Underline | No |
| DisableRowsPerPage | PageSizeSelector/HideRowsPerPage | No |
| DisableBackdropClick | BackdropClick | No |
| DisableSidePadding | Gutters | No |
| DisableBorders | Outlined | No |
| DisableSliders | ShowSliders | No |
| DisablePreview | ShowPreview | No |
| DisableColorField | ShowColorField | No |
| DisableAlpha | ShowAlpha | No |
| DisableLegend | ShowLegend | No |
| DisableToolbar | ShowToolbar | No |

### "Is" Prefix Removal (No Inversion)
| Old | New |
|-----|-----|
| IsEnabled | Enabled |
| IsVisible | Visible |
| IsSelected | Selected |
| IsExpanded | Expanded |
| IsOpen | Open |
| IsChecked | Checked |
| IsHidden | Hidden |
| IsEditable | Editable |
| IsExpandable | Expandable |
| IsCheckable | Checkable |
| IsInitiallyExpanded | Expanded |

### Direct Renames
| Component | Old | New |
|-----------|-----|-----|
| All Buttons | Link | Href |
| MudFab | Icon | StartIcon |
| MudCheckBox/MudSwitch | Checked | Value |
| MudRadio | Option | Value |
| MudRadioGroup | SelectedOption | Value |
| MudBadge | Bottom/Left/Start | Origin |
| MudDialog | ClassBackground | BackgroundClass |
| MudDialog | ClassAction | ActionsClass |
| MudDialog | ClassContent | ContentClass |
| Pickers | ClassAction | ActionsClass |
| Pickers | InputIcon | AdornmentIcon |
| Pickers | InputVariant | Variant |
| MudTooltip | Delayed | Delay |
| MudCarousel | ShowDelimiters | ShowBullets |
| MudCarousel | DelimitersColor | BulletsColor |
| MudProgress | Minimum/Maximum | Min/Max |
| MudToggleGroup | Outline | Outlined |
| MudAlert | AlertTextPosition | ContentAlignment |
| MudButtonGroup | VerticalAlign | Vertical |
| MudList/Item | Avatar/AvatarClass | AvatarContent |
| MudFilePicker | ButtonTemplate | ActivatorContent |
| MudListItem | AdornmentColor | ExpandIconColor |

---

## Version-Specific Notes

### v7.0.0
- All breaking changes listed above

### v7.2.0
- Added `OpenOnFocus` parameter to MudAutocomplete

### v8.0.0
- Additional stability improvements
- Performance optimizations

### v9.0.0 (if applicable)
- TreeItemData<T> structure for MudTreeView

---

## Testing Checklist

After migration, verify:

- [ ] Application starts without errors
- [ ] MudPopoverProvider is present in layout
- [ ] All icon references updated to Icons.Material.*
- [ ] No Grey references remain (all changed to Gray)
- [ ] All analyzer warnings resolved
- [ ] Generic components have type parameters
- [ ] Selection behavior matches expectations
- [ ] Dialog flows work correctly
- [ ] Form validation and reset work
- [ ] Table/Grid paging and sorting work
- [ ] File uploads work
- [ ] Menu and dropdowns function
- [ ] Tooltips display correctly
- [ ] Responsive behavior correct (breakpoints)
- [ ] Spacing looks correct (Grid, Lists, etc.)
- [ ] Avatar content renders properly
- [ ] Color pickers show all controls
- [ ] Snackbar notifications appear
- [ ] Drawer opens and closes
- [ ] Expansion panels expand/collapse
- [ ] Tree view selection works
- [ ] Chip selection works
- [ ] All async methods awaited properly

---

## Emergency Rollback

If migration issues are critical:

1. Revert MudBlazor package to v6.x.x
2. Remove `<MudPopoverProvider/>`
3. Revert all code changes
4. Consider gradual migration per component

---

## Additional Resources

- [MudBlazor v7 Migration Guide (Official)](https://github.com/MudBlazor/MudBlazor/issues/7533)
- [MudBlazor Documentation](https://mudblazor.com/)
- [Breaking Changes PR List](https://github.com/MudBlazor/MudBlazor/pulls?q=is%3Apr+label%3Abreaking-change)

---

## Document Version
**Version**: 1.0  
**Last Updated**: 2024  
**Compatible With**: MudBlazor v6 ? v8 migration  
**Target Audience**: AI coding assistants (GitHub Copilot, etc.)
