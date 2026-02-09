using BeamNgTerrainPoc.Terrain.Logging;
using Grille.BeamNG.IO.Binary;

namespace BeamNgTerrainPoc.Terrain.Validation;

/// <summary>
/// Validates terrain files for elevation spikes and other anomalies.
/// Reads the generated .ter file and checks for illegal values that would
/// create monolith spikes in the game.
/// </summary>
public static class TerrainSpikeValidator
{
    /// <summary>
    /// Result of terrain spike validation.
    /// </summary>
    public class SpikeValidationResult
    {
        /// <summary>
        /// Whether the terrain passed validation (no spikes detected).
        /// </summary>
        public bool IsValid { get; set; } = true;

        /// <summary>
        /// Total number of spikes detected.
        /// </summary>
        public int SpikeCount { get; set; }

        /// <summary>
        /// List of detected spike locations with details.
        /// </summary>
        public List<SpikeInfo> Spikes { get; set; } = new();

        /// <summary>
        /// Terrain size in pixels.
        /// </summary>
        public int TerrainSize { get; set; }

        /// <summary>
        /// Maximum height parameter used.
        /// </summary>
        public float MaxHeight { get; set; }

        /// <summary>
        /// Statistics about the height distribution.
        /// </summary>
        public HeightStatistics Statistics { get; set; } = new();

        /// <summary>
        /// Warnings that don't fail validation but are worth noting.
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// Information about a detected spike.
    /// </summary>
    public class SpikeInfo
    {
        /// <summary>
        /// X coordinate in terrain pixels.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// Y coordinate in terrain pixels.
        /// </summary>
        public int Y { get; set; }

        /// <summary>
        /// Raw 16-bit height value from the .ter file.
        /// </summary>
        public ushort RawHeight { get; set; }

        /// <summary>
        /// Calculated height in meters.
        /// </summary>
        public float HeightMeters { get; set; }

        /// <summary>
        /// Average height of neighboring pixels.
        /// </summary>
        public float NeighborAverageHeight { get; set; }

        /// <summary>
        /// Difference from neighbor average (spike magnitude).
        /// </summary>
        public float SpikeMagnitude { get; set; }

        /// <summary>
        /// Material index at this location.
        /// </summary>
        public byte MaterialIndex { get; set; }

        /// <summary>
        /// Reason this was flagged as a spike.
        /// </summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Statistics about height distribution.
    /// </summary>
    public class HeightStatistics
    {
        public float MinHeight { get; set; }
        public float MaxHeight { get; set; }
        public float AverageHeight { get; set; }
        public float MedianHeight { get; set; }
        public ushort MinRawHeight { get; set; }
        public ushort MaxRawHeight { get; set; }
        public int ZeroHeightCount { get; set; }
        public int MaxHeightCount { get; set; }
        public int NearMaxHeightCount { get; set; } // Within 1% of max
    }

