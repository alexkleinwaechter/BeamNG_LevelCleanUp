using Grille.BeamNG.SceneTree.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Lib_Tests.Sections;
internal class SimTreeSection
{
    public static string DirName = "Tree/SimGroup";

    public static void Run() 
    {
        Section("LevelTree");

        Test("MainTree", MainTree);
    }

    static void MainTree()
    {
        var terrain = new TerrainTemplate();

        var root1 = new SimGroupRoot();
        var terrain1 = new TerrainBlock(terrain);
        root1.MissionGroup.LevelObjects.Terrain.Items.Add(terrain1);
        root1.SaveTree(DirName);

        var root2 = new SimGroup(root1.Name);
        root2.LoadTree(DirName);

        AssertSimGroupEqual(root1, root2);

        var terrains = root2.Items.EnumerateRecursive<TerrainBlock>().ToArray();
        AssertIsEqual(1, terrains.Length);
    }

    static void AssertSimGroupEqual(SimGroup expected, SimGroup actual)
    {
        var list1 = new List<SimItem>();
        var list2 = new List<SimItem>();

        foreach (var item in expected.Items.EnumerateRecursive())
        {
            list1.Add(item);
        }

        foreach (var item in actual.Items.EnumerateRecursive())
        {
            list2.Add(item);
        }

        Comparison<SimItem> comparison = (a, b) => a.Name.Value.CompareTo(b.Name.Value);

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
