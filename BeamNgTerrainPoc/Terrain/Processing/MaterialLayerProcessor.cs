using BeamNgTerrainPoc.Terrain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
/// Processes material layer images to determine material placement on terrain.
/// </summary>
public static class MaterialLayerProcessor
{
    /// <summary>
    /// Processes material layer images and generates material indices for each terrain point.
    /// Materials with layer images are processed in reverse order (last = highest priority).
    /// Materials without layer images are skipped during auto-placement.
    /// </summary>
    /// <param name="materials">List of material definitions</param>
    /// <param name="size">Terrain size (width and height)</param>
    /// <returns>Array of material indices in BeamNG coordinate system (bottom-left to top-right)</returns>
    public static byte[] ProcessMaterialLayers(List<MaterialDefinition> materials, int size)
    {
        var materialIndices = new byte[size * size];
        
        // Initialize all to material 0 (default/fallback)
        Array.Fill(materialIndices, (byte)0);
        
        // Get only materials that have layer images
        var materialsWithImages = materials
            .Select((mat, index) => new { Material = mat, Index = index })
            .Where(x => x.Material.LayerImage != null)
            .ToList();
        
        // If no materials have images, return all zeros (material index 0)
        if (materialsWithImages.Count == 0)
        {
            return materialIndices;
        }
        
        // Process bottom-up (BeamNG coordinate system)
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // BeamNG array index (bottom-left origin)
                int flippedY = size - 1 - y;
                int arrayIndex = flippedY * size + x;
                
                // Find which material should be at this pixel
                // IMPORTANT: Process layers in REVERSE order
                // Last material layer with white pixel wins (highest priority)
                for (int i = materialsWithImages.Count - 1; i >= 0; i--)
                {
                    var matWithImage = materialsWithImages[i];
                    var pixel = matWithImage.Material.LayerImage![x, y]; // ! because we filtered for non-null
                    
                    // White pixel (255) means material is present
                    // Threshold at mid-gray (127) to handle anti-aliasing
                    if (pixel.PackedValue > 127)
                    {
                        materialIndices[arrayIndex] = (byte)matWithImage.Index;
                        break; // Found highest priority material
                    }
                }
            }
        }
        
        return materialIndices;
    }
    
    /// <summary>
    /// Helper method to create a full-coverage layer image (all white).
    /// Useful for creating default materials that cover the entire terrain.
    /// </summary>
    /// <param name="size">Image size</param>
    /// <returns>White L8 image</returns>
    public static Image<L8> CreateFullCoverageLayer(int size)
    {
        var image = new Image<L8>(size, size);
        var white = new L8(255);
        
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                image[x, y] = white;
            }
        }
        
        return image;
    }
    
    /// <summary>
    /// Helper method to create a no-coverage layer image (all black).
    /// </summary>
    /// <param name="size">Image size</param>
    /// <returns>Black L8 image</returns>
    public static Image<L8> CreateNoCoverageLayer(int size)
    {
        var image = new Image<L8>(size, size);
        // Default L8 is black (0), so just create empty image
        return image;
    }
}
