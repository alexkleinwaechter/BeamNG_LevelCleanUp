namespace Grille.BeamNG.SceneTree.Art;
public class ArtGroupRoot : ArtGroup
{
    public ArtGroup Terrains { get; }

    public ArtGroup Forest { get; }

    public ArtGroupShapes Shapes { get; }

    public ArtGroupRoot() : base("art")
    {
        Shapes = new ArtGroupShapes();
        Terrains = new("terrains");
        Forest = new("forest");

        Children.Add(Shapes);
        Children.Add(Terrains);
        Children.Add(Forest);
    }
}
