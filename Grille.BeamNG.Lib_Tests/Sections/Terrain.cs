using Grille.BeamNG.IO.Binary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grille.BeamNG;
using System.IO.Enumeration;

namespace Grille.BeamNG.Lib_Tests.Sections;

static class TerrainPaintSection
{
    static string FileName = "terrain.ter";

    static string[] MaterialNames = ["Material0", "Material1"];

    public static void Run()
    {
        Section("TerrainPaint");

        Test("TerrainV9Binary", TestTerrainV9Binary);
        Test("TerrainTemplate", TestTerrainTemplate);
        Test("TestAbstractTerrain", TestAbstractTerrain);
    }

    static void TestTerrainTemplate()
    {
        int resolution = 64;
        int length = resolution * resolution;

        float maxHeight = 512;
        float height = 10;
        var template = new TerrainTemplate() { 
            Height = height, 
            MaxHeight = maxHeight, 
            MaterialNames = MaterialNames,
            Resolution = resolution,
        };
        ushort u16height = template.U16Height;

        using var file = new MemoryStream();

        template.Serialize(file);

        file.Position = 0;
        var resultBinary = TerrainV9Serializer.Deserialize(file);
        AssertIListIsEqual(MaterialNames, resultBinary.MaterialNames);
        for (int i = 0; i < length; i++)
        {
            AssertIsEqual(u16height, resultBinary.HeightData[i]);
        }

        file.Position = 0;
        var resultTerrain0 = TerrainSerializer.Deserialize(file, maxHeight);
        AssertIListIsEqual(MaterialNames, resultTerrain0.MaterialNames);
        AssertDistance((i) => height, (i) => resultTerrain0.Data[i].Height, length, "terrain0 Deserialize");

        var resultTerrain1 = template.ToTerrain();
        AssertIListIsEqual(MaterialNames, resultTerrain1.MaterialNames);
        AssertDistance((i) => height, (i) => resultTerrain1.Data[i].Height, length, "terrain1 ToTerrain");
    }

    static void TestTerrainV9Binary()
    {
        var names = new string[]
        {
            "Material0",
            "Material1",
        };

        var terrain = new TerrainV9Binary(64)
        {
            MaterialNames = names
        };

        using var file = new MemoryStream();

        TerrainV9Serializer.Serialize(file, terrain);

        file.Position = 0;

        var result = TerrainV9Serializer.Deserialize(file);

        AssertIListIsEqual(names, result.MaterialNames);
    }

    static void TestAbstractTerrain()
    {
        float maxHeight = 512;
        var terrain1 = new Terrain(8);
        terrain1.Data[1, 1].Height = 8.5f;
        terrain1.Data[2, 2].IsHole = true;
        terrain1.Data[3, 3].Material = 45;
        terrain1.Save(FileName, maxHeight);


        var terrain2 = new Terrain(0);
        terrain2.Load(FileName, maxHeight);

        AssertIsEqual(terrain1.Size, terrain2.Size);
        AssertIListIsEqual(terrain1.MaterialNames, terrain2.MaterialNames);

        int length = terrain1.Data.Length;
        for (int i = 0; i < length; i++)
        {
            AssertIsEqual(Math.Round(terrain1.Data[i].Height, 1), Math.Round(terrain2.Data[i].Height, 1));
            AssertIsEqual(terrain1.Data[i].Material, terrain2.Data[i].Material);
            AssertIsEqual(terrain1.Data[i].IsHole, terrain2.Data[i].IsHole);
        }
    }
}
