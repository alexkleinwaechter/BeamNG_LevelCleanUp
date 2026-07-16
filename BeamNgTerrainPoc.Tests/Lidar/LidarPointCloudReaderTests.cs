using System.Collections;
using BeamNgTerrainPoc.Terrain.Lidar;

namespace BeamNgTerrainPoc.Tests.Lidar;

public class LidarPointCloudReaderTests
{
    [Fact]
    public void Las14PointFormatsUseExtendedClassification()
    {
        Assert.Equal((byte)2, LasZipNativeReader.SelectClassification(6, 0, 2));
        Assert.Equal((byte)2, LasZipNativeReader.SelectClassification(10, 0, 2));
        Assert.Equal((byte)2, LasZipNativeReader.SelectClassification(3, 2, 0));
    }

    [Fact]
    public void FillMissingCellsInterpolatesRowsAndColumns()
    {
        const int size = 4;
        var samples = new ushort[size * size];
        var populated = new BitArray(samples.Length);

        Set(1, 1, 0);
        Set(3, 1, 100);
        Set(1, 3, 200);
        Set(3, 3, 300);

        LidarPointCloudReader.FillMissingCells(samples, populated, size);

        Assert.Equal(new ushort[] { 0, 0, 50, 100 }, samples[0..4]);
        Assert.Equal(new ushort[] { 0, 0, 50, 100 }, samples[4..8]);
        Assert.Equal(new ushort[] { 100, 100, 150, 200 }, samples[8..12]);
        Assert.Equal(new ushort[] { 200, 200, 250, 300 }, samples[12..16]);
        return;

        void Set(int x, int y, ushort value)
        {
            var index = y * size + x;
            samples[index] = value;
            populated[index] = true;
        }
    }

    [Fact]
    public void FillMissingCellsRejectsEmptyGrid()
    {
        var samples = new ushort[16];
        var populated = new BitArray(16);

        Assert.Throws<InvalidOperationException>(() =>
            LidarPointCloudReader.FillMissingCells(samples, populated, 4));
    }
}
