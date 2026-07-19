using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNgTerrainPoc.Tests.Biome;

/// <summary>
/// Zone bands are distance-from-border ranges measured with the exact EDT. These tests pin
/// the band geometry on synthetic masks: band widths must be honored in meters (not pixels),
/// map-edge-touching regions have no border on that side, thin regions leave later bands
/// empty, and disjoint blobs band independently.
/// </summary>
public class BiomeZoneBanderTests
{
    private const int Size = 64;

    private static bool[] StripMask(int xMin, int xMax)
    {
        var mask = new bool[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = xMin; x <= xMax; x++)
            {
                mask[y * Size + x] = true;
            }
        }
        return mask;
    }

    [Fact]
    public void VerticalStrip_BorderBandAndInterior_SplitAtDepthMeters()
    {
        // Strip x=10..29, mpp=1: depth(x) = min(x-9, 30-x). Border band [0,5) covers
        // x=10..13 and x=26..29 (depth 1..4), interior the remaining 12 columns.
        var mask = StripMask(10, 29);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Equal(8 * Size, zones[0].Length);
        Assert.Equal(12 * Size, zones[1].Length);
        Assert.Equal(20 * Size, zones[0].Length + zones[1].Length);

        // Border pixels really are the outermost columns.
        foreach (var i in zones[0])
        {
            var x = i % Size;
            Assert.True(x is >= 10 and <= 13 or >= 26 and <= 29, $"unexpected border x={x}");
        }
    }

    [Fact]
    public void BandWidth_IsMeters_NotPixels()
    {
        // Same strip with mpp=2: depth(x=10)=2m, x=11→4m, x=12→6m. Band [0,5) now only
        // covers 2 columns per side.
        var mask = StripMask(10, 29);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 2f, bands);

        Assert.Equal(4 * Size, zones[0].Length);
        Assert.Equal(16 * Size, zones[1].Length);
    }

    [Fact]
    public void RegionTouchingMapEdge_HasNoBorderThere()
    {
        // Strip x=0..9 touches the left map edge; the only border is at x=10.
        // depth(x) = 10-x, so band [0,5) covers x=6..9 only.
        var mask = StripMask(0, 9);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Equal(4 * Size, zones[0].Length);
        Assert.Equal(6 * Size, zones[1].Length);
        foreach (var i in zones[0])
        {
            Assert.True(i % Size >= 6);
        }
    }

    [Fact]
    public void ThinRegion_LeavesDeeperBandsEmpty()
    {
        // 2-column strip: every pixel has depth 1m. Second band and interior stay empty.
        var mask = StripMask(10, 11);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Equal(2 * Size, zones[0].Length);
        Assert.Empty(zones[1]);
        Assert.Empty(zones[2]);
    }

    [Fact]
    public void DisjointBlobs_BandIndependently()
    {
        // Two 4x4 blobs. Perimeter pixels (12 each) have depth 1, the 2x2 center depth 2.
        var mask = new bool[Size * Size];
        foreach (var (bx, by) in new[] { (4, 4), (40, 40) })
        {
            for (var y = by; y < by + 4; y++)
            {
                for (var x = bx; x < bx + 4; x++)
                {
                    mask[y * Size + x] = true;
                }
            }
        }

        var bands = new[]
        {
            new BiomeZoneBandDefinition(2.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Equal(24, zones[0].Length);
        Assert.Equal(8, zones[1].Length);
    }

    [Fact]
    public void RegionCoveringWholeMap_IsAllInterior()
    {
        var mask = new bool[Size * Size];
        Array.Fill(mask, true);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Empty(zones[0]);
        Assert.Equal(Size * Size, zones[1].Length);
    }

    [Fact]
    public void ByteBasedApi_MatchesBoolMaskApi()
    {
        // The material-byte entry point (no per-material bool[] masks) must band identically.
        var mask = StripMask(10, 29);
        var materialData = new byte[Size * Size];
        for (var i = 0; i < mask.Length; i++)
        {
            materialData[i] = mask[i] ? (byte)3 : (byte)7;
        }

        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var fromBool = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);
        var fromBytes = BiomeZoneBander.ComputeZonePixels(materialData, 3, Size, 1f, bands);

        Assert.Equal(fromBool.Count, fromBytes.Count);
        for (var b = 0; b < fromBool.Count; b++)
        {
            Assert.Equal(fromBool[b], fromBytes[b]);
        }
    }

    [Fact]
    public void ZoneCounts_MatchZonePixelLengths()
    {
        var mask = StripMask(10, 29);
        var materialData = new byte[Size * Size];
        for (var i = 0; i < mask.Length; i++)
        {
            materialData[i] = mask[i] ? (byte)1 : (byte)0;
        }

        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var pixels = BiomeZoneBander.ComputeZonePixels(materialData, 1, Size, 1f, bands);
        var counts = BiomeZoneBander.ComputeZoneCounts(materialData, 1, Size, 1f, bands);

        Assert.Equal(pixels.Count, counts.Length);
        for (var b = 0; b < pixels.Count; b++)
        {
            Assert.Equal(pixels[b].Length, counts[b]);
        }
    }

    [Fact]
    public void HoleBytes_AreOutsideTheRegion()
    {
        // Material 2 strip with a hole column (byte 255) punched through the middle:
        // the hole acts as border, so pixels next to it land in the border band.
        var materialData = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 10; x <= 29; x++)
            {
                materialData[y * Size + x] = 2;
            }
            materialData[y * Size + 20] = 255;
        }

        var bands = new[]
        {
            new BiomeZoneBandDefinition(2.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(materialData, 2, Size, 1f, bands);

        // No zone may contain the hole column.
        foreach (var zone in zones)
        {
            foreach (var i in zone)
            {
                Assert.NotEqual(20, i % Size);
            }
        }

        // Pixels adjacent to the hole (x=19 and x=21, depth 1) are border-band pixels.
        Assert.Contains(zones[0], i => i % Size == 19);
        Assert.Contains(zones[0], i => i % Size == 21);
    }

    [Fact]
    public void FullScaleMap_BandsNeverContainForeignPixels_AndDistancesAreExact()
    {
        // Regression for the everywhere-trees bug: at 8192² the shared float EDT's 1e12 INF
        // sentinel exceeded float precision, foreground pixels got small nonzero distances,
        // and a 5m border band on a 2%-coverage material swallowed 29M foreign pixels
        // (franco_same_prio, forest_floor_italy). Small masks never trigger it — this must
        // run at real map scale.
        const int size = 8192;
        var materialData = new byte[size * size]; // material 0 everywhere

        // Scattered pseudo-random blobs of material 5 (like painted forest floor patches).
        var rng = new BiomeRandom(4711);
        for (var blob = 0; blob < 400; blob++)
        {
            var cx = rng.NextInt(4500) + 100;
            var cy = rng.NextInt(size - 200) + 100;
            var r = rng.NextInt(40) + 8;
            for (var y = cy - r; y <= cy + r; y++)
            {
                for (var x = cx - r; x <= cx + r; x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                    {
                        materialData[y * size + x] = 5;
                    }
                }
            }
        }

        // Plus one isolated 200×200 square with a known exact band size.
        const int sq = 6000;
        for (var y = sq; y < sq + 200; y++)
        {
            for (var x = sq; x < sq + 200; x++)
            {
                materialData[y * size + x] = 5;
            }
        }

        var bands = new[]
        {
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
        };

        var zones = BiomeZoneBander.ComputeZonePixels(materialData, 5, size, 1f, bands);

        // Invariant: every banded pixel carries the target material byte.
        foreach (var zone in zones)
        {
            foreach (var i in zone)
            {
                Assert.Equal(5, materialData[i]);
            }
        }

        // Exactness: the square's border band (depth 1..4) is its outer 4 rings.
        var squareBorderCount = zones[0].Count(i =>
        {
            var x = i % size;
            var y = i / size;
            return x >= sq && x < sq + 200 && y >= sq && y < sq + 200;
        });
        Assert.Equal(200 * 200 - 192 * 192, squareBorderCount);
    }

    [Fact]
    public void InteriorBandNotLast_Throws()
    {
        var mask = StripMask(10, 29);
        var bands = new[]
        {
            new BiomeZoneBandDefinition(0.0, IsInterior: true),
            new BiomeZoneBandDefinition(5.0, IsInterior: false),
        };

        Assert.Throws<ArgumentException>(() =>
            BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands));
    }
}
