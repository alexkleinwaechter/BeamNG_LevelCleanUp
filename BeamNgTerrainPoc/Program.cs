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

                    // Configure road smoothing parameters based on material type
                    RoadSmoothingParameters roadParameters = GetRoadSmoothingParameters(info.MaterialName, info.Index);

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

        /// <summary>
        /// Gets road smoothing parameters based on the material name.
        /// Returns null if the material is not a road type.
        /// </summary>
        /// <param name="materialName">The material name to check</param>
        /// <param name="layerIndex">The layer index for logging purposes</param>
        /// <returns>RoadSmoothingParameters or null if not a road material</returns>
        static RoadSmoothingParameters GetRoadSmoothingParameters(string materialName, int layerIndex)
        {
            // Check for ASPHALT1 - Main highways (8m wide, smooth)
            if (materialName.Equals("GROUNDMODEL_ASPHALT1", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Configuring HIGHWAY road smoothing for layer {layerIndex}");
                return CreateHighwayRoadParameters();
            }

            // Check for ASPHALT2 - Narrow steep roads (6m wide, mountainous)
            if (materialName.Equals("BeamNG_DriverTrainingETK_Asphalt", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Configuring MOUNTAIN road smoothing for layer {layerIndex}");
                return CreateMountainRoadParameters();
            }

            // Check for DIRT - Dirt roads (5m wide, terrain-following)
            if (materialName.Equals("Dirt", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Configuring DIRT road smoothing for layer {layerIndex}");
                return CreateDirtRoadParameters();
            }

            // Not a road material
            return null;
        }

        /// <summary>
        /// Creates road smoothing parameters for main highways (ASPHALT1).
        /// - 8 meters wide
        /// - Smooth terrain-following
        /// - Professional highway quality
        /// </summary>
        static RoadSmoothingParameters CreateHighwayRoadParameters()
        {
            return new RoadSmoothingParameters
            {
                EnableTerrainBlending = true,
                DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\highway",

                // ========================================
                // ROAD GEOMETRY - Highway (8m wide)
                // ========================================
                RoadWidthMeters = 8.0f,
                TerrainAffectedRangeMeters = 6.0f,       // Shoulder for smooth transition
                CrossSectionIntervalMeters = 0.5f,       // High quality sampling

                // ========================================
                // SLOPE CONSTRAINTS - Gentle highway grades
                // ========================================
                RoadMaxSlopeDegrees = 6.0f,              // Highway standard
                SideMaxSlopeDegrees = 45.0f,             // Standard embankment

                // ========================================
                // BLENDING
                // ========================================
                BlendFunctionType = BlendFunctionType.Cosine,

                // ========================================
                // POST-PROCESSING SMOOTHING
                // Eliminates staircase artifacts on road surface
                // ========================================
                EnablePostProcessingSmoothing = true,
                SmoothingType = PostProcessingSmoothingType.Gaussian,
                SmoothingKernelSize = 7,                 // Medium smoothing
                SmoothingSigma = 1.5f,
                SmoothingMaskExtensionMeters = 6.0f,     // Smooth into shoulder
                SmoothingIterations = 1,

                // ========================================
                // DEBUG VISUALIZATION
                // ========================================
                ExportSmoothedHeightmapWithOutlines = true,

                // ========================================
                // SPLINE-SPECIFIC SETTINGS
                // ========================================
                SplineParameters = new SplineRoadParameters
                {
                    // Skeletonization
                    SkeletonDilationRadius = 0,

                    // Junction handling - disabled for continuous curves
                    PreferStraightThroughJunctions = false,
                    JunctionAngleThreshold = 90.0f,
                    MinPathLengthPixels = 100.0f,

                    // Connectivity & path extraction
                    BridgeEndpointMaxDistancePixels = 40.0f,
                    DensifyMaxSpacingPixels = 1.5f,
                    SimplifyTolerancePixels = 0.5f,
                    UseGraphOrdering = true,
                    OrderingNeighborRadiusPixels = 2.5f,

                    // Spline curve fitting
                    SplineTension = 0.2f,                // Loose for smooth curves
                    SplineContinuity = 0.7f,             // Very smooth corners
                    SplineBias = 0.0f,

                    // Elevation smoothing
                    SmoothingWindowSize = 301,           // ~150m smoothing window
                    UseButterworthFilter = true,         // Butterworth filter
                    ButterworthFilterOrder = 4,          // Aggressive flatness
                    GlobalLevelingStrength = 0.0f,       // Terrain-following

                    // Debug output
                    ExportSplineDebugImage = true,
                    ExportSkeletonDebugImage = true,
                    ExportSmoothedElevationDebugImage = true
                }
            };
        }

        /// <summary>
        /// Creates road smoothing parameters for narrow mountain roads (ASPHALT2).
        /// - 6 meters wide (narrower than highways)
        /// - Steeper grades allowed
        /// - Tighter curves for mountainous terrain
        /// </summary>
        static RoadSmoothingParameters CreateMountainRoadParameters()
        {
            return new RoadSmoothingParameters
            {
                EnableTerrainBlending = true,
                DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\mountain",

                // ========================================
                // ROAD GEOMETRY - Narrow mountain road (6m wide)
                // ========================================
                RoadWidthMeters = 6.0f,                  // Narrower road
                TerrainAffectedRangeMeters = 8.0f,       // Tighter shoulder (road hugs terrain)
                CrossSectionIntervalMeters = 0.5f,       // High quality sampling

                // ========================================
                // SLOPE CONSTRAINTS - Steeper for mountains
                // ========================================
                RoadMaxSlopeDegrees = 8.0f,              // Steeper mountain grade
                SideMaxSlopeDegrees = 35.0f,             // Steeper embankment

                // ========================================
                // BLENDING
                // ========================================
                BlendFunctionType = BlendFunctionType.Cosine,

                // ========================================
                // POST-PROCESSING SMOOTHING
                // Light smoothing to preserve mountain character
                // ========================================
                EnablePostProcessingSmoothing = true,
                SmoothingType = PostProcessingSmoothingType.Gaussian,
                SmoothingKernelSize = 5,                 // Lighter smoothing
                SmoothingSigma = 1.0f,                   // Less aggressive
                SmoothingMaskExtensionMeters = 4.0f,     // Smaller extension
                SmoothingIterations = 1,

                // ========================================
                // SPLINE-SPECIFIC SETTINGS
                // ========================================
                SplineParameters = new SplineRoadParameters
                {
                    // Skeletonization
                    SkeletonDilationRadius = 0,

                    // Junction handling
                    PreferStraightThroughJunctions = false,
                    JunctionAngleThreshold = 90.0f,
                    MinPathLengthPixels = 50.0f,         // Allow shorter segments

                    // Connectivity & path extraction
                    BridgeEndpointMaxDistancePixels = 30.0f,
                    DensifyMaxSpacingPixels = 1.5f,
                    SimplifyTolerancePixels = 0.5f,
                    UseGraphOrdering = true,
                    OrderingNeighborRadiusPixels = 2.5f,

                    // Spline curve fitting - tighter for mountain curves
                    SplineTension = 0.3f,                // Tighter following
                    SplineContinuity = 0.5f,             // Allow sharper corners
                    SplineBias = 0.0f,

                    // Elevation smoothing
                    SmoothingWindowSize = 201,           // ~100m smoothing window
                    UseButterworthFilter = true,         // Butterworth filter
                    ButterworthFilterOrder = 3,          // Less aggressive
                    GlobalLevelingStrength = 0.0f,       // Follow terrain closely

                    // Debug output
                    ExportSplineDebugImage = true,
                    ExportSkeletonDebugImage = true,
                    ExportSmoothedElevationDebugImage = true
                }
            };
        }

        /// <summary>
        /// Creates road smoothing parameters for dirt roads.
        /// - 5 meters wide (narrow, rustic)
        /// - Minimal smoothing (preserve natural terrain character)
        /// - Higher tolerance for bumps and irregularities
        /// </summary>
        static RoadSmoothingParameters CreateDirtRoadParameters()
        {
            return new RoadSmoothingParameters
            {
                EnableTerrainBlending = true,
                DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\dirt",

                // ========================================
                // ROAD GEOMETRY - Narrow dirt road (5m wide)
                // ========================================
                RoadWidthMeters = 5.0f,                  // Narrow dirt road
                TerrainAffectedRangeMeters = 6.0f,       // Minimal shoulder
                CrossSectionIntervalMeters = 0.75f,      // Standard quality (faster)

                // ========================================
                // SLOPE CONSTRAINTS - Relaxed for dirt roads
                // ========================================
                RoadMaxSlopeDegrees = 10.0f,             // Allow steep sections
                SideMaxSlopeDegrees = 40.0f,             // Natural embankment

                // ========================================
                // BLENDING
                // ========================================
                BlendFunctionType = BlendFunctionType.Cosine,

                // ========================================
                // POST-PROCESSING SMOOTHING
                // Minimal smoothing to preserve rustic character
                // ========================================
                EnablePostProcessingSmoothing = true,
                SmoothingType = PostProcessingSmoothingType.Gaussian,
                SmoothingKernelSize = 5,                 // Light smoothing only
                SmoothingSigma = 0.8f,                   // Very gentle
                SmoothingMaskExtensionMeters = 3.0f,     // Minimal extension
                SmoothingIterations = 1,

                // ========================================
                // SPLINE-SPECIFIC SETTINGS
                // ========================================
                SplineParameters = new SplineRoadParameters
                {
                    // Skeletonization
                    SkeletonDilationRadius = 0,

                    // Junction handling
                    PreferStraightThroughJunctions = false,
                    JunctionAngleThreshold = 90.0f,
                    MinPathLengthPixels = 40.0f,         // Allow short segments

                    // Connectivity & path extraction
                    BridgeEndpointMaxDistancePixels = 25.0f,
                    DensifyMaxSpacingPixels = 2.0f,      // Less dense
                    SimplifyTolerancePixels = 0.75f,     // More simplification
                    UseGraphOrdering = true,
                    OrderingNeighborRadiusPixels = 2.5f,

                    // Spline curve fitting - preserve character
                    SplineTension = 0.4f,                // Follow terrain more closely
                    SplineContinuity = 0.3f,             // Allow natural bumps
                    SplineBias = 0.0f,

                    // Elevation smoothing
                    SmoothingWindowSize = 51,            // ~40m smoothing window
                    UseButterworthFilter = false,        // Use simple Gaussian
                    ButterworthFilterOrder = 2,          // Not used (UseButterworthFilter=false)
                    GlobalLevelingStrength = 0.0f,       // Follow terrain very closely

                    // Debug output
                    ExportSplineDebugImage = true,
                    ExportSkeletonDebugImage = true,
                    ExportSmoothedElevationDebugImage = true
                }
            };
        }
    }
}
