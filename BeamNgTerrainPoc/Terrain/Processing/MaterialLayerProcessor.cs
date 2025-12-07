using BeamNgTerrainPoc.Terrain.Logging;
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
    /// Materials are processed in order of their index (0, 1, 2...).
    /// For each pixel, the HIGHEST index material with a white pixel in its layer wins.
    /// Materials without layer images are treated as having no coverage (all black) -
    /// they don't claim any pixels but their index position is preserved for the .ter file.
    /// </summary>
    /// <param name="materials">List of material definitions in order</param>
    /// <param name="size">Terrain size (width and height)</param>
    /// <returns>Array of material indices in BeamNG coordinate system (bottom-left to top-right)</returns>
    public static byte[] ProcessMaterialLayers(List<MaterialDefinition> materials, int size)
    {
        var materialIndices = new byte[size * size];
        
        // Initialize all to material 0 (default/fallback)
        Array.Fill(materialIndices, (byte)0);
        
        // Log material order for debugging
        TerrainLogger.Info($"Processing {materials.Count} materials for layer assignment:");
        for (int i = 0; i < materials.Count; i++)
        {
            var mat = materials[i];
            var hasLayer = !string.IsNullOrWhiteSpace(mat.LayerImagePath);
            TerrainLogger.Info($"  [{i}] {mat.MaterialName} - {(hasLayer ? "has layer map" : "NO layer map (won't claim pixels)")}");
        }
        
        // Build list of (materialIndex, image) pairs for materials that have layer images
        // The materialIndex is the position in the original list - this is critical!
        var loadedImages = new List<(int MaterialIndex, Image<L8> Image)>();
        
        try
        {
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                
                // Skip materials without layer images - they don't claim any pixels
                // but their index position is still valid for the .ter file
                if (string.IsNullOrWhiteSpace(mat.LayerImagePath))
                {
                    continue;
                }
                
                try
                {
                    var image = Image.Load<L8>(mat.LayerImagePath!);
                    
                    // Validate size matches terrain size
                    if (image.Width != size || image.Height != size)
                    {
                        TerrainLogger.Warning($"Layer image for '{mat.MaterialName}' " +
                                        $"has size {image.Width}x{image.Height} but terrain is {size}x{size}. Skipping.");
                        image.Dispose();
                        continue;
                    }
                    
                    // Store with the ORIGINAL material index, not the loadedImages index
                    loadedImages.Add((i, image));
                }
                catch (Exception ex)
                {
                    TerrainLogger.Warning($"Failed to load layer image for '{mat.MaterialName}': {ex.Message}");
                }
            }
            
            TerrainLogger.Info($"Loaded {loadedImages.Count} layer images for processing");
            
            // If no materials have images, return all zeros (material index 0)
            if (loadedImages.Count == 0)
            {
                TerrainLogger.Info("No layer images loaded - all pixels will use material index 0");
                return materialIndices;
            }
            
            // Process each pixel
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // BeamNG array index (bottom-left origin)
                    int flippedY = size - 1 - y;
                    int arrayIndex = flippedY * size + x;
                    
                    // Find which material should be at this pixel
                    // IMPORTANT: Process layers in REVERSE order of material index
                    // Highest material index with white pixel wins (highest priority)
                    for (int i = loadedImages.Count - 1; i >= 0; i--)
                    {
                        var (materialIndex, image) = loadedImages[i];
                        var pixel = image[x, y];
                        
                        // White pixel (>127) means material is present at this location
                        if (pixel.PackedValue > 127)
                        {
                            materialIndices[arrayIndex] = (byte)materialIndex;
                            break; // Found highest priority material for this pixel
                        }
                    }
                    // If no material claimed this pixel, it stays at 0 (default/fallback)
                }
            }
        }
        finally
        {
            // Always dispose loaded images
            foreach (var (_, image) in loadedImages)
            {
                image.Dispose();
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
