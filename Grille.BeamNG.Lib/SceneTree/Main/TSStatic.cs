using Grille.BeamNG.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Main;
public class TSStatic : SimItem
{
    public const string ClassName = "TSStatic";

    public JsonDictProperty<string> ShapeName { get; }

    public JsonDictProperty<RotationMatrix3x3> RotationMatrix { get; }

    public JsonDictProperty<Vector3> Scale { get; }

    public TSStatic(JsonDict? dict) : base(dict, ClassName)
    {
        ShapeName = new(this, "shapeName");
        RotationMatrix = new(this, "rotationMatrix");
        Scale = new(this, "scale");
    }
}
