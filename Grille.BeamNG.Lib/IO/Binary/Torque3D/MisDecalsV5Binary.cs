using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.IO.Binary.Torque3D;
public class MisDecalsV5Binary
{
    public string[] DecalNames;

    public DecalInstance[] DecalInstances;

    public MisDecalsV5Binary()
    {
        DecalNames = Array.Empty<string>();
        DecalInstances = Array.Empty<DecalInstance>();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DecalInstance
    {
        public byte DataIndex;
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public uint RectIdx;
        public float Size;
        public byte RenderPriority;
    }
}
