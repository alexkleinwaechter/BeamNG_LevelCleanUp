using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNgLib_Tests;
internal class Utils
{
    public static T[] CreateArray<T>(int size, T value)
    {
        var array = new T[size];
        for (int i = 0; i < array.Length; i++) { array[i] = value; }
        return array;
    }
}
