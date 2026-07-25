using System.Diagnostics;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicBiome;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.Biome;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class GenerateBiome
{
    private const string OperationLoadLevel = "load-level";
    private const string OperationSaveSettings = "save-settings";
    private const string OperationGenerate = "generate";
    private const string OperationDelete = "delete";
    private const string OperationEstimates = "estimates";
    private const string OperationCleanup = "cleanup";

    private readonly BiomeService _service = new();

    private BiomeLevelContext? _context;
    private string? _staleReason;

    private Anchor _anchor;
    private bool _isLoading;
    private bool _isApplying;
    private bool _openDrawer;
    private bool _showErrorLog;
    private bool _showWarningLog;
    private string _busyOperation = string.Empty;
    private string _busyMessage = string.Empty;
    private string _drawerWidth = "100%";
    private string _drawerHeight = "50%";
    private List<string> _errors = new();
    private List<string> _warnings = new();
    private List<string> _messages = new();

    /// <summary>Zone pixel counts per layer id — drives the estimate labels; invalidated on zone edits.</summary>
    private readonly Dictionary<string, long[]> _zonePixelCounts = new();

    /// <summary>Expanded material cards (key = material internalName).</summary>
    private readonly HashSet<string> _expandedMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Expanded OSM layer cards (key = LayerId).</summary>
    private readonly HashSet<string> _expandedOsmLayers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Selection of the "Add OSM layer" dropdown.</summary>
    private string? _osmLayerToAdd;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private bool HasLevel => _context != null;
    private bool IsBusy => _isLoading || _isApplying;
    private bool HasBusyMessage => IsBusy && !string.IsNullOrWhiteSpace(_busyMessage);
    private int TotalGeneratedItems => _context?.Manifest.Layers.Sum(l => l.ItemCount) ?? 0;
    private int ConfiguredMaterialLayerCount => _context?.Settings.MaterialLayers.Count ?? 0;
    private int ConfiguredOsmLayerCount => _context?.Settings.OsmLayers.Count ?? 0;
    private int NegativeListCount => _context == null
        ? 0
        : _context.Settings.NegativeList.MaterialInternalNames.Count +
          _context.Settings.NegativeList.OsmLayerKeys.Count;
    private bool CanGenerateAny => !IsBusy && _context != null &&
                                   (_context.Settings.MaterialLayers.Any(LayerHasSelections) ||
                                    _context.Settings.OsmLayers.Any(LayerHasSelections));

    private IEnumerable<BiomeOsmLayerInfo> AvailableOsmLayersToAdd => _context == null
        ? Enumerable.Empty<BiomeOsmLayerInfo>()
        : _context.OsmLayers.Where(info => !_context.Settings.OsmLayers.Any(l =>
            l.SourceKey.Equals(info.Key, StringComparison.OrdinalIgnoreCase)));

    protected override void OnInitialized()
    {
        Task.Run(ReadPubSubMessages);
    }

    private async Task OnFolderSelected(string folder)
    {
        _isLoading = true;
        _busyOperation = OperationLoadLevel;
        _busyMessage = "Loading level, terrain layers and forest brushes...";
        ClearMessages();
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            var result = await Task.Run(() => _service.LoadLevel(folder));
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error, result.ErrorMessage);
                    Snackbar.Add(result.ErrorMessage, Severity.Error,
                        options => options.RequireInteraction = true);
                }
                return;
            }

            _context = result.Context;
            _zonePixelCounts.Clear();
            _staleReason = BiomeService.ComputeStaleReason(_context!);
        }
        finally
        {
            _isLoading = false;
            ClearBusyOperation();
            await InvokeAsync(StateHasChanged);
        }
    }

    private BiomeLayerSettings? FindMaterialLayer(string internalName)
    {
        return _context?.Settings.MaterialLayers.FirstOrDefault(l =>
            l.SourceKey.Equals(internalName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LayerHasSelections(BiomeLayerSettings layer)
    {
        return layer.Zones.Any(z => z.Items.Any(i => i.MixWeight > 0));
    }

    private long[]? GetCachedZoneCounts(BiomeLayerSettings layer)
    {
        return _zonePixelCounts.TryGetValue(layer.LayerId, out var counts) ? counts : null;
    }

    private bool IsMaterialExpanded(string internalName) => _expandedMaterials.Contains(internalName);

    private void ToggleMaterialExpanded(string internalName)
    {
        if (FindMaterialLayer(internalName) == null)
            return; // nothing to expand

        if (!_expandedMaterials.Remove(internalName))
            _expandedMaterials.Add(internalName);
    }

    private static string GetMaterialStatusIcon(BiomeLayerSettings? layer) => layer == null
        ? Icons.Material.Filled.RadioButtonUnchecked
        : LayerHasSelections(layer)
            ? Icons.Material.Filled.Forest
            : Icons.Material.Filled.Tune;

    private static MudBlazor.Color GetMaterialStatusColor(BiomeLayerSettings? layer) => layer == null
        ? MudBlazor.Color.Default
        : LayerHasSelections(layer)
            ? MudBlazor.Color.Success
            : MudBlazor.Color.Warning;

    private static string GetMaterialStatusTooltip(BiomeLayerSettings? layer) => layer == null
        ? "No biome configuration"
        : LayerHasSelections(layer)
            ? "Zones with items configured"
            : "Zones configured, but no items selected yet";

    private void AddMaterialLayer(string internalName)
    {
        if (_context == null)
            return;

        _context.Settings.MaterialLayers.Add(new BiomeLayerSettings
        {
            Kind = BiomeLayerKind.TerrainMaterial,
            SourceKey = internalName,
            Zones = new List<BiomeZoneSettings> { new() },
        });
        _expandedMaterials.Add(internalName);
    }

    private async Task RemoveMaterialLayer(BiomeLayerSettings layer)
    {
        if (_context == null)
            return;

        var hasGenerated = _context.Manifest.Layers.Any(l => l.LayerId == layer.LayerId);
        if (hasGenerated)
        {
            var confirmed = await DialogService.ShowMessageBox(
                "Remove Layer Configuration",
                "This layer still has generated forest items on the map. Remove the configuration AND delete its generated items?",
                yesText: "Remove & Delete Items", cancelText: "Cancel");
            if (confirmed != true)
                return;

            await RunBusyOperation(OperationDelete, "Deleting generated items...", async () =>
            {
                var result = await Task.Run(() => _service.DeleteGenerated(_context, new[] { layer.LayerId }));
                Snackbar.Add($"Removed {result.ItemsRemoved:N0} generated item(s).", Severity.Success);
            });
        }

        _context.Settings.MaterialLayers.Remove(layer);
        _zonePixelCounts.Remove(layer.LayerId);
        _expandedMaterials.Remove(layer.SourceKey);
    }

    private void OnLayerStructureChanged(BiomeLayerSettings layer)
    {
        // Zone structure changed — cached band areas are no longer trustworthy.
        _zonePixelCounts.Remove(layer.LayerId);
    }

    private string GetOsmDisplayName(string key)
    {
        return _context?.OsmLayers.FirstOrDefault(o =>
                   o.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.DisplayName
               ?? key;
    }

    /// <summary>False when a configured layer's mask PNG has disappeared since the last generation run.</summary>
    private bool OsmMaskExists(string key)
    {
        return _context != null && _context.OsmLayers.Any(o =>
            o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsOsmLayerExpanded(BiomeLayerSettings layer) => _expandedOsmLayers.Contains(layer.LayerId);

    private void ToggleOsmLayerExpanded(BiomeLayerSettings layer)
    {
        if (!_expandedOsmLayers.Remove(layer.LayerId))
            _expandedOsmLayers.Add(layer.LayerId);
    }

    private void AddOsmLayer()
    {
        if (_context == null || string.IsNullOrEmpty(_osmLayerToAdd))
            return;
        if (_context.Settings.OsmLayers.Any(l =>
                l.SourceKey.Equals(_osmLayerToAdd, StringComparison.OrdinalIgnoreCase)))
            return;

        var layer = new BiomeLayerSettings
        {
            Kind = BiomeLayerKind.Osm,
            SourceKey = _osmLayerToAdd,
            Zones = new List<BiomeZoneSettings> { new() },
        };
        _context.Settings.OsmLayers.Add(layer);
        _expandedOsmLayers.Add(layer.LayerId);
        _osmLayerToAdd = null;
    }

    private async Task RemoveOsmLayer(BiomeLayerSettings layer)
    {
        if (_context == null)
            return;

        var hasGenerated = _context.Manifest.Layers.Any(l => l.LayerId == layer.LayerId);
        if (hasGenerated)
        {
            var confirmed = await DialogService.ShowMessageBox(
                "Remove Layer Configuration",
                "This layer still has generated forest items on the map. Remove the configuration AND delete its generated items?",
                yesText: "Remove & Delete Items", cancelText: "Cancel");
            if (confirmed != true)
                return;

            await RunBusyOperation(OperationDelete, "Deleting generated items...", async () =>
            {
                var result = await Task.Run(() => _service.DeleteGenerated(_context, new[] { layer.LayerId }));
                Snackbar.Add($"Removed {result.ItemsRemoved:N0} generated item(s).", Severity.Success);
            });
        }

        _context.Settings.OsmLayers.Remove(layer);
        _zonePixelCounts.Remove(layer.LayerId);
        _expandedOsmLayers.Remove(layer.LayerId);
    }

    private async Task ComputeEstimates(BiomeLayerSettings layer)
    {
        if (_context == null)
            return;

        await RunBusyOperation(OperationEstimates, $"Computing zone areas for '{layer.SourceKey}'...", async () =>
        {
            var counts = await Task.Run(() => BiomeService.ComputeZonePixelCounts(_context, layer));
            _zonePixelCounts[layer.LayerId] = counts;
        });
    }

    private async Task GenerateLayer(BiomeLayerSettings layer)
    {
        await GenerateInternal(new[] { layer }, $"Generating biome for '{layer.SourceKey}'...");
    }

    private async Task GenerateAll()
    {
        if (_context == null)
            return;
        var layers = _context.Settings.MaterialLayers.Where(LayerHasSelections)
            .Concat(_context.Settings.OsmLayers.Where(LayerHasSelections))
            .ToList();
        await GenerateInternal(layers, "Generating all biome layers...");
    }

    private async Task GenerateInternal(IReadOnlyList<BiomeLayerSettings> layers, string busyMessage)
    {
        if (_context == null || layers.Count == 0)
            return;

        await RunBusyOperation(OperationGenerate, busyMessage, async () =>
        {
            var result = await Task.Run(() => _service.GenerateLayers(_context, layers));
            _staleReason = BiomeService.ComputeStaleReason(_context);

            if (result.LayersGenerated == 0)
            {
                // The reasons belong IN the snackbar — never point the user at another log.
                Snackbar.Add(
                    BuildSnackbarMarkup("No layers were generated.", result.SkipReasons),
                    Severity.Error,
                    options => options.RequireInteraction = true);
            }
            else
            {
                var cleanupNote = result.ItemsRemovedByCleanup > 0
                    ? $" Negative-list cleanup removed {result.ItemsRemovedByCleanup:N0} of them again."
                    : string.Empty;
                var headline =
                    $"Generated {result.ItemsPlaced:N0} forest item(s) across {result.LayersGenerated} layer(s).{cleanupNote}";
                if (result.SkipReasons.Count > 0)
                {
                    Snackbar.Add(
                        BuildSnackbarMarkup($"{headline} Skipped {result.LayersSkipped} layer(s):", result.SkipReasons),
                        Severity.Warning,
                        options => options.VisibleStateDuration = 15000);
                }
                else
                {
                    Snackbar.Add(headline, Severity.Success);
                }
            }
        });
    }

    private async Task DeleteLayerItems(BiomeLayerSettings layer)
    {
        if (_context == null)
            return;

        var manifestLayer = _context.Manifest.Layers.FirstOrDefault(l => l.LayerId == layer.LayerId);
        if (manifestLayer == null)
            return;

        var confirmed = await DialogService.ShowMessageBox(
            "Delete Generated Items",
            $"Delete {manifestLayer.ItemCount:N0} generated forest item(s) of layer '{layer.SourceKey}'? " +
            "Hand-placed forest items are never touched.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed != true)
            return;

        await RunBusyOperation(OperationDelete, $"Deleting generated items of '{layer.SourceKey}'...", async () =>
        {
            var result = await Task.Run(() => _service.DeleteGenerated(_context, new[] { layer.LayerId }));
            Snackbar.Add($"Removed {result.ItemsRemoved:N0} generated item(s).", Severity.Success);
        });
    }

    private async Task DeleteAllGenerated()
    {
        if (_context == null || (TotalGeneratedItems == 0 && _context.OrphanedForestFileCount == 0))
            return;

        var orphanNote = _context.OrphanedForestFileCount > 0
            ? $" Also removes {_context.OrphanedForestFileCount} orphaned biome forest file(s) from interrupted generations."
            : string.Empty;
        var confirmed = await DialogService.ShowMessageBox(
            "Delete All Generated Items",
            $"Delete ALL {TotalGeneratedItems:N0} forest item(s) this tool has generated in '{_context.LevelName}'?{orphanNote} " +
            "Hand-placed forest items are never touched.",
            yesText: "Delete All", cancelText: "Cancel");
        if (confirmed != true)
            return;

        await RunBusyOperation(OperationDelete, "Deleting all generated forest items...", async () =>
        {
            var result = await Task.Run(() => _service.DeleteGenerated(_context));
            _context.OrphanedForestFileCount = 0;
            var orphanSummary = result.OrphanFilesDeleted > 0
                ? $" ({result.OrphanFilesDeleted} orphaned file(s) swept)"
                : string.Empty;
            Snackbar.Add(
                $"Removed {result.ItemsRemoved:N0} generated item(s) from {result.LayersDeleted} layer(s){orphanSummary}.",
                Severity.Success);
        });
    }

    private void SetNegativeMaterials(IEnumerable<string> values)
    {
        if (_context != null)
            _context.Settings.NegativeList.MaterialInternalNames = values.ToList();
    }

    private void SetNegativeOsmLayers(IEnumerable<string> values)
    {
        if (_context != null)
            _context.Settings.NegativeList.OsmLayerKeys = values.ToList();
    }

    private void RemoveNegativeMaterial(string name)
    {
        _context?.Settings.NegativeList.MaterialInternalNames.Remove(name);
    }

    private void RemoveNegativeOsmLayer(string key)
    {
        _context?.Settings.NegativeList.OsmLayerKeys.Remove(key);
    }

    private string GetOsmSelectionText(List<string> keys)
    {
        return string.Join(", ", keys.Select(GetOsmDisplayName));
    }

    /// <summary>
    /// Explicit "Cleanup Now": tracked items first, then — only when the opt-in is set —
    /// a counted, confirmed foreign-item pass reusing the session's negative mask.
    /// </summary>
    private async Task CleanupNow()
    {
        if (_context == null)
            return;
        if (NegativeListCount == 0)
        {
            Snackbar.Add("The negative list is empty — select layers to clean up first.", Severity.Info);
            return;
        }

        BiomeCleanupSession? session = null;
        await RunBusyOperation(OperationCleanup, "Running negative-list cleanup...", async () =>
        {
            var (cleanupSession, failReason) = await Task.Run(() => _service.RunNegativeListCleanup(_context));
            session = cleanupSession;
            if (session == null)
            {
                Snackbar.Add(
                    $"Cleanup did not run: {failReason ?? "unknown reason."}",
                    Severity.Warning,
                    options => options.VisibleStateDuration = 12000);
                return;
            }
            Snackbar.Add(
                $"Cleanup removed {session.TrackedResult.ItemsRemoved:N0} generated item(s) standing on the negative list.",
                Severity.Success);
        });

        if (session == null || !_context.Settings.NegativeList.IncludeForeignItems)
            return;

        var foreignCount = 0;
        await RunBusyOperation(OperationCleanup, "Counting foreign forest items on the negative layers...", async () =>
        {
            foreignCount = await Task.Run(() => _service.CountForeignItemsOnMask(_context, session!));
        });

        if (foreignCount == 0)
        {
            Snackbar.Add("No foreign forest items stand on the negative layers.", Severity.Info);
            return;
        }

        var confirmed = await DialogService.ShowMessageBox(
            "Remove Foreign Forest Items",
            $"{foreignCount:N0} forest item(s) NOT placed by this tool stand on the negative-list layers " +
            "(hand-placed or created by the in-game biome tool). Remove them from the forest files? " +
            "This cannot be undone.",
            yesText: $"Remove {foreignCount:N0} item(s)", cancelText: "Keep them");
        if (confirmed != true)
            return;

        await RunBusyOperation(OperationCleanup, "Removing foreign forest items...", async () =>
        {
            var removed = await Task.Run(() => _service.RemoveForeignItemsOnMask(_context, session!));
            Snackbar.Add($"Removed {removed:N0} foreign forest item(s).", Severity.Success);
        });
    }

    private async Task SaveSettings()
    {
        if (_context == null)
            return;

        await RunBusyOperation(OperationSaveSettings, "Saving biome settings...", async () =>
        {
            await Task.Run(() => _context.Settings.Save(_context.LevelPath));
            Snackbar.Add("Biome settings saved.", Severity.Success);
        });
    }

    private void ResetPage()
    {
        _context = null;
        _staleReason = null;
        _zonePixelCounts.Clear();
        _expandedMaterials.Clear();
        _expandedOsmLayers.Clear();
        _osmLayerToAdd = null;
        ClearBusyOperation();
        ClearMessages();
    }

    private void OpenDrawer(Anchor anchor, PubSubMessageType msgType)
    {
        _anchor = anchor;
        _openDrawer = true;
        _showErrorLog = msgType == PubSubMessageType.Error;
        _showWarningLog = msgType == PubSubMessageType.Warning;
    }

    private void OpenLevelFolder()
    {
        if (_context != null)
            Process.Start("explorer.exe", _context.LevelPath);
    }

    private async Task OpenHelpDialog()
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        await DialogService.ShowAsync<GenerateBiomeHelpDialog>("Generate Biome Guide", options);
    }

    private async Task RunBusyOperation(string operation, string message, Func<Task> action)
    {
        if (_isApplying)
            return;

        _isApplying = true;
        _busyOperation = operation;
        _busyMessage = message;
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var errorMessage = ex.InnerException != null
                ? $"{ex.Message} {ex.InnerException.Message}"
                : ex.Message;
            PubSubChannel.SendMessage(PubSubMessageType.Error, errorMessage);
            Snackbar.Add(errorMessage, Severity.Error, options => options.RequireInteraction = true);
        }
        finally
        {
            _isApplying = false;
            ClearBusyOperation();
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// Headline plus bulleted detail lines as snackbar markup (details are HTML-encoded).
    /// Used to put skip/failure reasons directly into the snackbar instead of pointing
    /// the user at the message log.
    /// </summary>
    private static MarkupString BuildSnackbarMarkup(string headline, IReadOnlyCollection<string> details)
    {
        var encodedHeadline = System.Net.WebUtility.HtmlEncode(headline);
        if (details.Count == 0)
            return new MarkupString(encodedHeadline);

        var lines = details.Select(d => "• " + System.Net.WebUtility.HtmlEncode(d));
        return new MarkupString(encodedHeadline + "<br/>" + string.Join("<br/>", lines));
    }

    private bool IsOperation(string operation)
    {
        return IsBusy && _busyOperation.Equals(operation, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearBusyOperation()
    {
        _busyOperation = string.Empty;
        _busyMessage = string.Empty;
    }

    private void ClearMessages()
    {
        _errors = new List<string>();
        _warnings = new List<string>();
        _messages = new List<string>();
        _openDrawer = false;
    }

    private async Task ReadPubSubMessages()
    {
        while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
        {
            var msg = await PubSubChannel.ch.Reader.ReadAsync();
            if (_messages.Contains(msg.Message) || _errors.Contains(msg.Message) || _warnings.Contains(msg.Message))
                continue;

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

            await InvokeAsync(StateHasChanged);
        }
    }
}
