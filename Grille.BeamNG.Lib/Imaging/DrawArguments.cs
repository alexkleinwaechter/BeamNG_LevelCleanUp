using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Imaging;

public unsafe struct DrawArguments<T> where T : unmanaged
{
    public T* SrcBuffer;
    public T* DstBuffer;

    public Size DstSize;
    public Size SrcSize;
     
    public Rectangle DstRect;
    public Rectangle SrcRect;

    public Action<DrawOperatorArguments<T>> Operator;
}
