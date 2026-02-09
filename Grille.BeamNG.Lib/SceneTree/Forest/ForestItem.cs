using Grille.BeamNG.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Forest;

public class ForestItem : JsonDictWrapper
{
    public JsonDictProperty<int> CtxId { get; }
    public JsonDictProperty<Vector3> Position { get; }
    public JsonDictProperty<RotationMatrix3x3> RotationMatrix { get; }
    public JsonDictProperty<float> Scale { get; }
    public JsonDictProperty<string> Type { get; }

    public ForestItem(JsonDict dict) : base(dict)
    {
        CtxId = new(this, "ctxid");
        Position = new(this, "pos");
        RotationMatrix = new(this, "rotationMatrix");
        Scale = new(this, "scale");
        Type = new(this, "type");
    }
}
