namespace BeamNG.Procedural3D.RoadMesh;

/// <summary>
/// Doc 15 mesh-layer trims for one bridge deck at deck-to-deck merges: per-station parapet suppression
/// masks — the union roadway of intersecting decks must carry parapets only on its OUTER boundary, a
/// wall must never cross a roadway (b) — and end-stamp suppression at ends that continue onto another
/// deck instead of meeting the ground (c, the doc-13 terrain abutment suppression mirrored to the
/// mesh). A null / default trim produces the byte-identical legacy mesh.
/// </summary>
public sealed class BridgeDeckTrim
{
    /// <summary>Skip the start end-stamp block (the end continues onto another deck — the stamp would
    /// hang through the trunk deck mid-air). The box shell's end CAP face stays: at a conformed merge
    /// end it sits flush under the trunk surface and closes the mesh.</summary>
    public bool SuppressStartStamp { get; init; }

    /// <summary>See <see cref="SuppressStartStamp"/> — the far end.</summary>
    public bool SuppressEndStamp { get; init; }

    /// <summary>
    /// Per-station suppression of the LEFT parapet (same indexing as the cross-section list): true =
    /// this station's left edge lies ON a drivable surface — another deck's footprint or a ground
    /// road's flattened band, vertically coplanar; stacked decks and roads a full clearance below
    /// keep their walls. A parapet
    /// segment is dropped when EITHER of its two stations is suppressed; every surviving run whose end
    /// is an interior mask boundary is closed with a vertical cap face. Null ⇒ full-length parapet
    /// (legacy).
    /// </summary>
    public IReadOnlyList<bool>? LeftParapetSuppressed { get; init; }

    /// <summary>See <see cref="LeftParapetSuppressed"/> — the right edge.</summary>
    public IReadOnlyList<bool>? RightParapetSuppressed { get; init; }
}