    /// <summary>
    /// Validates a terrain file for elevation spikes.
    /// </summary>
    /// <param name="terFilePath">Path to the .ter file</param>
    /// <param name="maxHeight">Maximum height value used for the terrain</param>
    /// <param name="spikeThresholdPercent">Percentage of max height to consider as spike threshold (default 90%)</param>
    /// <param name="neighborCheckRadius">Radius in pixels to check for neighbor comparison (default 3)</param>
    /// <returns>Validation result with spike information</returns>
    public static SpikeValidationResult ValidateTerrainFile(
        string terFilePath,
        float maxHeight,
        float spikeThresholdPercent = 0.90f,
        int neighborCheckRadius = 3)
    {
        var result = new SpikeValidationResult { MaxHeight = maxHeight };

        if (!File.Exists(terFilePath))
        {
            result.IsValid = false;
            result.Warnings.Add($"Terrain file not found: {terFilePath}");
            return result;
        }

        TerrainV9Binary binary;
        try
        {
            using var stream = File.OpenRead(terFilePath);
            binary = TerrainV9Serializer.Deserialize(stream);
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Warnings.Add($"Failed to read terrain file: {ex.Message}");
            return result;
        }

        var size = (int)binary.Size;
        result.TerrainSize = size;

        var heightData = binary.HeightData;
        var materialData = binary.MaterialData;

        // Convert raw heights to float meters
        var heightsMeters = new float[heightData.Length];
        for (int i = 0; i < heightData.Length; i++)
        {
            heightsMeters[i] = TerrainV9Serializer.GetSingleHeight(heightData[i], maxHeight);
        }

        // Calculate statistics
        result.Statistics = CalculateStatistics(heightData, heightsMeters, maxHeight);

        TerrainLogger.Info("=== TERRAIN SPIKE VALIDATION ===");
        TerrainLogger.Info($"  File: {Path.GetFileName(terFilePath)}");
        TerrainLogger.Info($"  Size: {size}x{size}");
        TerrainLogger.Info($"  MaxHeight parameter: {maxHeight}m");
        TerrainLogger.Info($"  Height range: {result.Statistics.MinHeight:F2}m - {result.Statistics.MaxHeight:F2}m");
        TerrainLogger.Info($"  Average height: {result.Statistics.AverageHeight:F2}m");
        TerrainLogger.Info($"  Median height: {result.Statistics.MedianHeight:F2}m");
        TerrainLogger.Info($"  Raw height range: {result.Statistics.MinRawHeight} - {result.Statistics.MaxRawHeight}");
        TerrainLogger.Info($"  Zero height pixels (raw=0): {result.Statistics.ZeroHeightCount:N0}");
        TerrainLogger.Info($"  Max height pixels (raw=65535): {result.Statistics.MaxHeightCount:N0}");
        TerrainLogger.Info($"  Near-max height pixels (raw>=64880, >99%): {result.Statistics.NearMaxHeightCount:N0}");

        // DIAGNOSTIC: Analyze the distribution of raw height values
        var rawValueDistribution = AnalyzeRawHeightDistribution(heightData);
        TerrainLogger.Info("  === RAW HEIGHT VALUE ANALYSIS ===");
        TerrainLogger.Info($"  Pixels with raw value 0: {rawValueDistribution.ZeroCount:N0}");
        TerrainLogger.Info($"  Pixels with raw value 65535 (max): {rawValueDistribution.MaxCount:N0}");
        TerrainLogger.Info($"  Pixels with raw value 65534: {rawValueDistribution.NearMaxCount:N0}");
        TerrainLogger.Info($"  Pixels in range 0-100: {rawValueDistribution.VeryLowCount:N0}");
        TerrainLogger.Info($"  Pixels in range 65400-65535: {rawValueDistribution.VeryHighCount:N0}");
        
        // Log some specific extreme values
        if (rawValueDistribution.MaxCount > 0)
        {
            TerrainLogger.Warning($"  !!! Found {rawValueDistribution.MaxCount} pixels at MAXIMUM raw value (65535) !!!");
            TerrainLogger.Warning($"      This equals {maxHeight:F2}m - these are potential spikes!");
        }

        // Calculate spike detection threshold
        var spikeThreshold = maxHeight * spikeThresholdPercent;
        var nearZeroThreshold = maxHeight * 0.05f;

        TerrainLogger.Info($"  Spike threshold: {spikeThreshold:F2}m ({spikeThresholdPercent:P0} of max)");

        // Scan for spikes - look specifically for isolated max-value pixels
        var spikesFound = 0;
        var maxSpikesToReport = 100; // Limit detailed reporting

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var index = y * size + x;
                var height = heightsMeters[index];
                var rawHeight = heightData[index];

                // SPIKE DETECTION CRITERIA:
                // 1. Raw value is at or very near maximum (65535 or 65534)
                // 2. OR height in meters is above threshold
                
                var isAtMaxRaw = rawHeight >= 65534;
                var isAboveThreshold = height >= spikeThreshold;
                
                if (!isAtMaxRaw && !isAboveThreshold)
                    continue;

                // Check neighbors to see if this is an isolated spike
                var (neighborAvg, neighborMin, neighborMax, neighborAvgRaw) = 
                    CalculateNeighborStats(heightsMeters, heightData, x, y, size, neighborCheckRadius);
                var spikeMagnitude = height - neighborAvg;

                // It's a spike if:
                // 1. This pixel is at max raw value AND neighbors are much lower
                // 2. OR there's a huge jump from neighbors
                var isSpike = false;
                var reason = string.Empty;

                // Case 1: Raw height at maximum AND neighbors are significantly lower
                if (isAtMaxRaw && neighborAvgRaw < 60000)
                {
                    isSpike = true;
                    reason = $"Raw value at MAX ({rawHeight}) while neighbor avg raw={neighborAvgRaw:F0}, " +
                             $"meters: {height:F2}m vs neighbor avg {neighborAvg:F2}m";
                }
                // Case 2: Height is at max AND neighbors are near zero
                else if (rawHeight == 65535 && neighborAvg < nearZeroThreshold)
                {
                    isSpike = true;
                    reason = $"Raw=65535 ({height:F2}m) while neighbors average {neighborAvg:F2}m (near zero)";
                }
                // Case 3: Massive jump from neighbors (more than 80% of max height difference)
                else if (spikeMagnitude > maxHeight * 0.8f && neighborMax < height * 0.2f)
                {
                    isSpike = true;
                    reason = $"Massive jump: {height:F2}m (raw={rawHeight}) vs neighbor max {neighborMax:F2}m";
                }

                if (isSpike)
                {
                    spikesFound++;

                    if (result.Spikes.Count < maxSpikesToReport)
                    {
                        result.Spikes.Add(new SpikeInfo
                        {
                            X = x,
                            Y = y,
                            RawHeight = rawHeight,
                            HeightMeters = height,
                            NeighborAverageHeight = neighborAvg,
                            SpikeMagnitude = spikeMagnitude,
                            MaterialIndex = materialData[index],
                            Reason = reason
                        });
                    }
                }
            }
        }

