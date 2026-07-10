namespace BeamNG.Procedural3D.RoadMesh;

/// <summary>
/// Configuration for the 3D bridge-deck box mesh (E-B). Captures the v1 default cross-section
/// (deck thickness, parapets, abutment end walls, under-deck clearance) ratified 2026-06-07
/// (spec <c>09-bridge-deck-mesh-spec.md</c> §0/§2). The thickness rule
/// (<see cref="ComputeDeckThicknessMeters"/>) is the <b>single source of truth</b> shared by both
/// <see cref="BridgeDeckMeshBuilder"/> (soffit placement) and the terrain excavator (under-deck
/// clearance, D-5) so the soffit Z they assume is identical.
/// </summary>
/// <remarks>
/// Stage 2 (box deck) consumes only the thickness fields; the parapet/abutment/clearance fields are
/// declared now with their ratified defaults and consumed in later stages (parapets §3g, abutment
/// walls §3h, under-deck gap §4).
/// </remarks>
public sealed class BridgeDeckProfile
{
    /// <summary>Deck thickness as a fraction of span length (B-1): <c>thickness = ratio·span</c>, clamped.</summary>
    public float DeckThicknessSpanRatio { get; set; } = 0.05f;

    /// <summary>Lower clamp on deck thickness in meters (B-1).</summary>
    public float DeckThicknessMinMeters { get; set; } = 0.45f;

    /// <summary>Upper clamp on deck thickness in meters (B-1).</summary>
    public float DeckThicknessMaxMeters { get; set; } = 1.2f;

    /// <summary>
    /// Minimum parapet wall thickness in meters, enforced at every height of the wall.
    /// <see cref="BridgeDeckMeshBuilder"/> clamps both <see cref="ParapetBaseWidthMeters"/> and
    /// <see cref="ParapetTopWidthMeters"/> up to this value, so no configuration can produce a
    /// thinner (paper-like) wall.
    /// </summary>
    public const float MinParapetThicknessMeters = 0.4f;

    /// <summary>Parapet height in meters above the deck edge (B-2); <c>0</c> disables parapets. Consumed in Stage 3.</summary>
    public float ParapetHeightMeters { get; set; } = 0.9f;

    /// <summary>Parapet base width in meters along the deck normal (B-2 constant for v1); clamped to at least
    /// <see cref="MinParapetThicknessMeters"/> (and never below the top width). Consumed in Stage 3.</summary>
    public float ParapetBaseWidthMeters { get; set; } = 0.45f;

    /// <summary>Parapet top width in meters — the literal wall thickness at the parapet top; clamped to at
    /// least <see cref="MinParapetThicknessMeters"/>. Consumed in Stage 3.</summary>
    public float ParapetTopWidthMeters { get; set; } = 0.40f;

    /// <summary>
    /// Whether to build a solid abutment "stamp" block under each bridge end (B-3). The stamp is a watertight
    /// solid from the soffit down by <see cref="AbutmentDepthMeters"/>, <see cref="EndStampLengthMeters"/> long
    /// along the road; its top follows the deck soffit polyline so an arched/curved deck never opens a gap
    /// above it. Its inner face is the boundary the terrain excavator carves up against — so the road surface
    /// in the end zone is protected (E-B iteration 2). Consumed in Stage 4 / iteration 2.
    /// </summary>
    public bool GenerateAbutments { get; set; } = true;

    /// <summary>How far below the soffit the solid end-stamp block extends, in meters (B-3, v1).</summary>
    public float AbutmentDepthMeters { get; set; } = 1.0f;

    /// <summary>
    /// Along-road length (m) of the solid end-stamp block at each bridge end, i.e. the thickness of the
    /// "stamp" — the solid abutment block under each deck end.
    /// </summary>
    public float EndStampLengthMeters { get; set; } = 3.0f;

    /// <summary>
    /// Computes the deck thickness (riding surface top → soffit underside) for a span, clamped to the
    /// configured min/max. Single source of truth for both the mesh builder and the excavator (§2.1).
    /// </summary>
    /// <param name="spanLengthMeters">
    /// Deck span: <c>lastSection.DistanceAlongRoad − firstSection.DistanceAlongRoad</c>.
    /// </param>
    /// <param name="p">The deck profile supplying the ratio and clamp bounds.</param>
    public static float ComputeDeckThicknessMeters(float spanLengthMeters, BridgeDeckProfile p)
        => Math.Clamp(p.DeckThicknessSpanRatio * spanLengthMeters, p.DeckThicknessMinMeters, p.DeckThicknessMaxMeters);
}
