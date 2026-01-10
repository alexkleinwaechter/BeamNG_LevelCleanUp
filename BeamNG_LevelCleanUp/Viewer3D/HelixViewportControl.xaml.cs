using System.Collections.ObjectModel;
using System.Windows;
using BeamNG_LevelCleanUp.Objects;
using HelixToolkit.Wpf.SharpDX;
using UserControl = System.Windows.Controls.UserControl;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     WPF UserControl hosting the Helix Toolkit 3D viewport.
///     Supports DAE model loading and material preview on plane.
///     Uses PathResolver for direct file access - no temp folder needed.
///     Supports .link file resolution from game asset ZIPs via LinkFileResolver.
/// </summary>
/// <remarks>
///     This is a partial class split across multiple files:
///     - HelixViewportControl.xaml.cs - Core control, initialization, disposal, visibility
///     - HelixViewportControl.Selection.cs - Mesh selection and highlighting
///     - HelixViewportControl.Loading.cs - Model and texture loading
///     - HelixViewportControl.Lighting.cs - Lighting controls and presets
/// </remarks>
public partial class HelixViewportControl : UserControl, IDisposable
{
    // ============== SHARED FIELDS (used across partial classes) ==============
    
    private readonly DefaultEffectsManager _effectsManager;
    private readonly List<Element3D> _loadedModels = new();

    /// <summary>
    ///     Maps MeshGeometryModel3D instances to their material information.
    /// </summary>
    private readonly Dictionary<MeshGeometryModel3D, MeshSelectionInfo> _meshMaterialMap = new();

    private bool _disposed;
    
    // Selection-related fields (used in HelixViewportControl.Selection.cs)
    private MeshGeometryModel3D? _selectedMesh;
    private bool _isUpdatingListSelection;
    
    // Loading-related fields (used in HelixViewportControl.Loading.cs)
    private TextureLookup? _currentTextureLookup;
    
    // Lighting-related fields (used in HelixViewportControl.Lighting.cs)
    private Viewer3DRequest? _lastRequest;

    public HelixViewportControl()
    {
        InitializeComponent();

        _effectsManager = new DefaultEffectsManager();
        Viewport.EffectsManager = _effectsManager;

        // Bind mesh items to the ListView
        MeshListView.ItemsSource = MeshItems;
        MeshListView.SelectionChanged += MeshListView_SelectionChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    ///     Observable collection of mesh items for the visibility control panel.
    /// </summary>
    public ObservableCollection<MeshVisibilityItem> MeshItems { get; } = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ClearModels();
        _effectsManager?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Initialize lighting to medium settings
        TextureMapConfig.SetMediumLighting();
        UpdateSlidersFromConfig();
        UpdateViewportLighting();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void ClearModels()
    {
        // Deselect any selected mesh first
        _selectedMesh = null;

        foreach (var model in _loadedModels)
        {
            Viewport.Items.Remove(model);
            if (model is IDisposable disposable)
                disposable.Dispose();
        }

        _loadedModels.Clear();
        _meshMaterialMap.Clear();
        _currentTextureLookup = null;
        MeshItems.Clear();
    }

    /// <summary>
    ///     Sets visibility for all meshes that have no textures.
    /// </summary>
    /// <param name="visible">True to show meshes without textures, false to hide them.</param>
    public void SetMeshesWithoutTexturesVisible(bool visible)
    {
        foreach (var item in MeshItems)
            if (!item.HasTextures)
                item.IsVisible = visible;

        UpdateStatus();
    }

    /// <summary>
    ///     Sets visibility for all meshes.
    /// </summary>
    /// <param name="visible">True to show all meshes, false to hide them.</param>
    public void SetAllMeshesVisible(bool visible)
    {
        foreach (var item in MeshItems) item.IsVisible = visible;
        UpdateStatus();
    }

    /// <summary>
    ///     Updates the status bar with current mesh visibility info.
    /// </summary>
    private void UpdateStatus()
    {
        var visibleCount = MeshItems.Count(m => m.IsVisible);
        var withTexturesCount = MeshItems.Count(m => m.HasTextures);
        SetStatus($"Meshes: {visibleCount}/{MeshItems.Count} visible | {withTexturesCount} with textures");
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
    }

    /// <summary>
    ///     Resets camera to fit all loaded content.
    /// </summary>
    public void ResetCamera()
    {
        Viewport.ZoomExtents(500);
    }

    /// <summary>
    ///     Button handler to show all meshes.
    /// </summary>
    private void BtnShowAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllMeshesVisible(true);
    }