        result.SpikeCount = spikesFound;
        result.IsValid = spikesFound == 0;

        if (spikesFound > 0)
        {
            TerrainLogger.Warning($"  *** SPIKES DETECTED: {spikesFound:N0} pixels ***");
            
            // Log first few spikes with detailed info
            var spikesToLog = Math.Min(10, result.Spikes.Count);
            for (int i = 0; i < spikesToLog; i++)
            {
                var spike = result.Spikes[i];
                TerrainLogger.Warning($"    Spike #{i+1} at ({spike.X}, {spike.Y}):");
                TerrainLogger.Warning($"      Raw value: {spike.RawHeight} (max is 65535)");
                TerrainLogger.Warning($"      Height: {spike.HeightMeters:F2}m");
                TerrainLogger.Warning($"      Neighbor avg: {spike.NeighborAverageHeight:F2}m");
                TerrainLogger.Warning($"      Material index: {spike.MaterialIndex}");
                TerrainLogger.Warning($"      Reason: {spike.Reason}");
            }

            if (spikesFound > spikesToLog)
                TerrainLogger.Warning($"    ... and {spikesFound - spikesToLog} more spikes");
        }
        else
        {
            TerrainLogger.Info("  ? No spikes detected");
        }

        // Additional warnings
        if (result.Statistics.MaxHeightCount > size) // More than one row of max height pixels
        {
            result.Warnings.Add($"Large number of max-height pixels ({result.Statistics.MaxHeightCount:N0}) - may indicate data issue");
        }

        if (result.Statistics.ZeroHeightCount > size * size * 0.5f) // More than 50% zeros
        {
            result.Warnings.Add($"More than 50% of terrain is at zero height ({result.Statistics.ZeroHeightCount:N0} pixels)");
        }

        TerrainLogger.Info("=== VALIDATION COMPLETE ===");

        return result;
    }

    /// <summary>
    /// Analyzes the distribution of raw height values for diagnostics.
    /// </summary>
    private static RawHeightDistribution AnalyzeRawHeightDistribution(ushort[] heightData)
    {
        var result = new RawHeightDistribution();
        
        foreach (var h in heightData)
        {
            if (h == 0) result.ZeroCount++;
            else if (h == 65535) result.MaxCount++;
            else if (h == 65534) result.NearMaxCount++;
            
            if (h <= 100) result.VeryLowCount++;
            if (h >= 65400) result.VeryHighCount++;
        }
        
        return result;
    }

    private class RawHeightDistribution
    {
        public int ZeroCount { get; set; }
        public int MaxCount { get; set; }
        public int NearMaxCount { get; set; }
        public int VeryLowCount { get; set; }
        public int VeryHighCount { get; set; }
    }

