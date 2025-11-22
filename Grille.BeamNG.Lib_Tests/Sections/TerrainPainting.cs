using Grille.BeamNG.IO.Binary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grille.BeamNG;
using System.IO.Enumeration;
using System.Drawing;

namespace Grille.BeamNG.Lib_Tests.Sections;

static class TerrainSection
{
    static string FileName = "terrain.ter";

    static string[] MaterialNames = ["Material0", "Material1"];

    public static void Run()
    {
        /*
        Section("TerrainPaint");

        var terrain0_x4 = new Terrain(4);

        var terrain0_x2 = new Terrain(2);
        terrain0_x2.Draw(new TerrainData(5), new Rectangle(0, 0, 2, 2));


        AssertIsEqual(5, terrain0_x2.Data[0, 0].Height);
        AssertIsEqual(5, terrain0_x2.Data[1, 1].Height);

        //terrain0_x4.Draw(terrain0_x2, new Point(0, 0));

        AssertIsEqual(5, terrain0_x4.Data[0, 0].Height);
        AssertIsEqual(5, terrain0_x4.Data[1, 1].Height);
        AssertIsEqual(0, terrain0_x4.Data[2, 2].Height);
        AssertIsEqual(0, terrain0_x4.Data[3, 3].Height);

        terrain0_x4.Draw(new TerrainData(0), new Rectangle(0, 0, 4, 4));

        terrain0_x2.Draw(new TerrainData(7), new Rectangle(0, 0, 2, 2));
        terrain0_x2.Data[1, 1].Height = 8;

        terrain0_x4.Draw(terrain0_x2, new Rectangle(0, 0, 4, 4), new Rectangle(1, 1, 1, 1));

        AssertIsEqual(8, terrain0_x4.Data[0, 0].Height);
        AssertIsEqual(8, terrain0_x4.Data[1, 1].Height);
        AssertIsEqual(8, terrain0_x4.Data[2, 2].Height);
        AssertIsEqual(8, terrain0_x4.Data[3, 3].Height);
        */
    }

}
