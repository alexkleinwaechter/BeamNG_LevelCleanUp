using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Main;
public class ScatterSky : SimItem
{
    public const string ClassName = "ScatterSky";

    public JsonDictProperty<float> SkyBrightness { get; }

    public JsonDictProperty<float> MieScattering { get; }

    public Gardient Colorize { get; }

    public Gardient SunScale { get; }

    public Gardient AmbientScale { get; }

    public Gardient FogScale { get; }

    public JsonDictProperty<bool> EnableFogFallBack { get; }

    public JsonDictProperty<int> ShadowTexSize { get; }

    public JsonDictProperty<float> ShadowDistance { get; }

    public ScatterSky(JsonDict? dict) : base(dict, ClassName)
    {
        SkyBrightness = new(this, "skyBrightness");
        MieScattering = new(this, "mieScattering");

        Colorize = new(this, "colorize");
        SunScale = new(this, "sunScale");
        AmbientScale = new(this, "ambientScale");
        FogScale = new(this, "fogScale");
        EnableFogFallBack = new(this, "enableFogFallBack");

        ShadowTexSize = new(this, "texSize");
        ShadowDistance = new(this, "shadowDistance");
    }

    public ScatterSky() : this(null)
    {
        Name.Value = "sunsky";

        SkyBrightness.Value = 40;

        Colorize.GradientFile.Value = "art/sky_gradients/default/gradient_colorize.png";
        SunScale.GradientFile.Value = "art/sky_gradients/default/gradient_sunscale.png";
        FogScale.GradientFile.Value = "art/sky_gradients/default/gradient_fog.png";
        AmbientScale.GradientFile.Value = "art/sky_gradients/default/gradient_ambient.png";
        EnableFogFallBack.Value = false;

        ShadowTexSize.Value = 2048;
        ShadowDistance.Value = 1500;

    }

    public class Gardient
    {
        public JsonDictProperty<Vector4> Color { get; }

        public JsonDictProperty<string> GradientFile { get; }

        public Gardient(ScatterSky owner, string key)
        {
            Color = new(owner, key);
            GradientFile = new(owner, $"{key}GradientFile");
        }
    }
}
