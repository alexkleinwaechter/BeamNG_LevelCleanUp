using Grille.BeamNG.Numerics;

namespace Grille.BeamNG.SceneTree.Main;

public class SpawnSphere : SimItem
{
    public const string ClassName = "SpawnSphere";

    public JsonDictProperty<string> DataBlock { get; }

    public JsonDictProperty<RotationMatrix3x3> RotationMatrix { get; }


    public SpawnSphere(JsonDict dict) : base(dict, ClassName)
    {
        DataBlock = new(this, "dataBlock");
        RotationMatrix = new(this, "rotationMatrix");
    }

    public SpawnSphere(float height) : this(new JsonDict())
    {
        Name.Value = "spawn_default";
        Class.Value = "SpawnSphere";
        DataBlock.Value = "SpawnSphereMarker";
        Position.Value = new System.Numerics.Vector3(0, 0, height);
    }

    /*
    "name": "spawn_default",
    "class": "SpawnSphere",
    "position": [
        0,
        0,
        0
    ],
    "autoplaceOnSpawn": "0",
    "dataBlock": "SpawnSphereMarker",
    "enabled": "1",
    "homingCount": "0",
    "indoorWeight": "1",
    "isAIControlled": "0",
    "lockCount": "0",
    "outdoorWeight": "1",
    "radius": 1,
    "rotationMatrix": [
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        1
    ],
    "sphereWeight": "1"
    */
}
