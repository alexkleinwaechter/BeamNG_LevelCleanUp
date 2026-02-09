using Grille.BeamNG.SceneTree.Art;
using Grille.BeamNG.IO.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Lib_Tests.Sections;
public static class ArtTreeSection
{
    public static string DirName = "Tree/ArtGroup";

    public static void Run()
    {
        Section("ArtTree");

        Test("ArtTree", MainTree);
    }

    static void MainTree()
    {
        var terrain = new TerrainTemplate();

        var terrainMaterialDict = new JsonDict();
        terrainMaterialDict["class"] = TerrainMaterial.ClassName;
        terrainMaterialDict["name"] = "tmat";
        var terrainMaterial = new TerrainMaterial(terrainMaterialDict);

        var objectMaterialDict = new JsonDict();
        objectMaterialDict["class"] = ObjectMaterial.ClassName;
        objectMaterialDict["name"] = "omat";
        objectMaterialDict["Stages"] = new JsonDict[4] { new(), new(), new(), new() };
        var objectMaterial = new ObjectMaterial(objectMaterialDict);

        var root1 = new ArtGroupRoot();
        root1.Terrains.MaterialItems.Add(terrainMaterial);
        root1.Shapes.Groundcover.MaterialItems.Add(objectMaterial);
        root1.SaveTree(DirName);

        var root2 = new ArtGroup(root1.Name);
        root2.LoadTree(DirName);

        AssertArtGroupEqual(root1, root2);
    }

    static void AssertArtGroupEqual(ArtGroup expected, ArtGroup actual)
    {
        var list1 = new List<ArtItem>();
        var list2 = new List<ArtItem>();

        foreach (var item in expected.MaterialItems.EnumerateRecursive())
        {
            list1.Add(item);
        }

        foreach (var item in actual.MaterialItems.EnumerateRecursive())
        {
            list2.Add(item);
        }

        foreach (var item in expected.ManagedItems.EnumerateRecursive())
        {
            list1.Add(item);
        }

        foreach (var item in actual.ManagedItems.EnumerateRecursive())
        {
            list2.Add(item);
        }

        Comparison<ArtItem> comparison = (a, b) => a.Name.Value.CompareTo(b.Name.Value);

        list1.Sort(comparison);
        list2.Sort(comparison);

        AssertIsEqual(list1.Count, list2.Count);

        int count = list1.Count;

        for (int i = 0; i < count; i++)
        {
            var item1 = list1[i];
            var item2 = list2[i];

            AssertIsEqual(item1.Name.Value, item2.Name.Value);
            AssertIsEqual(item1.Class.Value, item2.Class.Value);
        }
    }
}
