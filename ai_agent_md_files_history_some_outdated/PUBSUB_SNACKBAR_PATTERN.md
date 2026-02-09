# PubSub Channel ? Snackbar Consumer Pattern

## Overview

Every Blazor page in this application that performs background work uses a **PubSub Channel consumer** to bridge the gap between the Logic layer (which runs on background threads) and the Blazor UI (which shows MudBlazor Snackbar notifications). This document describes the standard pattern, its variations, and how to correctly manage snackbar lifecycle when long-running operations complete.

---

## Architecture

```
Logic Layer (background thread)          Blazor UI (main thread)
?????????????????????????????           ??????????????????????????
PubSubChannel.SendMessage()  ??write???  Channel<PubSubMessage>
                                              ?
                                         consumer Task.Run(...)
                                              ?
                                         ??? Add to _messages/_warnings/_errors lists
                                         ??? Snackbar.Add(msg, severity)
```

### Components Involved

| Component | Location | Role |
|-----------|----------|------|
| `PubSubChannel` | `Communication/PubSubChannel.cs` | Static unbounded `Channel<PubSubMessage>` for fire-and-forget messaging |
| `PubSubMessage` | `Objects/PubSubMessage.cs` | DTO with `MessageType` (Info/Warning/Error) and `Message` string |
| Consumer loop | Each page's `OnInitialized()` | Reads from channel, adds to lists, shows Snackbar |
| `ISnackbar` | Injected via `@inject ISnackbar Snackbar` | MudBlazor snackbar service for toast notifications |

---

## Standard Consumer Pattern

Every page that uses `PubSubChannel` must set up its consumer in `OnInitialized()`. The consumer runs for the lifetime of the page.

### Basic Pattern (Most Pages)

Used by: `MapShrink`, `RenameMap`, `Utilities`, `ConvertToForest`, `CopyAssets`, `CopyTerrains`, `CopyForestBrushes`, `CreateLevel`

```csharp
protected override void OnInitialized()
{
    // IMPORTANT: Always reset to default working directory on page init
    AppPaths.EnsureWorkingDirectory();

    var consumer = Task.Run(async () =>
    {
        while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
        {
            var msg = await PubSubChannel.ch.Reader.ReadAsync();
            if (!_messages.Contains(msg.Message) && !_errors.Contains(msg.Message))
            {
                switch (msg.MessageType)
                {
                    case PubSubMessageType.Info:
                        _messages.Add(msg.Message);
                        Snackbar.Add(msg.Message, Severity.Info);
                        break;
                    case PubSubMessageType.Warning:
                        _warnings.Add(msg.Message);
                        Snackbar.Add(msg.Message, Severity.Warning);
                        break;
                    case PubSubMessageType.Error:
                        _errors.Add(msg.Message);
                        Snackbar.Add(msg.Message, Severity.Error);
                        break;
                }
            }
        }
    });
}
```

### Key Details

1. **Duplicate prevention**: The `if (!_messages.Contains(...) && !_errors.Contains(...))` guard prevents showing the same message twice.
2. **Three lists**: `_messages` (Info), `_warnings` (Warning), `_errors` (Error) — these feed the MudDrawer log panels in the footer.
3. **No `await`**: The `consumer` Task is fire-and-forget. It runs until the app exits or the channel is completed.
4. **Thread safety**: `Snackbar.Add()` is called from the consumer task. MudBlazor's snackbar service is thread-safe for `Add()` calls. However, if you need to update Blazor UI state (e.g., `StateHasChanged()`), wrap it in `InvokeAsync()`.

### Pattern with `InvokeAsync` (CreateLevel page)

The `CreateLevel` page additionally calls `StateHasChanged` after each message:

```csharp
// Inside the consumer switch block, after adding to lists and snackbar:
await InvokeAsync(StateHasChanged);
```

Use this variant when messages should trigger immediate UI re-renders (e.g., updating progress indicators in the page content).

---

## Enhanced Pattern: Snackbar Suppression for Long Operations

Used by: `GenerateTerrain`

When a page performs long-running operations that produce many messages (e.g., terrain generation), the standard pattern causes problems: after the operation finishes, dozens of queued snackbar notifications continue appearing. The solution uses a `_suppressSnackbars` flag.

### Setup

```csharp
// Field declaration
private volatile bool _suppressSnackbars;
```

### Consumer with Suppression

```csharp
protected override void OnInitialized()
{
    // ... other initialization ...

    var consumer = Task.Run(async () =>
    {
        while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
        {
            var msg = await PubSubChannel.ch.Reader.ReadAsync();
            if (!_messages.Contains(msg.Message) && !_errors.Contains(msg.Message))
            {
                // ALWAYS add to lists (for drawer/logs) regardless of suppression
                switch (msg.MessageType)
                {
                    case PubSubMessageType.Info:
                        _messages.Add(msg.Message);
                        break;
                    case PubSubMessageType.Warning:
                        _warnings.Add(msg.Message);
                        break;
                    case PubSubMessageType.Error:
                        _errors.Add(msg.Message);
                        break;
                }

                // Only show snackbar if not suppressed
                if (!_suppressSnackbars)
                {
                    await InvokeAsync(() =>
                    {
                        switch (msg.MessageType)
                        {
                            case PubSubMessageType.Info:
                                Snackbar.Add(msg.Message, Severity.Info);
                                break;
                            case PubSubMessageType.Warning:
                                Snackbar.Add(msg.Message, Severity.Warning);
                                break;
                            case PubSubMessageType.Error:
                                Snackbar.Add(msg.Message, Severity.Error);
                                break;
                        }
                    });
                }
            }
        }
    });
}
```

