using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Repaints the terrain MATERIAL under bridge deck footprints with a vegetation-free material
/// (doc `ai_docs/2026-06-10_bridge_generation_V2/07`). Doc 06 deliberately tucks terrain TIGHT under the
/// deck (overlap tongues 1–3 cm below the top, excavated cells just below the soffit), and billboard /
/// groundcover vegetation keys off the terrain material per cell — so grass grows straight through the
/// deck mesh wherever terrain and deck are close. The fix is material-level, not geometry-level:
///
///   for every heightmap cell under the deck footprint whose terrain is within
///   <c>clearanceMeters</c> BELOW the local (sloped, banked) deck surface, repaint its material;
///   leave deeper cells untouched so ravines and water under tall spans keep their natural material.
///
/// Footprint and lateral march are identical to <see cref="BridgeDeckExcavator"/>
/// (<see cref="BridgeDeckExcavator.CollectDeckGroups"/>), so exactly the cells the bridge machinery
/// touches are candidates. Runs AFTER all road material painting so the repaint wins under the deck.
/// </summary>
public static class BridgeUnderDeckMaterialPainter
{
    /// <summary>
    /// Default material names tried (case-insensitive CONTAINS) when no explicit selection exists,
    /// in preference order. Among several matches the SHORTEST name wins ("dirt" beats "dirt_loose_rocky").
    /// </summary>
    private static readonly string[] DefaultMaterialPreferences = ["dirt", "asphalt"];

    /// <summary>
    /// Resolves the default under-deck material from the level's terrain material list: prefer a name
    /// containing "dirt", else one containing "asphalt" (case-insensitive); among several matches take the
    /// shortest name. Null when nothing matches — the caller leaves the selection empty and the repaint
    /// becomes a warned no-op. Shared by the UI dropdown prepopulation and any headless fallback.
    /// </summary>
    public static string? ResolveDefaultMaterialName(IEnumerable<string> materialNames)
    {
        ArgumentNullException.ThrowIfNull(materialNames);
        var names = materialNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        foreach (var preference in DefaultMaterialPreferences)
        {
            var match = names
                .Where(n => n.Contains(preference, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Length)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match != null)
                return match;
        }

        return null;
    }

    /// <summary>
    /// Repaints tight under-deck cells of <paramref name="materialIndices"/> to the selected material.
    /// </summary>
    /// <param name="network">Solved network; bridge cross-sections carry the final deck elevation.</param>
    /// <param name="heightMap">Canonical 2D heightmap [y, x] in pre-base-height units (read-only here).</param>
    /// <param name="materialIndices">
    /// Per-cell material byte layer, index = y * size + x — the same orientation as
    /// <paramref name="heightMap"/> (mutated in place).
    /// </param>
    /// <param name="metersPerPixel">Heightmap resolution (same convention as the excavator).</param>
    /// <param name="materialName">
    /// Selected terrain material name; null/empty or not found in <paramref name="materialNames"/> ⇒
    /// warned no-op.
    /// </param>
    /// <param name="materialNames">Terrain material list in .ter index order (parameters.Materials).</param>
    /// <param name="clearanceMeters">Only cells within this distance below the deck surface are painted.</param>
    /// <param name="edgeMarginMeters">Extra lateral reach beyond the deck half-width (match the excavator).</param>
    /// <param name="log">Whether to emit the <c>[BRIDGE-MATERIAL]</c> summary/warning line.</param>
    public static BridgeUnderDeckPaintResult Paint(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        byte[] materialIndices,
        float metersPerPixel,
        string? materialName,
        IReadOnlyList<string> materialNames,
        float clearanceMeters,
        float edgeMarginMeters = BridgeDeckExcavator.DefaultEdgeMarginMeters,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentNullException.ThrowIfNull(materialIndices);
        ArgumentNullException.ThrowIfNull(materialNames);
        if (metersPerPixel <= 0f)
            throw new ArgumentOutOfRangeException(nameof(metersPerPixel));

        var result = new BridgeUnderDeckPaintResult();

        if (string.IsNullOrWhiteSpace(materialName))
        {
            if (log)
                TerrainCreationLogger.Current?.Warning(
                    "[BRIDGE-MATERIAL] no under-deck material selected — vegetation may poke through bridge decks");
            return result;
        }

        var materialIndex = -1;
        for (var i = 0; i < materialNames.Count; i++)
        {
            if (string.Equals(materialNames[i], materialName, StringComparison.OrdinalIgnoreCase))
            {
                materialIndex = i;
                break;
            }
        }

        if (materialIndex < 0 || materialIndex > byte.MaxValue)
        {
            if (log)
                TerrainCreationLogger.Current?.Warning(
                    $"[BRIDGE-MATERIAL] under-deck material '{materialName}' not found in the terrain material " +
                    "list — skipping repaint");
            return result;
        }

        result.ResolvedMaterialIndex = materialIndex;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var paintByte = (byte)materialIndex;

        // Same sub-pixel lateral step as the excavator, so the painted strip has no holes.
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        foreach (var deckSections in BridgeDeckExcavator.CollectDeckGroups(network))
        {
            var painted = 0;

            foreach (var cs in deckSections)
            {
                var deckZ = cs.TargetElevation;
                if (float.IsNaN(deckZ) || float.IsInfinity(deckZ))
                    continue;

                var halfWidth = cs.EffectiveRoadWidth / 2f + edgeMarginMeters;
                var normal = cs.NormalDirection;
                var bankSlope = MathF.Sin(cs.BankAngleRadians);

                for (var offset = -halfWidth; offset <= halfWidth; offset += lateralStep)
                {
                    var worldX = cs.CenterPoint.X + normal.X * offset;
                    var worldY = cs.CenterPoint.Y + normal.Y * offset;

                    var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                    var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                    var terrainZ = heightMap[py, px];
                    if (float.IsNaN(terrainZ) || float.IsInfinity(terrainZ))
                        continue;

                    // Deck surface at this lateral offset (longitudinal slope + lateral bank, same math as
                    // the excavator). Only "tight" cells — within clearanceMeters below it — are repainted;
                    // deeper terrain (ravines, water) keeps its natural material. Cells the excavator just
                    // shaved sit a few cm below the deck, so they always qualify.
                    var deckSurface = deckZ + offset * bankSlope;
                    if (terrainZ < deckSurface - clearanceMeters)
                        continue;

                    var cell = py * mapWidth + px;
                    if (materialIndices[cell] == paintByte)
                        continue;

                    materialIndices[cell] = paintByte;
                    painted++;
                    result.CellsPainted++;
                }
            }

            if (painted > 0)
                result.SpansPainted++;
        }

        if (log)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-MATERIAL] spans={result.SpansPainted} cellsPainted={result.CellsPainted} " +
                $"material={materialName} clearance={clearanceMeters:F2}m");

        return result;
    }
}

/// <summary>Aggregate result of a <see cref="BridgeUnderDeckMaterialPainter.Paint"/> pass.</summary>
public sealed class BridgeUnderDeckPaintResult
{
    /// <summary>Number of deck footprints that had at least one cell repainted.</summary>
    public int SpansPainted { get; set; }

    /// <summary>Total material cells repainted.</summary>
    public int CellsPainted { get; set; }

    /// <summary>Resolved index of the selected material in the terrain material list, −1 when unresolved.</summary>
    public int ResolvedMaterialIndex { get; set; } = -1;
}