    /// <summary>
    /// Calculates statistics about neighboring pixels including raw values.
    /// </summary>
    private static (float avgMeters, float minMeters, float maxMeters, float avgRaw) CalculateNeighborStats(
        float[] heightsMeters, ushort[] heightsRaw, int x, int y, int size, int radius)
    {
        var sumMeters = 0f;
        long sumRaw = 0;
        var minMeters = float.MaxValue;
        var maxMeters = float.MinValue;
        var count = 0;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue; // Skip center pixel

                var nx = x + dx;
                var ny = y + dy;

                if (nx >= 0 && nx < size && ny >= 0 && ny < size)
                {
                    var index = ny * size + nx;
                    var hMeters = heightsMeters[index];
                    var hRaw = heightsRaw[index];
                    
                    sumMeters += hMeters;
                    sumRaw += hRaw;
                    
                    if (hMeters < minMeters) minMeters = hMeters;
                    if (hMeters > maxMeters) maxMeters = hMeters;
                    
                    count++;
                }
            }
        }

        if (count == 0)
            return (0, 0, 0, 0);
            
        return (sumMeters / count, minMeters, maxMeters, (float)sumRaw / count);
    }

    /// <summary>
    /// Calculates the average height of neighboring pixels.
    /// </summary>
    private static float CalculateNeighborAverage(float[] heights, int x, int y, int size, int radius)
    {
        var sum = 0f;
        var count = 0;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue; // Skip center pixel

                var nx = x + dx;
                var ny = y + dy;

                if (nx >= 0 && nx < size && ny >= 0 && ny < size)
                {
                    var index = ny * size + nx;
                    sum += heights[index];
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : 0;
    }

    /// <summary>
    /// Calculates statistics about height distribution.
    /// </summary>
    private static HeightStatistics CalculateStatistics(ushort[] rawHeights, float[] heightsMeters, float maxHeight)
    {
        var stats = new HeightStatistics();

        if (heightsMeters.Length == 0)
            return stats;

        // Use a copy for QuickSelect since it partially reorders the data
        var heightsCopy = new float[heightsMeters.Length];
        Array.Copy(heightsMeters, heightsCopy, heightsMeters.Length);

        // Single pass for min, max, sum
        var min = float.MaxValue;
        var max = float.MinValue;
        var sum = 0.0;
        for (var i = 0; i < heightsCopy.Length; i++)
        {
            var h = heightsCopy[i];
            if (h < min) min = h;
            if (h > max) max = h;
            sum += h;
        }

        stats.MinHeight = min;
        stats.MaxHeight = max;
        stats.AverageHeight = (float)(sum / heightsCopy.Length);
        stats.MedianHeight = Processing.QuickSelect.Median(heightsCopy.AsSpan());

        stats.MinRawHeight = rawHeights.Min();
        stats.MaxRawHeight = rawHeights.Max();

        var nearMaxRawThreshold = (ushort)(65535 * 0.99);
        
        foreach (var raw in rawHeights)
        {
            if (raw == 0)
                stats.ZeroHeightCount++;
            if (raw == 65535)
                stats.MaxHeightCount++;
            if (raw >= nearMaxRawThreshold)
                stats.NearMaxHeightCount++;
        }

        return stats;
    }

    /// <summary>
    /// Validates and optionally fixes spikes in a terrain file.
    /// Creates a new file with spikes corrected if any are found.
    /// </summary>
    /// <param name="terFilePath">Path to the .ter file</param>
    /// <param name="maxHeight">Maximum height value used</param>
    /// <param name="fixSpikes">If true, creates a fixed version of the file</param>
    /// <returns>Validation result</returns>
    public static SpikeValidationResult ValidateAndFix(
        string terFilePath,
        float maxHeight,
        bool fixSpikes = true)
    {
        var result = ValidateTerrainFile(terFilePath, maxHeight);

        if (!result.IsValid && fixSpikes && result.Spikes.Count > 0)
        {
            TerrainLogger.Info("Attempting to fix spikes in terrain file...");

            try
            {
                // Read the binary data
                TerrainV9Binary binary;
                using (var stream = File.OpenRead(terFilePath))
                {
                    binary = TerrainV9Serializer.Deserialize(stream);
                }

                var size = (int)binary.Size;
                var heightData = binary.HeightData;
                var fixedCount = 0;

                // Fix each spike by using neighbor average
                foreach (var spike in result.Spikes)
                {
                    var index = spike.Y * size + spike.X;
                    
                    // Calculate neighbor average in raw units
                    var neighborAvgRaw = CalculateNeighborAverageRaw(heightData, spike.X, spike.Y, size, 3);
                    
                    // Use neighbor average, or a small default if neighbors are also problematic
                    var fixedValue = neighborAvgRaw > 0 ? neighborAvgRaw : (ushort)(65535 * 0.00035f); // ~0.23m at 650m max
                    
                    heightData[index] = fixedValue;
                    fixedCount++;
                }

                // Write fixed file
                var fixedPath = Path.Combine(
                    Path.GetDirectoryName(terFilePath)!,
                    Path.GetFileNameWithoutExtension(terFilePath) + "_fixed.ter");

                using (var stream = File.Create(fixedPath))
                {
                    TerrainV9Serializer.Serialize(stream, binary);
                }

                TerrainLogger.Info($"Fixed {fixedCount} spikes, saved to: {fixedPath}");
                result.Warnings.Add($"Fixed terrain saved to: {fixedPath}");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Failed to fix terrain: {ex.Message}");
                result.Warnings.Add($"Failed to fix terrain: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates neighbor average using raw ushort values.
    /// </summary>
    private static ushort CalculateNeighborAverageRaw(ushort[] heights, int x, int y, int size, int radius)
    {
        long sum = 0;
        var count = 0;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var nx = x + dx;
                var ny = y + dy;

                if (nx >= 0 && nx < size && ny >= 0 && ny < size)
                {
                    var index = ny * size + nx;
                    var h = heights[index];
                    
                    // Skip max values in neighbor average (they might be spikes too)
                    if (h < 65000)
                    {
                        sum += h;
                        count++;
                    }
                }
            }
        }

        return count > 0 ? (ushort)(sum / count) : (ushort)0;
    }
}