### Usage in Operation Completion

When a long-running operation finishes, suppress ? clear ? show final message:

```csharp
finally
{
    _isGenerating = false;

    // 1. Suppress new snackbars from PubSub consumer
    //    (prevents race condition where consumer creates snackbar after Clear)
    _suppressSnackbars = true;

    // 2. Clear all existing/queued snackbars and show final message
    await InvokeAsync(() =>
    {
        Snackbar.Clear();

        if (generationSucceeded && finalSuccessMessage != null)
        {
            Snackbar.Add(finalSuccessMessage, Severity.Success);
        }
    });

    // 3. Re-enable snackbars for future interactions
    _suppressSnackbars = false;

    _terrainGenerationSnackbar = null;
    await InvokeAsync(StateHasChanged);
}
```

### Why This Works

1. **`_suppressSnackbars = true`** — The consumer loop continues to run and drains messages from the channel, adding them to the log lists, but does NOT create Snackbar notifications.
2. **`Snackbar.Clear()`** — Removes all currently visible and pending snackbar notifications from the UI.
3. **`Snackbar.Add(finalMessage, Severity.Success)`** — The final success message is the only snackbar the user sees.
4. **`_suppressSnackbars = false`** — After the clear + final message, resume normal snackbar behavior for future user actions on the page.
5. **`volatile` keyword** — Ensures the flag is immediately visible across threads (consumer runs on a thread pool thread).

---

## Persistent Snackbar Pattern

For operations that take a noticeable amount of time, show a persistent snackbar that stays visible until explicitly removed:

```csharp
// Show persistent snackbar
_staticSnackbar = Snackbar.Add("Unzipping level...", Severity.Normal,
    config => { config.VisibleStateDuration = int.MaxValue; });

// ... do work ...

// Remove when done
Snackbar.Remove(_staticSnackbar);
Snackbar.Add("Unzipping finished", Severity.Success);
```

### Guidelines

- Use `VisibleStateDuration = int.MaxValue` to keep the snackbar visible indefinitely.
- Always remove the persistent snackbar in a `finally` block or after the operation completes.
- Store the reference in a field (e.g., `_staticSnackbar`, `_terrainGenerationSnackbar`, `_geoTiffLoadingSnackbar`).

---

## When to Use Each Pattern

| Scenario | Pattern | Example Pages |
|----------|---------|---------------|
| Simple page with occasional messages | Basic consumer | MapShrink, RenameMap, Utilities |
| Page with copy operations (moderate messages) | Basic consumer + persistent snackbar | CopyAssets, CopyTerrains, CopyForestBrushes |
| Page with long-running heavy operations (many messages) | Consumer with suppression + clear on finish | GenerateTerrain |
| Page needing UI updates on each message | Consumer with `InvokeAsync(StateHasChanged)` | CreateLevel |

---

## Applying Snackbar Suppression to Other Pages

If a page starts producing too many snackbar messages during a long operation, apply the enhanced pattern:

1. Add `private volatile bool _suppressSnackbars;` field.
2. Wrap the `Snackbar.Add()` calls in the consumer with `if (!_suppressSnackbars)`.
3. **Keep** the list additions (`_messages.Add(...)` etc.) outside the suppression check — logs must always be captured.
4. In the operation's `finally` block, use the suppress ? clear ? final message ? unsuppress sequence.

---

## Common Pitfalls

### 1. Don't Forget to Unsuppress
Always set `_suppressSnackbars = false` after clearing. Otherwise, no future messages will show snackbars on the page.

### 2. Messages Still Accumulate in Lists
Suppression only affects Snackbar UI notifications. The `_messages`, `_warnings`, and `_errors` lists continue to grow. This is intentional — the MudDrawer log panels should always have the full history.

### 3. Channel is Shared (Single Consumer)
The `PubSubChannel.ch` is a static singleton `Channel<PubSubMessage>`. Each page starts its own consumer, but only one page is active at a time (Blazor SPA navigation). When the user navigates away, the old consumer may still be running but the Snackbar service context changes.

### 4. Thread Safety of Snackbar.Add vs StateHasChanged
- `Snackbar.Add()` — Can be called from any thread (MudBlazor handles marshaling internally).
- `StateHasChanged()` — **Must** be called via `InvokeAsync()` from background threads.
- The basic pattern (most pages) calls `Snackbar.Add()` directly without `InvokeAsync`. This works because MudBlazor's snackbar service is designed for cross-thread use.
- When you also need `StateHasChanged()`, wrap everything in `InvokeAsync()`.

### 5. Snackbar Configuration
The `Welcome.razor` page sets global snackbar defaults on initialization:
```csharp
Snackbar.Configuration.PositionClass = Defaults.Classes.Position.TopRight;
Snackbar.Configuration.MaxDisplayedSnackbars = 50;
Snackbar.Configuration.VisibleStateDuration = 5000;
```
Individual pages can override (e.g., `GenerateTerrain` sets `PreventDuplicates = true` and `MaxDisplayedSnackbars = 10`).

---

## Related Files

| File | Purpose |
|------|---------|
| `Communication/PubSubChannel.cs` | Static channel + `SendMessage()` method |
| `Objects/PubSubMessage.cs` | Message DTO (MessageType + Message) |
| `Objects/StaticVariables.cs` | `ApplicationExitRequest` flag for graceful shutdown |
| `BlazorUI/Pages/*.razor.cs` | All pages implementing the consumer pattern |
