using Grille.BeamNG.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Lib_Tests.Sections;
internal static class MathSection
{
    public static void Run()
    {
        Section("LevelTree");

        Test("Matrix3", Matrix3);
    }

    static void Matrix3()
    {
        var array = new float[] { -0.065461874f, -0.997852206f, 0.0023935393f, 0.99785471f, -0.0654635429f, -0.000646822504f, 0.000802123046f, 0.0023460621f, 0.999996901f };
        var matrix = new RotationMatrix3x3(array);
        var euler = matrix.ToEuler();
        var mat2 = new RotationMatrix3x3(euler);
        var eul2 =mat2.ToEuler();
    }

}