    /// <summary>
    ///     Button handler to hide meshes without textures.
    /// </summary>
    private void BtnHideNoTextures_Click(object sender, RoutedEventArgs e)
    {
        SetMeshesWithoutTexturesVisible(false);
    }

    /// <summary>
    ///     Button handler to toggle the help popup.
    /// </summary>
    private void BtnHelp_Click(object sender, RoutedEventArgs e)
    {
        HelpPopup.IsOpen = !HelpPopup.IsOpen;
    }

    /// <summary>
    ///     Toggle button handler to switch LOD filtering mode and reload the model.
    /// </summary>
    private async void BtnToggleLod_Click(object sender, RoutedEventArgs e)
    {
        // Get the toggle state from the sender
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
        {
            // Update the LOD filtering setting
            UseHighestLodOnly = toggleButton.IsChecked == true;

            // Reload the model if we have a previous request
            if (_lastRequest != null)
            {
                SetStatus(UseHighestLodOnly 
                    ? "Reloading with highest LOD only..." 
                    : "Reloading with all LODs...");
                await LoadAsync(_lastRequest);
            }
        }
    }

    /// <summary>
    ///     Button handler to apply texture map settings and reload the model.
    /// </summary>
    private async void BtnReloadTextureSettings_Click(object sender, RoutedEventArgs e)
    {
        // Find checkboxes using FindName (consistent with other controls in this file)
        var chkBaseColor = FindName("ChkBaseColorMap") as System.Windows.Controls.CheckBox;
        var chkOpacity = FindName("ChkOpacityMap") as System.Windows.Controls.CheckBox;
        var chkNormal = FindName("ChkNormalMap") as System.Windows.Controls.CheckBox;
        var chkRoughness = FindName("ChkRoughnessMap") as System.Windows.Controls.CheckBox;
        var chkMetallic = FindName("ChkMetallicMap") as System.Windows.Controls.CheckBox;
        var chkAO = FindName("ChkAmbientOcclusionMap") as System.Windows.Controls.CheckBox;
        var chkEmissive = FindName("ChkEmissiveMap") as System.Windows.Controls.CheckBox;
        var chkSpecular = FindName("ChkSpecularMap") as System.Windows.Controls.CheckBox;

        // Apply checkbox values to TextureMapConfig
        TextureMapConfig.EnableBaseColorMap = chkBaseColor?.IsChecked == true;
        TextureMapConfig.EnableOpacityMap = chkOpacity?.IsChecked == true;
        TextureMapConfig.EnableNormalMap = chkNormal?.IsChecked == true;
        TextureMapConfig.EnableRoughnessMap = chkRoughness?.IsChecked == true;
        TextureMapConfig.EnableMetallicMap = chkMetallic?.IsChecked == true;
        TextureMapConfig.EnableAmbientOcclusionMap = chkAO?.IsChecked == true;
        TextureMapConfig.EnableEmissiveMap = chkEmissive?.IsChecked == true;
        TextureMapConfig.EnableSpecularMap = chkSpecular?.IsChecked == true;

        // Reload the model if we have a previous request
        if (_lastRequest != null)
        {
            SetStatus("Reloading with new texture settings...");
            await LoadAsync(_lastRequest);
        }
        else
        {
            SetStatus("Texture settings updated (no model to reload)");
        }
    }
}