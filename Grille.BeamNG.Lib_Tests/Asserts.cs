using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNgLib_Tests;
internal static class Asserts
{
    public static void AssertDistance(float value0, float value1, string msg)
    {
        var distance = MathF.Abs(value0 - value1);
        AssertIsTrue(distance < 0.01f, $"Distance {distance}; {msg}");
    }

    public static void AssertDistance(Func<int, float> get0, Func<int, float> get1, int length, string msg)
    {
        for (int i = 0; i < length; i++)
        {
            float value0 = get0(i);
            float value1 = get1(i);
            AssertDistance(value0, value1, msg);
        }
    }
}
