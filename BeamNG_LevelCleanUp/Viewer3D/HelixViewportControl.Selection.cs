using System.Windows.Controls;
using System.Windows.Input;
using BeamNG_LevelCleanUp.Utils;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using Material = HelixToolkit.Wpf.SharpDX.Material;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Partial class for mesh selection handling.
/// </summary>
public partial class HelixViewportControl
{
    /// <summary>
    ///     Event raised when a mesh is selected by clicking on it.
    /// </summary>
    public event EventHandler<MeshSelectionInfo?>? MeshSelected;

    /// <summary>
    ///     Handles mouse click on the viewport for mesh selection.
    /// </summary>
    private void Viewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Skip double-clicks (used for camera operations)
        if (e.ClickCount > 1)
            return;

        var position = e.GetPosition(Viewport);
        var hits = Viewport.FindHits(position);

        // Single click - select/toggle mesh
        if (hits != null && hits.Count > 0)
            // Find the first MeshGeometryModel3D hit
            foreach (var hit in hits)
                if (hit.ModelHit is MeshGeometryModel3D meshModel &&
                    _meshMaterialMap.TryGetValue(meshModel, out var selectionInfo))
                {
                    SelectMesh(meshModel, selectionInfo);
                    e.Handled = true;
                    return;
                }

        // Clicked on empty space - deselect
        DeselectMesh();
    }

    /// <summary>
    ///     Selects a mesh and highlights it.
    /// </summary>
    private void SelectMesh(MeshGeometryModel3D mesh, MeshSelectionInfo info)
    {
        // If clicking the same mesh, deselect it
        if (_selectedMesh == mesh)
        {
            DeselectMesh();
            return;
        }

        // Deselect previous mesh first
        if (_selectedMesh != null)
        {
            RestoreOriginalMaterial(_selectedMesh);
            UpdatePanelSelection(_selectedMesh, false);
        }

        // Find the MeshVisibilityItem for this mesh
        var visibilityItem = MeshItems.FirstOrDefault(m => m.MeshElement == mesh);

        // Store original material if not already stored
        if (visibilityItem != null && visibilityItem.OriginalMaterial == null)
            visibilityItem.OriginalMaterial = mesh.Material;

        // Store current mesh
        _selectedMesh = mesh;

        // Create highlight material (semi-transparent yellow overlay)
        var originalMaterial = visibilityItem?.OriginalMaterial ?? mesh.Material;
        var highlightMaterial = CreateHighlightMaterial(originalMaterial);
        mesh.Material = highlightMaterial;

        // Update panel selection state
        UpdatePanelSelection(mesh, true);

        // Update status with material info
        var statusText = $"Selected: {info.MeshName}";
        if (!string.IsNullOrEmpty(info.MaterialName))
            statusText += $" | Material: {info.MaterialName}";
        if (info.TextureFiles.Count > 0)
            statusText += $" | Textures: {info.TextureFiles.Count}";
        SetStatus(statusText);

        // Raise event for external handling (e.g., filtering texture gallery)
        MeshSelected?.Invoke(this, info);
    }

    /// <summary>
    ///     Deselects the currently selected mesh.
    /// </summary>
    private void DeselectMesh()
    {
        if (_selectedMesh != null)
        {
            RestoreOriginalMaterial(_selectedMesh);
            UpdatePanelSelection(_selectedMesh, false);
        }

        _selectedMesh = null;

        SetStatus($"Loaded: {_loadedModels.Count} meshes");

        // Raise event with null to indicate deselection
        MeshSelected?.Invoke(this, null);
    }

    /// <summary>
    ///     Updates the selection state in the mesh panel.
    /// </summary>
    private void UpdatePanelSelection(MeshGeometryModel3D mesh, bool isSelected)
    {
        var item = MeshItems.FirstOrDefault(m => m.MeshElement == mesh);
        if (item != null)
        {
            item.IsSelected = isSelected;

            // Scroll to selected item if selecting
            if (isSelected) MeshListView.ScrollIntoView(item);
        }
    }

    /// <summary>
    ///     Restores the original material of the specified mesh.
    /// </summary>
    private void RestoreOriginalMaterial(MeshGeometryModel3D mesh)
    {
        var visibilityItem = MeshItems.FirstOrDefault(m => m.MeshElement == mesh);
        if (visibilityItem?.OriginalMaterial != null) mesh.Material = visibilityItem.OriginalMaterial;
    }

    /// <summary>
    ///     Handles selection changes in the mesh list panel.
    ///     Clicking an item selects/deselects the mesh in the 3D viewer.
    /// </summary>
    private void MeshListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Prevent re-entrant calls when we programmatically change selection
        if (_isUpdatingListSelection)
            return;

        if (e.AddedItems.Count > 0 && e.AddedItems[0] is MeshVisibilityItem selectedItem)
        {
            // Select or toggle this mesh in the 3D viewer
            if (_meshMaterialMap.TryGetValue(selectedItem.MeshElement, out var selectionInfo))
                SelectMesh(selectedItem.MeshElement, selectionInfo);

            // Clear ListView selection so clicking the same item again will trigger SelectionChanged
            _isUpdatingListSelection = true;
            MeshListView.SelectedItem = null;
            _isUpdatingListSelection = false;
        }
    }

    /// <summary>
    ///     Creates a highlight material based on the original material.
    /// </summary>
    private static Material CreateHighlightMaterial(Material? original)
    {
        // Create a highlighted version - bright yellow/orange tint
        if (original is PBRMaterial pbrOriginal)
        {
            var highlight = new PBRMaterial
            {
                AlbedoColor = new Color4(1.0f, 0.8f, 0.2f, 1.0f), // Yellow/orange highlight
                MetallicFactor = pbrOriginal.MetallicFactor,
                RoughnessFactor = 0.3, // Make it shinier
                EmissiveColor = new Color4(0.3f, 0.2f, 0.0f, 1.0f), // Subtle glow
                AlbedoMap = pbrOriginal.AlbedoMap,
                NormalMap = pbrOriginal.NormalMap
            };
            return highlight;
        }

        if (original is PhongMaterial phongOriginal)
        {
            var highlight = new PhongMaterial
            {
                DiffuseColor = new Color4(1.0f, 0.8f, 0.2f, 1.0f), // Yellow/orange highlight
                SpecularColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f),
                SpecularShininess = 100, // Very shiny
                EmissiveColor = new Color4(0.3f, 0.2f, 0.0f, 1.0f), // Subtle glow
                DiffuseMap = phongOriginal.DiffuseMap,
                NormalMap = phongOriginal.NormalMap
            };
            return highlight;
        }

        // Fallback
        return new PhongMaterial
        {
            DiffuseColor = new Color4(1.0f, 0.8f, 0.2f, 1.0f),
            SpecularColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f),
            SpecularShininess = 100,
            EmissiveColor = new Color4(0.3f, 0.2f, 0.0f, 1.0f)
        };
    }

    /// <summary>
    ///     Gets the selection info for a material name.
    /// </summary>
    public MeshSelectionInfo? GetSelectionInfoForMaterial(string materialName)
    {
        return _meshMaterialMap.Values.FirstOrDefault(info =>
            info.MaterialName.Equals(materialName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Builds selection info for a mesh, finding associated materials and textures.
    /// </summary>
    private static MeshSelectionInfo BuildSelectionInfo(string meshName, string materialName,
        TextureLookup textureLookup)
    {
        var info = new MeshSelectionInfo
        {
            MeshName = meshName,
            MaterialName = materialName
        };

        // Try to find the MaterialJson for this material
        var materialJson = textureLookup.FindMaterialByKey(materialName)
                           ?? textureLookup.FindMaterialByFuzzyMatch(materialName);

        if (materialJson != null)
        {
            info.Material = materialJson;
            info.MaterialName = materialJson.Name ?? materialJson.InternalName ?? materialName;

            // Get associated texture files
            if (materialJson.MaterialFiles != null)
                info.TextureFiles = materialJson.MaterialFiles
                    .Where(f => f.File != null && (f.File.Exists || LinkFileResolver.CanResolve(f.File.FullName)))
                    .ToList();
        }

        return info;
    }
}
