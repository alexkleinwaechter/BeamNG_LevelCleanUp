using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Main;
public class LevelInfo : SimItem
{
    public const string ClassName = "LevelInfo";

    public JsonDictProperty<float> Gravity { get; }

    public JsonDictProperty<float> VisibleDistance { get; }

    public JsonDictProperty<float> NearClip { get; }

    public JsonDictProperty<float> FogDensity { get; }

    public JsonDictProperty<float> FogDensityOffset { get; }

    public JsonDictProperty<float> FogAtmosphereHeight { get; }

    public JsonDictProperty<Vector4> CanvasClearColor { get; }

    public LevelInfo(JsonDict? dict) : base(dict, ClassName)
    {
        NearClip = new(this, "nearClip");
        VisibleDistance = new(this, "visibleDistance");

        Gravity = new(this, "gravity");

        FogDensity = new(this, "fogDensity");
        FogDensityOffset = new(this, "fogDensityOffset");
        FogAtmosphereHeight = new(this, "fogAtmosphereHeight");

        CanvasClearColor = new(this, "canvasClearColor");
    }

    public LevelInfo() : this(null)
    {
        Name.Value = "theLevelInfo";

        NearClip.Value = 0.1f;
        VisibleDistance.Value = 4000;

        Gravity.Value = -9.810f;

        FogDensity.Value = 0.0001f;
        FogDensityOffset.Value = 0f;
        FogAtmosphereHeight.Value = 1000f;

        CanvasClearColor.Value = new Vector4(0, 0, 0, 1);
    }
}
