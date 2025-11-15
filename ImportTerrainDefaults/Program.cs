using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Metadata;

if (args.Length < 2 || args.Length > 3)
{
  Console.WriteLine("Usage: ImportTerrainDefaults <path-to-terrainpreset.json> <target-directory> [fixbitdepth]");
  Console.WriteLine("  fixbitdepth - Optional: Convert existing target files to match source bit depth without changing content");
    return 1;
}

string jsonPath = args[0];
string targetDirectory = args[1];
bool fixBitDepth = args.Length == 3 && args[2].Equals("fixbitdepth", StringComparison.OrdinalIgnoreCase);

if (fixBitDepth)
{
    Console.WriteLine("Mode: Fix bit depth of existing files");
}
else
{
    Console.WriteLine("Mode: Create new black images");
}

// Validate input file exists
if (!File.Exists(jsonPath))
{
    Console.WriteLine($"Error: File not found: {jsonPath}");
  return 1;
}

// Create target directory if it doesn't exist
Directory.CreateDirectory(targetDirectory);

try
{
    // Read and parse the JSON file
    string jsonContent = await File.ReadAllTextAsync(jsonPath);
    var terrainData = JsonSerializer.Deserialize<TerrainData>(jsonContent);

    if (terrainData == null)
    {
     Console.WriteLine("Error: Failed to parse terrain preset JSON");
 return 1;
    }

    // Extract all PNG file paths
  var pngPaths = new List<string>();
    
    if (!string.IsNullOrEmpty(terrainData.HeightMapPath) && terrainData.HeightMapPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        pngPaths.Add(terrainData.HeightMapPath);
 
    if (!string.IsNullOrEmpty(terrainData.HoleMapPath) && terrainData.HoleMapPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        pngPaths.Add(terrainData.HoleMapPath);
    
 if (terrainData.OpacityMaps != null)
 pngPaths.AddRange(terrainData.OpacityMaps.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));

    Console.WriteLine($"Found {pngPaths.Count} PNG files to process");

    // Get the directory where the JSON file is located - this is where all PNG files are
  string sourceDirectory = Path.GetDirectoryName(jsonPath) ?? Directory.GetCurrentDirectory();

    // Process each PNG file
    int processedCount = 0;
    int skippedCount = 0;
    foreach (string pathFromJson in pngPaths)
    {
        try
        {
     // Extract only the filename from the path in JSON (ignore the path structure)
    string fileName = Path.GetFileName(pathFromJson);
  
    // Build the actual source path (PNG is in same directory as JSON)
     string sourcePath = Path.Combine(sourceDirectory, fileName);
       
  if (!File.Exists(sourcePath))
  {
Console.WriteLine($"Warning: Source file not found, skipping: {fileName}");
     continue;
     }

    // Read the source image info to get dimensions and bit depth
        var sourceImageInfo = await Image.IdentifyAsync(sourcePath);
    int width = sourceImageInfo.Width;
       int height = sourceImageInfo.Height;
   
 // Get PNG metadata to determine bit depth
 var sourcePngMetadata = sourceImageInfo.Metadata.GetPngMetadata();
    int sourceBitDepth = sourcePngMetadata.BitDepth.HasValue ? (int)sourcePngMetadata.BitDepth.Value : 8;
    
    Console.WriteLine($"Source: {fileName} - {width}x{height}, BitDepth: {sourceBitDepth}, ColorType: {sourcePngMetadata.ColorType}");

            // Build target path
       string targetPath = Path.Combine(targetDirectory, fileName);

        if (fixBitDepth)
        {
            // Fix bit depth mode: convert existing target file if bit depth differs
            if (!File.Exists(targetPath))
            {
     Console.WriteLine($"  Skipping: Target file does not exist yet: {fileName}");
         skippedCount++;
          continue;
  }

      // Read target file info
 var targetImageInfo = await Image.IdentifyAsync(targetPath);
    var targetPngMetadata = targetImageInfo.Metadata.GetPngMetadata();
            int targetBitDepth = targetPngMetadata.BitDepth.HasValue ? (int)targetPngMetadata.BitDepth.Value : 8;

    if (targetBitDepth == sourceBitDepth)
    {
              Console.WriteLine($"  OK: Target already has correct bit depth ({targetBitDepth}-bit)");
 skippedCount++;
      continue;
            }

     Console.WriteLine($"  Converting: {targetBitDepth}-bit -> {sourceBitDepth}-bit");

            // Load the existing target image content
       using var existingImage = await Image.LoadAsync(targetPath);
          
            // Convert to the correct bit depth while preserving content
      if (sourceBitDepth == 16)
       {
    using var converted = existingImage.CloneAs<L16>();
 await converted.SaveAsPngAsync(targetPath);
 Console.WriteLine($"  Converted to 16-bit grayscale: {fileName}");
            }
          else
  {
      using var converted = existingImage.CloneAs<L8>();
             await converted.SaveAsPngAsync(targetPath);
         Console.WriteLine($"  Converted to 8-bit grayscale: {fileName}");
  }

            processedCount++;
        }
        else
        {
            // Create new black image mode
          if (sourceBitDepth == 16)
            {
      // 16-bit grayscale (already black by default)
     using var blackImage = new Image<L16>(width, height);
 await blackImage.SaveAsPngAsync(targetPath);
      Console.WriteLine($"Created 16-bit grayscale: {fileName} ({width}x{height})");
     }
  else
   {
       // 8-bit grayscale (already black by default)
      using var blackImage = new Image<L8>(width, height);
      await blackImage.SaveAsPngAsync(targetPath);
    Console.WriteLine($"Created 8-bit grayscale: {fileName} ({width}x{height})");
   }

    processedCount++;
}
    }
 catch (Exception ex)
        {
   Console.WriteLine($"Error processing {Path.GetFileName(pathFromJson)}: {ex.Message}");
   }
    }

    if (fixBitDepth)
    {
      Console.WriteLine($"\nConverted {processedCount} files, {skippedCount} already correct or skipped");
    }
    else
    {
  Console.WriteLine($"\nSuccessfully created {processedCount} of {pngPaths.Count} files");
    }
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    return 1;
}

// Data classes for JSON deserialization
public class TerrainData
{
    [JsonPropertyName("heightMapPath")]
    public string? HeightMapPath { get; set; }

    [JsonPropertyName("heightScale")]
    public double HeightScale { get; set; }

    [JsonPropertyName("holeMapPath")]
  public string? HoleMapPath { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

[JsonPropertyName("opacityMaps")]
    public List<string>? OpacityMaps { get; set; }

    [JsonPropertyName("pos")]
  public Position? Pos { get; set; }

[JsonPropertyName("squareSize")]
  public double SquareSize { get; set; }

    [JsonPropertyName("type")]
 public string? Type { get; set; }
}

public class Position
{
    [JsonPropertyName("x")]
  public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}
