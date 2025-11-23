using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.SceneTree.Art;
public class ArtGroupShapes : ArtGroup
{
    public ArtGroup Groundcover { get; }

    public ArtGroupShapes() : base("shapes")
    {
        Groundcover = new("groundcover");

        Children.Add(Groundcover);
    }
}
