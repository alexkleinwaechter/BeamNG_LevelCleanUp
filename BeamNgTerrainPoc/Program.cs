// BeamNG Terrain Creator - Example Usage

using System.Drawing.Text;
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Processing;
using BeamNgTerrainPoc.Terrain.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;


internal class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== BeamNG Terrain Creator ===");
        Console.WriteLine();

        // Check command line arguments for custom usage
        if (args.Length > 0)
        {
            Console.WriteLine($"Arguments: {string.Join(", ", args)}");

            // Check for specific test mode
            if (args[0].Equals("complex", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running complex multi-material terrain creation...");
                Console.WriteLine();
                await CreateTerrainWithMultipleMaterials();
                Console.WriteLine();
                Console.WriteLine("Done!");
                return;
            }
        }

        Console.WriteLine("Running simple test terrain creation...");
        Console.WriteLine("(Use 'dotnet run -- complex' to run complex multi-material example)");
        Console.WriteLine();

        // Example: Create a simple test terrain with generated data
        await CreateSimpleTestTerrain();

        Console.WriteLine();
        Console.WriteLine("Done!");

        // ====================================================================================
        // HELPER METHODS
        // ====================================================================================

        static async Task CreateSimpleTestTerrain()
        {
            Console.WriteLine("--- Creating Simple Test Terrain ---");

            var creator = new TerrainCreator();

            // Create a simple 256x256 terrain for testing
            int size = 256;
            float maxHeight = 100.0f;

            Console.WriteLine($"Generating test data ({size}x{size})...");

            // Generate a simple heightmap (gradient from 0 to max)
            var heightmap = CreateTestHeightmap(size, maxHeight);

            // Create a simple material setup with one default material
            var parameters = new TerrainCreationParameters
            {
                Size = size,
                MaxHeight = maxHeight,
                HeightmapImage = heightmap, // Still supported for advanced scenarios
                Materials = new List<MaterialDefinition>
                {
                    new MaterialDefinition("grass") // Single material, entire terrain uses this
                }
            };

            // Create output directory in temp folder
            var outputDir = Path.Combine(Path.GetTempPath(), "BeamNG_TerrainTest");
            var outputPath = Path.Combine(outputDir, "test_terrain.ter");

            Console.WriteLine($"Output path: {outputPath}");
            Console.WriteLine();

            // Create terrain file
            var success = await creator.CreateTerrainFileAsync(outputPath, parameters);

            if (success)
            {
                Console.WriteLine();
                Console.WriteLine($"✓ Test terrain created successfully!");
                Console.WriteLine($"  Location: {outputPath}");
                Console.WriteLine();
                Console.WriteLine("NOTE: For library usage, prefer using file paths:");
                Console.WriteLine("  parameters.HeightmapPath = \"path/to/heightmap.png\"");
                Console.WriteLine("  materials.Add(new MaterialDefinition(\"grass\", \"path/to/layer.png\"))");
                Console.WriteLine("  This avoids ImageSharp dependency in your code!");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("✗ Failed to create test terrain.");
            }

            // Dispose images (when using HeightmapImage directly, you're responsible for disposal)
            heightmap.Dispose();
        }

        static Image<L16> CreateTestHeightmap(int size, float maxHeight)
        {
            var image = new Image<L16>(size, size);

            // Create a simple gradient heightmap
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Create a gradient from bottom-left (low) to top-right (high)
                    float normalizedHeight = (x + y) / (2.0f * size);
                    ushort pixelValue = (ushort)(normalizedHeight * 65535f);
                    image[x, y] = new L16(pixelValue);
                }
            }

            return image;
        }

        // ====================================================================================
        // ADVANCED EXAMPLES (commented out - uncomment to use with real image files)
        // ====================================================================================


        static async Task CreateTerrainWithMultipleMaterials()
        {
            Console.WriteLine("--- Creating Terrain with Multiple Materials ---");
            Console.WriteLine();

            // This method demonstrates complex terrain creation with real-world data:
            // - 4096x4096 heightmap (16.7 million pixels)
            // - 25 different terrain materials with layer masks
            // - Automatic material ordering based on layer index
            // - Proper material name extraction from filenames
            // - Output: ~48MB .ter file ready for BeamNG.drive

            var creator = new TerrainCreator();

            // Source directory with all terrain files
            string sourceDir = @"d:\temp\TestMappingTools\_import";

            // Terrain name (can be changed to match your terrain)
            string terrainName = "theTerrain";

            try
            {
                Console.WriteLine($"Loading terrain data from: {sourceDir}");
                Console.WriteLine($"Terrain name: {terrainName}");
                Console.WriteLine();

                // Heightmap path (using terrain name in filename)
                string heightmapPath = Path.Combine(sourceDir, $"{terrainName}_heightmap.png");
                if (!File.Exists(heightmapPath))
                {
                    Console.WriteLine($"ERROR: Heightmap not found at {heightmapPath}");
                    return;
                }

                Console.WriteLine($"Found heightmap: {heightmapPath}");

                // Find and parse all layer map files (using terrain name pattern)
                var layerMapFiles = Directory.GetFiles(sourceDir, $"{terrainName}_layerMap_*.png")
                    .Select(path => new
                    {
                        Path = path,
                        FileName = Path.GetFileName(path),
                        ParsedInfo = ParseLayerMapFileName(Path.GetFileName(path), terrainName)
                    })
                    .Where(x => x.ParsedInfo != null)
                    .OrderBy(x => x.ParsedInfo!.Value.Index)
                    .ToList();

                Console.WriteLine($"Found {layerMapFiles.Count} layer map files");
                Console.WriteLine();

                // Create material definitions with file paths
                var materials = new List<MaterialDefinition>();

                foreach (var layerFile in layerMapFiles)
                {
                    var info = layerFile.ParsedInfo!.Value;
                    Console.WriteLine($"Adding layer {info.Index}: {info.MaterialName}");

                    //Configure road smoothing parameters if needed
                    RoadSmoothingParameters roadParameters = null;
                    if (info.MaterialName.Contains("GROUNDMODEL_ASPHALT1", StringComparison.OrdinalIgnoreCase))
                    {
                        // Set specific parameters for road material
                        Console.WriteLine($"Configuring road smoothing for layer {info.Index}");
                        roadParameters = new RoadSmoothingParameters
                        {
                            // ========================================
                            // USE SPLINE APPROACH (OPTIMIZED)
                            // ========================================
                            Approach = RoadSmoothingApproach.Spline, // Fast EDT-based blending
                            EnableTerrainBlending = true,
                            DebugOutputDirectory = @"d:\temp\TestMappingTools\_output",
                            
                            // ROAD GEOMETRY (applies to all approaches)
                            RoadWidthMeters = 8.0f,
                            TerrainAffectedRangeMeters = 6.0f,       // 32m total width (realistic highway)
                            CrossSectionIntervalMeters = 0.5f,        // Auto-adjusts if needed
                            
                            // SLOPE CONSTRAINTS
                            RoadMaxSlopeDegrees = 4.0f,               // Allow gentle terrain following
                            SideMaxSlopeDegrees = 30.0f,              // Standard embankment
                            
                            // BLENDING
                            BlendFunctionType = BlendFunctionType.Cosine,
                            
                            
                            // ========================================
                            // SPLINE-SPECIFIC SETTINGS
                            // ========================================
                            SplineParameters = new SplineRoadParameters
                            {
                                // JUNCTION HANDLING
                                PreferStraightThroughJunctions = true,
                                JunctionAngleThreshold = 45.0f,
                                MinPathLengthPixels = 50.0f,
                                
                                // CONNECTIVITY & PATH EXTRACTION
                                BridgeEndpointMaxDistancePixels = 40.0f,
                                DensifyMaxSpacingPixels = 1.5f,
                                SimplifyTolerancePixels = 0.5f,
                                UseGraphOrdering = true,
                                OrderingNeighborRadiusPixels = 2.5f,
                                
                                // SPLINE CURVE FITTING
                                SplineTension = 0.2f,                 // Loose for smooth curves
                                SplineContinuity = 0.7f,              // Very smooth corners
                                SplineBias = 0.0f,
                                
                                // ELEVATION SMOOTHING - Butterworth filter
                                SmoothingWindowSize = 201,            // 50m radius
                                UseButterworthFilter = true,          // Maximally flat passband
                                ButterworthFilterOrder = 4,           // Aggressive flatness
                                GlobalLevelingStrength = 0.0f,        // DISABLED - terrain-following
                                
                                // DEBUG OUTPUT
                                ExportSplineDebugImage = true,
                                ExportSkeletonDebugImage = true,
                                ExportSmoothedElevationDebugImage = true
                            }
                        };
                    }

                    // Pass file path instead of loaded image
                    materials.Add(new MaterialDefinition(info.MaterialName, layerFile.Path, roadParameters));
                }

                // If no materials were found, add a default one
                if (materials.Count == 0)
                {
                    Console.WriteLine("No layer maps found. Adding default material: grass");
                    materials.Add(new MaterialDefinition("grass"));
                }

                Console.WriteLine();
                Console.WriteLine($"Total materials: {materials.Count}");
                Console.WriteLine();

                // Determine terrain size - we'll need to load heightmap temporarily to check size
                // Or you can specify it directly if you know it
                int terrainSize = 4096; // Adjust based on your heightmap

                // Create terrain parameters using file paths
                var parameters = new TerrainCreationParameters
                {
                    Size = terrainSize,
                    MaxHeight = 192.54f, // Adjust as needed for your terrain
                    MetersPerPixel = 1.0f, // Adjust based on your terrain scale
                    HeightmapPath = heightmapPath, // Use path instead of image
                    Materials = materials,
                    TerrainName = terrainName
                };

                // Create output path (using terrain name)
                string outputDir = @"d:\temp\TestMappingTools\_output";
                Directory.CreateDirectory(outputDir);
                string outputPath = Path.Combine(outputDir, $"{terrainName}.ter");

                Console.WriteLine($"Output path: {outputPath}");
                Console.WriteLine();

                // Create terrain file - images will be loaded and disposed automatically
                Console.WriteLine("Creating terrain file...");
                var success = await creator.CreateTerrainFileAsync(outputPath, parameters);

                Console.WriteLine();

                if (success)
                {
                    Console.WriteLine("✓ Terrain with multiple materials created successfully!");
                    Console.WriteLine($"  Location: {outputPath}");

                    // Display summary
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine();
                    Console.WriteLine("Summary:");
                    Console.WriteLine($"  Terrain name: {terrainName}");
                    Console.WriteLine($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / (1024.0 * 1024.0):F2} MB)");
                    Console.WriteLine($"  Terrain size: {terrainSize}x{terrainSize}");
                    Console.WriteLine($"  Max height: {parameters.MaxHeight}");
                    Console.WriteLine($"  Total materials: {materials.Count}");

                    Console.WriteLine();
                    Console.WriteLine("Material list:");
                    for (int i = 0; i < materials.Count; i++)
                    {
                        string hasImage = !string.IsNullOrWhiteSpace(materials[i].LayerImagePath) ? "✓" : "✗";
                        Console.WriteLine($"  [{i}] {hasImage} {materials[i].MaterialName}");
                    }
                }
                else
                {
                    Console.WriteLine("✗ Failed to create terrain.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Parses layer map filename to extract index and material name.
        /// Format: {terrainName}_layerMap_[index]_[materialName].png
        /// </summary>
        /// <param name="fileName">The filename to parse</param>
        /// <param name="terrainName">The name of the terrain (e.g., "theTerrain")</param>
        /// <returns>Tuple with Index and MaterialName, or null if parsing fails</returns>
        static (int Index, string MaterialName)? ParseLayerMapFileName(string fileName, string terrainName)
        {
            try
            {
                // Remove extension
                string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                // Expected format: {terrainName}_layerMap_[index]_[materialName]
                string prefix = $"{terrainName}_layerMap_";

                if (!nameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;

                // Remove prefix
                string remainder = nameWithoutExt.Substring(prefix.Length);

                // Find first underscore after index
                int underscoreIndex = remainder.IndexOf('_');
                if (underscoreIndex == -1)
                    return null;

                // Extract index and material name
                string indexStr = remainder.Substring(0, underscoreIndex);
                string materialName = remainder.Substring(underscoreIndex + 1);

                if (!int.TryParse(indexStr, out int index))
                    return null;

                return (index, materialName);
            }
            catch
            {
                return null;
            }
        }
    }
}
