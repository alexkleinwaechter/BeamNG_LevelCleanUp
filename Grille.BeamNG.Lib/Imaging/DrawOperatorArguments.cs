using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Imaging;
public unsafe struct DrawOperatorArguments<T> where T : unmanaged
{
    public T* DstPointer;
    public T* SrcPointer;

    public DrawOperatorArguments(T* dstPointer, T* srcPointer)
    {
        SrcPointer = srcPointer;
        DstPointer = dstPointer;
    }
}
