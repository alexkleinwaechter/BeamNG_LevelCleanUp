namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Deterministic PRNG for biome placement (xorshift128+ seeded via splitmix64).
/// System.Random's algorithm is not guaranteed stable across .NET versions;
/// biome regeneration must be reproducible (same seed + settings + terrain = same forest),
/// so we own the generator.
/// </summary>
public sealed class BiomeRandom
{
    private ulong _s0;
    private ulong _s1;

    public BiomeRandom(ulong seed)
    {
        var z = seed;
        _s0 = SplitMix64(ref z);
        _s1 = SplitMix64(ref z);
        if (_s0 == 0 && _s1 == 0)
        {
            _s1 = 0x9E3779B97F4A7C15UL;
        }
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var result = state;
        result = (result ^ (result >> 30)) * 0xBF58476D1CE4E5B9UL;
        result = (result ^ (result >> 27)) * 0x94D049BB133111EBUL;
        return result ^ (result >> 31);
    }

    public ulong NextUInt64()
    {
        var x = _s0;
        var y = _s1;
        _s0 = y;
        x ^= x << 23;
        _s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
        return _s1 + y;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform int in [0, maxExclusive). maxExclusive must be &gt; 0.</summary>
    public int NextInt(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>Uniform double in [min, max).</summary>
    public double NextRange(double min, double max) => min + NextDouble() * (max - min);
}

/// <summary>
/// Stable seed derivation (FNV-1a 64) — string.GetHashCode is randomized per process
/// and must never feed a persisted seed.
/// </summary>
public static class BiomeSeed
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Derive(int globalSeed, string layerId, int zoneIndex)
    {
        var hash = FnvOffset;
        hash = HashInt(hash, globalSeed);
        foreach (var c in layerId)
        {
            hash = (hash ^ c) * FnvPrime;
        }
        hash = HashInt(hash, zoneIndex);
        return hash;
    }

    private static ulong HashInt(ulong hash, int value)
    {
        var v = (uint)value;
        for (var i = 0; i < 4; i++)
        {
            hash = (hash ^ (v & 0xFF)) * FnvPrime;
            v >>= 8;
        }
        return hash;
    }
}
