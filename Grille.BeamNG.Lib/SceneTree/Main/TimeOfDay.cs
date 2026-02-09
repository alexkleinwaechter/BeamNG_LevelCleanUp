using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Main;
public class TimeOfDay : SimItem
{
    public const string ClassName = "TimeOfDay";

    public JsonDictProperty<float> StartTime { get; }

    public TimeOfDay(JsonDict? dict) : base(dict, ClassName)
    {
        StartTime = new(this, "startTime");
    }

    public TimeOfDay() : this(null)
    {
        Name.Value = "tod";
        StartTime.Value = 0.15f;
    }
}
