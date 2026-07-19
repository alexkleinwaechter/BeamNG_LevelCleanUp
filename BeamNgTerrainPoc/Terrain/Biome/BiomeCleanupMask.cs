namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Combined negative-list mask for the biome cleanup step: OR of terrain-material regions
/// and OSM layer masks (all flat row-major terrain space, row 0 = south), optionally grown
/// by a buffer distance so items leaning over a road/parking edge are caught although their
/// trunk pixel sits just off the mask.
/// </summary>
public static class BiomeCleanupMask
{
    /// <summary>ORs the region of one terrain material (from the raw .ter bytes) into the mask.</summary>
    public static void OrMaterial(bool[] mask, byte[] materialData, byte materialIndex)
    {
        if (mask.Length != materialData.Length)
        {
            throw new ArgumentException(
                $"Mask length {mask.Length} does not match material data length {materialData.Length}.", nameof(mask));
        }

        for (var i = 0; i < mask.Length; i++)
        {
            if (materialData[i] == materialIndex)
            {
                mask[i] = true;
            }
        }
    }

    /// <summary>ORs another mask (e.g. a loaded OSM layer) into the mask.</summary>
    public static void OrMask(bool[] mask, bool[] other)
    {
        if (mask.Length != other.Length)
        {
            throw new ArgumentException(
                $"Mask length {mask.Length} does not match other mask length {other.Length}.", nameof(other));
        }

        for (var i = 0; i < mask.Length; i++)
        {
            if (other[i])
            {
                mask[i] = true;
            }
        }
    }

    /// <summary>
    /// Grows the mask by <paramref name="bufferMeters"/>: the result contains every pixel whose
    /// Euclidean distance to the original mask is ≤ buffer (biome-private double-precision EDT).
    /// A non-positive buffer or an empty mask returns the input instance unchanged.
    /// </summary>
    public static bool[] ExpandByMeters(bool[] mask, int size, float metersPerPixel, double bufferMeters)
    {
        if (bufferMeters <= 0 || !mask.Any(m => m))
        {
            return mask;
        }

        var distance = BiomeZoneBander.ComputeDistanceToRegionMeters(mask, size, metersPerPixel);
        var expanded = new bool[mask.Length];
        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                expanded[row + x] = distance[y, x] <= bufferMeters;
            }
        }
        return expanded;
    }

    /// <summary>
    /// Tests whether a BeamNG world position (centered origin) lands on a set mask pixel.
    /// Uses floor — the exact inverse of the sampler's (pixel + jitter∈[0,1))·mpp placement,
    /// so every generated item maps back to the pixel it was sampled from. Positions outside
    /// the terrain are never hits.
    /// </summary>
    public static bool ContainsWorldPosition(bool[] mask, int size, float metersPerPixel, double worldX, double worldY)
    {
        var halfSizeMeters = size / 2.0 * metersPerPixel;
        var px = (int)Math.Floor((worldX + halfSizeMeters) / metersPerPixel);
        var py = (int)Math.Floor((worldY + halfSizeMeters) / metersPerPixel);
        if (px < 0 || py < 0 || px >= size || py >= size)
        {
            return false;
        }
        return mask[py * size + px];
    }

    /// <summary>Number of set pixels — reporting and empty-mask short-circuits.</summary>
    public static long CountSet(bool[] mask)
    {
        long count = 0;
        foreach (var set in mask)
        {
            if (set)
            {
                count++;
            }
        }
        return count;
    }
}
