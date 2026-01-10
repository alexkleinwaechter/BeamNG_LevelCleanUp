using System.ComponentModel;
using System.Windows;
using HelixToolkit.Wpf.SharpDX;
using Material = HelixToolkit.Wpf.SharpDX.Material;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     View model for mesh items in the visibility control panel.
/// </summary>
public class MeshVisibilityItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isVisible = true;

    /// <summary>The mesh element in the viewport.</summary>
    public MeshGeometryModel3D MeshElement { get; set; } = null!;

    /// <summary>Selection info for this mesh.</summary>
    public MeshSelectionInfo SelectionInfo { get; set; } = null!;

    /// <summary>The original material of the mesh (before any selection highlighting).</summary>
    public Material? OriginalMaterial { get; set; }

    /// <summary>Gets or sets whether the mesh is visible.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                MeshElement.Visibility = value ? Visibility.Visible : Visibility.Hidden;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }
    }

    /// <summary>Gets or sets whether this mesh is currently selected in the 3D viewer.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <summary>Display name for the mesh - shows "MaterialName (MeshName)" format.</summary>
    public string DisplayName
    {
        get
        {
            var material = !string.IsNullOrEmpty(SelectionInfo.MaterialName) 
                ? SelectionInfo.MaterialName 
                : "(no material)";
            var mesh = !string.IsNullOrEmpty(SelectionInfo.MeshName) 
                ? SelectionInfo.MeshName 
                : "(unnamed)";
            return $"{material} ({mesh})";
        }
    }

    /// <summary>Material name for display (may be shared across multiple meshes).</summary>
    public string MaterialDisplayName => !string.IsNullOrEmpty(SelectionInfo.MaterialName)
        ? SelectionInfo.MaterialName
        : "(no material)";

    /// <summary>Indicates if the mesh has textures.</summary>
    public bool HasTextures => SelectionInfo.HasTextures;

    public event PropertyChangedEventHandler? PropertyChanged;
}
