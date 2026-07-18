using System.Numerics;
using BeamNG.Procedural3D.Exporters;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Exports bridge deck meshes to DAE (Collada) format for use in BeamNG.drive.
/// One flat ribbon deck mesh is produced per bridge spline and written to its own <c>.dae</c> file
/// (no chunking — decision D5). The deck follows the bridge's solved cross-section centerline at the
/// solved <see cref="UnifiedCrossSection.TargetElevation"/> spanning <see cref="UnifiedCrossSection.EffectiveRoadWidth"/>.
///
/// Bridge cross-sections are flagged <see cref="UnifiedCrossSection.IsExcluded"/> so the terrain is not
/// stamped/painted beneath them, but the elevation chain solve still populates their target elevation —
/// so they describe the deck surface directly (decision D2). This exporter therefore uses the opt-in
/// <see cref="CrossSectionConverter.ConvertSplineToWorldCoordinates"/> path that keeps excluded sections.
///
/// Like <see cref="RoadNetworkDaeExporter"/>, the mesh is written in BeamNG world coordinates (Z-up) so the
/// placed <c>TSStatic</c> at world position (0,0,0) aligns with the terrain and the approach roads.
/// The DAE uses BeamNG's <c>base00/start01/Colmesh-1/collision-1</c> hierarchy so DecalRoads with
/// <c>overObjects=true</c> have a collision mesh to project onto.
/// </summary>
public class BridgeDeckDaeExporter
{
    /// <summary>
    /// Default placeholder material name for bridge decks (D3). Replaced with real materials later.
    /// </summary>
    public const string DefaultMaterialName = "building_concrete"; //bridge_deck_placeholder

    /// <summary>
    /// Doc 15 (b): a parapet station is suppressed only when its edge point lies INSIDE the partner
    /// deck footprint by at least this much (m) — merely touching footprints (butt joints) keep their
    /// parapets.
    /// </summary>
    private const float ParapetInsideEpsilonMeters = 0.05f;

    /// <summary>
    /// Doc 15 (b): base tolerance (m) of the parapet-suppression Z window. The footprint test must
    /// be 3D — plan overlap alone opened parapets on decks stacked ABOVE/BELOW other decks
    /// (Manhattan render 2026-07-07). The window is ASYMMETRIC around the edge Z: downward (partner
    /// surface below the edge) exactly this tolerance — the coplanar union-roadway case conformance
    /// guarantees at genuine merges. Upward (partner surface ABOVE the edge) the window extends to
    /// parapet height + this tolerance: in the merge transition zone the landing deck rides up to
    /// ~1 parapet-height over the trunk edge, and a kept wall PIERCES the upper roadway (Manhattan
    /// render 2026-07-11: 621793096's parapet through 904452323's surface at the gore). Genuine
    /// grade-separated crossings sit a full clearance (≥ ~4.7 m) above and keep their walls.
    /// </summary>
    private const float ParapetCoplanarToleranceMeters = 0.5f;

    /// <summary>
    /// Determines whether a spline should generate a bridge deck.
    /// "Generate bridge" mode is gated on the same flag that suppresses terrain stamping (decision D1):
    /// the spline is a bridge AND <see cref="RoadSmoothingParameters.ExcludeBridgesFromTerrain"/> is set.
    /// </summary>
    public static bool ShouldGenerateDeck(ParameterizedRoadSpline spline)
    {
        return spline.IsBridge && spline.Parameters.ExcludeBridgesFromTerrain;
    }

    /// <summary>
    /// Exports a deck DAE file for every qualifying bridge spline in the network.
    /// </summary>
    /// <param name="network">The solved unified road network (cross-section elevations already computed).</param>
    /// <param name="shapesOutputDirectory">
    /// Directory the <c>bridge_{splineId}.dae</c> files are written to (e.g. <c>.../art/shapes/MT_bridges</c>).
    /// Created if it does not exist.
    /// </param>
    /// <param name="terrainSizePixels">Terrain size in pixels (for coordinate transformation).</param>
    /// <param name="metersPerPixel">Scale factor in meters per pixel.</param>
    /// <param name="terrainBaseHeight">Base height offset added to all Z coordinates.</param>
    /// <param name="materialName">Material name assigned to the deck mesh. Defaults to the placeholder material.</param>
    /// <param name="profile">
    /// Bridge-deck box profile (thickness rule + later-stage parapet/abutment config, E-B spec §0/§2).
    /// If null, ratified <see cref="BridgeDeckProfile"/> defaults are used.
    /// </param>
    /// <param name="heightMap">
    /// FINAL (post excavator/dam-stamper) terrain heightmap, terrain-local <c>[y, x]</c> meters — the ground
    /// piers stand on (doc 19 §1 ordering). Null ⇒ pier generation is skipped with a warning when enabled.
    /// </param>
    /// <returns>Per-bridge export results plus warnings for any skipped bridges.</returns>
    public BridgeDeckExportResult Export(
        UnifiedRoadNetwork network,
        string shapesOutputDirectory,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight,
        string materialName = DefaultMaterialName,
        BridgeDeckProfile? profile = null,
        float[,]? heightMap = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(shapesOutputDirectory);

        var result = new BridgeDeckExportResult();
        profile ??= new BridgeDeckProfile();

        // Merged-corridor mode (plan doc 11, Phase 5): build one deck per captured BridgeSpan from its snapshot
        // (the merged, smoothed sub-range), keyed by the span's stable OSM-way-id-derived id. The deck and the
        // approach road are then literally sampled from one centerline — continuity by construction.
        if (network.BridgeSpans.Count > 0)
        {
            Directory.CreateDirectory(shapesOutputDirectory);
            ExportFromSpans(network, shapesOutputDirectory, terrainSizePixels, metersPerPixel,
                terrainBaseHeight, materialName, profile, result, heightMap);
            result.Success = true;
            result.OutputDirectory = shapesOutputDirectory;
            return result;
        }

        var bridgeSplines = network.Splines.Where(ShouldGenerateDeck).ToList();
        if (bridgeSplines.Count == 0)
        {
            // Nothing to do — not a failure, just an empty result.
            result.Success = true;
            result.OutputDirectory = shapesOutputDirectory;
            return result;
        }

        Directory.CreateDirectory(shapesOutputDirectory);

        // NOTE: bridge-deck endpoint elevation + grade continuity is now solved once, network-wide, by
        // BridgeProfileSolver.RefineSpans in TerrainCreator BEFORE both DecalRoad generation and
        // this export — so the deck and the lane markings read identical elevations. The old export-time
        // ReconcileBridgeEndpointElevations band-aid (G0-only, ran after DecalRoad gen) has been removed to
        // avoid double-correcting (plan doc 05 §1.3 / §4.1 / Step 4).

        foreach (var spline in bridgeSplines)
        {
            var crossSections = CrossSectionConverter.ConvertSplineToWorldCoordinates(
                network, spline.SplineId, terrainSizePixels, metersPerPixel, terrainBaseHeight);

            // A bridge whose cross-sections lost their solved elevation (unchained — chain fragmentation
            // is a known fragility) can't be built into a deck. Skip it with a warning rather than emit a
            // NaN/degenerate mesh (spec §3 / acceptance §6.5). ElevationProfile/terrain fallback is a
            // future follow-up; for now we never crash and never silently produce a bad deck.
            if (crossSections.Count < 2)
            {
                result.Warnings.Add(
                    $"Bridge spline {spline.SplineId} ({spline.DisplayName ?? spline.OsmRoadType ?? "unnamed"}) " +
                    $"produced {crossSections.Count} usable cross-section(s) — skipped (no solved deck elevation).");
                result.BridgesSkipped++;
                continue;
            }

            // Closed box deck (E-B Stage 2): deck-top ribbon + soffit at top−thickness + 2 fascia side
            // faces + start/end caps. The deck follows the solved cross-sections: centerline at
            // TargetElevation, edges at the banked LeftEdgeElevation/RightEdgeElevation that
            // BridgeProfileSolver.RefineSpans wrote (center ± halfWidth·sin(bank)). The soffit
            // and fascia are derived by subtracting the span-proportional thickness in world-Z, so on a
            // banked bridge the whole box tilts with the deck. Thickness comes from the shared helper
            // BridgeDeckProfile.ComputeDeckThicknessMeters so the excavator (D-5) assumes the same soffit Z.
            var mesh = new BridgeDeckMeshBuilder()
                .Build(crossSections, profile, $"BridgeDeck_{spline.SplineId}", materialName);

            if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            {
                result.Warnings.Add(
                    $"Bridge spline {spline.SplineId} produced an empty deck mesh — skipped.");
                result.BridgesSkipped++;
                continue;
            }

            var daeFileName = $"bridge_{spline.SplineId}.dae";
            var outputPath = Path.Combine(shapesOutputDirectory, daeFileName);

            WriteBeamNgBridgeDae(mesh, spline.SplineId, outputPath);

            result.Decks.Add(new BridgeDeckExportItem
            {
                SplineId = spline.SplineId,
                DaeFileName = daeFileName,
                OutputPath = outputPath,
                Vertices = mesh.Vertices.Count,
                Triangles = mesh.Triangles.Count
            });
        }

        result.Success = true;
        result.OutputDirectory = shapesOutputDirectory;
        return result;
    }

    /// <summary>
    /// Builds one deck per captured <see cref="BridgeSpanSnapshot"/> (merged-corridor mode). Each deck is
    /// keyed by the span's stable <see cref="BridgeSpanSnapshot.SpanId"/> (derived from the OSM way-id set),
    /// so the file name and scene-object name are reproducible across runs (plan §10.2).
    /// </summary>
    private static void ExportFromSpans(
        UnifiedRoadNetwork network,
        string shapesOutputDirectory,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight,
        string materialName,
        BridgeDeckProfile profile,
        BridgeDeckExportResult result,
        float[,]? heightMap)
    {
        // Runs BEFORE bounds/trim/mesh so every consumer sees the extended stations consistently.
        ExtendLandedEndsOntoPartnerDecks(network);

        Dictionary<BridgeSpanSnapshot, (Vector2 Min, Vector2 Max)>? spanBounds = null;
        var warnedNoHeightMap = false;
        // Shared across all spans: every plan probes every partner span (§3b.4 + footprint
        // validation) — built here, AFTER the stations are final.
        var pierGeometryCache = new BridgePierPlanner.SpanGeometryCache();
        var pierPlanningStopwatch = new System.Diagnostics.Stopwatch();
        var pierSpansPlanned = 0;
        foreach (var span in network.BridgeSpans)
        {
            var crossSections = span.Stations
                .Select(st => StationToWorldCrossSection(st, terrainSizePixels, metersPerPixel, terrainBaseHeight))
                .ToList();

            if (crossSections.Count < 2)
            {
                result.Warnings.Add(
                    $"Bridge span {span.SpanId} on spline {span.SplineId} produced {crossSections.Count} usable " +
                    "station(s) — skipped (no solved deck geometry).");
                result.BridgesSkipped++;
                continue;
            }

            // Doc 15 (b)/(c): parapet openings + end-stamp suppression at deck-to-deck merges,
            // computed in terrain coordinates from the FINAL snapshots (masks index 1:1 into the
            // converted cross-sections). Null unless EnableSeamlessDeckOverlap is on.
            spanBounds ??= network.BridgeSpans.ToDictionary(s => s, ComputeSpanBounds);
            var trim = ComputeDeckTrim(network, span, spanBounds, profile.ParapetHeightMeters);

            var mesh = new BridgeDeckMeshBuilder()
                .Build(crossSections, profile, $"BridgeDeck_{span.SpanId}", materialName, trim);

            if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            {
                result.Warnings.Add($"Bridge span {span.SpanId} produced an empty deck mesh — skipped.");
                result.BridgesSkipped++;
                continue;
            }

            // Doc 19: intermediate pier supports for long spans. The plan is a pure function of the
            // snapshot + obstacle set + FINAL carved ground; flag off ⇒ nothing here runs and the DAE
            // stays byte-identical (piers are a separate, additive mesh — the deck mesh never changes).
            var pierRules = network.GetSplineById(span.SplineId)?.Parameters.BridgeRules;
            List<PierPlan>? pierPlans = null;
            if (pierRules is { EnableBridgePiers: true })
            {
                if (heightMap == null)
                {
                    if (!warnedNoHeightMap)
                    {
                        result.Warnings.Add(
                            "EnableBridgePiers is on but no heightmap was passed to the deck exporter — " +
                            "piers skipped (they must stand on the final carved ground).");
                        warnedNoHeightMap = true;
                    }
                }
                else
                {
                    pierPlanningStopwatch.Start();
                    pierPlans = PlanSpanPiers(network, span, pierRules, profile, heightMap, metersPerPixel,
                        pierGeometryCache);
                    pierPlanningStopwatch.Stop();
                    pierSpansPlanned++;
                }
            }

            Mesh? pierMesh = null;
            if (pierPlans is { Count: > 0 } && pierRules != null)
            {
                var specs = pierPlans
                    .Select(p => ToPierSpec(p, pierRules, terrainSizePixels, metersPerPixel, terrainBaseHeight))
                    .ToList();
                var built = new BridgePierMeshBuilder()
                    .Build(specs, $"BridgePiers_{span.SpanId}", materialName);
                if (built.Vertices.Count > 0)
                    pierMesh = built;
            }

            var daeFileName = $"bridge_{span.SpanId}.dae";
            var outputPath = Path.Combine(shapesOutputDirectory, daeFileName);
            var meshes = pierMesh != null ? new List<Mesh> { mesh, pierMesh } : [mesh];
            WriteBeamNgBridgeDae(meshes, span.SpanId, outputPath);

            result.Decks.Add(new BridgeDeckExportItem
            {
                SplineId = span.SpanId,
                DaeFileName = daeFileName,
                OutputPath = outputPath,
                Vertices = meshes.Sum(m => m.Vertices.Count),
                Triangles = meshes.Sum(m => m.Triangles.Count),
                Piers = pierPlans?.Count ?? 0
            });
        }

        if (pierSpansPlanned > 0)
        {
            TerrainCreationLogger.Current?.Detail(
                $"[PIER] planning total: {pierPlanningStopwatch.Elapsed.TotalMilliseconds:F0}ms " +
                $"across {pierSpansPlanned} span(s)");
        }
    }

    /// <summary>
    /// Converts a planned pier (terrain-local, pre-base-height) into the mesh builder's world-space
    /// spec: cap-top corners ON the banked soffit at the cap's lateral extents (linear interp of the
    /// deck-top edge Z minus deck thickness — the same single-source thickness the deck itself uses),
    /// columns at their planned plan positions with ground-embedded bottoms.
    /// </summary>
    private static BridgePierSpec ToPierSpec(
        PierPlan plan,
        BridgeRuleSystemOptions rules,
        int terrainSizePixels,
        float metersPerPixel,
        float terrainBaseHeight)
    {
        var thickness = plan.CenterZ - plan.SoffitZ;
        var halfWidth = plan.DeckWidth / 2f;
        var capHalf = plan.CapWidth / 2f;

        float SoffitAt(float offset)
        {
            var edgeZ = offset >= 0f ? plan.RightEdgeZ : plan.LeftEdgeZ;
            var frac = halfWidth > 1e-3f ? Math.Clamp(MathF.Abs(offset) / halfWidth, 0f, 1f) : 0f;
            return plan.CenterZ + (edgeZ - plan.CenterZ) * frac - thickness + terrainBaseHeight;
        }

        Vector2 World(Vector2 terrainXY) =>
            BeamNgCoordinateTransformer.TerrainToWorld2D(terrainXY, terrainSizePixels, metersPerPixel);

        var leftXY = World(plan.Center - plan.Normal * capHalf);
        var rightXY = World(plan.Center + plan.Normal * capHalf);

        return new BridgePierSpec
        {
            Normal = plan.Normal,
            Tangent = plan.Tangent,
            CapTopLeft = new Vector3(leftXY.X, leftXY.Y, SoffitAt(-capHalf)),
            CapTopRight = new Vector3(rightXY.X, rightXY.Y, SoffitAt(capHalf)),
            CapLength = rules.PierCapLengthMeters,
            CapDepth = rules.PierCapDepthMeters,
            Columns = plan.Columns.Select(c =>
            {
                var xy = World(c.Center);
                return new BridgePierColumnSpec(xy, c.BottomZ + terrainBaseHeight, c.Diameter);
            }).ToList(),
        };
    }

    /// <summary>
    /// Doc 19: assembles the pier planner's inputs for one span — the span's structure segment (merge-end
    /// flags), the shared obstacle set (<see cref="RoadSmoothingParameters.BridgeObstacles"/>), the network
    /// grade-separated crossings with EXACT lower-road half-widths from the lower spline's cross-sections,
    /// all partner snapshots (lower-deck exclusion) and the FINAL carved ground — and runs the plan.
    /// Diagnostics go to the ambient <see cref="TerrainCreationLogger"/> as <c>[PIER]</c> lines.
    /// </summary>
    private static List<PierPlan> PlanSpanPiers(
        UnifiedRoadNetwork network,
        BridgeSpanSnapshot span,
        BridgeRuleSystemOptions rules,
        BridgeDeckProfile profile,
        float[,] heightMap,
        float metersPerPixel,
        BridgePierPlanner.SpanGeometryCache geometryCache)
    {
        var spline = network.GetSplineById(span.SplineId);
        var segment = spline?.StructureSegments?.FirstOrDefault(s => s.SpanId == span.SpanId);

        var sectionCache = new Dictionary<int, List<UnifiedCrossSection>>();
        float LowerHalfWidth(GradeSeparatedCrossing crossing)
        {
            if (!crossing.HasLowerSpline) return BridgePierPlanner.DefaultRoadHalfWidthMeters;
            if (!sectionCache.TryGetValue(crossing.LowerSplineId, out var sections))
                sectionCache[crossing.LowerSplineId] =
                    sections = network.GetCrossSectionsForSpline(crossing.LowerSplineId).ToList();

            UnifiedCrossSection? nearest = null;
            var bestDistSq = float.MaxValue;
            foreach (var cs in sections)
            {
                var distSq = Vector2.DistanceSquared(cs.CenterPoint, crossing.CrossingXY);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = cs;
                }
            }

            return nearest != null && nearest.EffectiveRoadWidth > 0f
                ? nearest.EffectiveRoadWidth / 2f
                : BridgePierPlanner.DefaultRoadHalfWidthMeters;
        }

        return BridgePierPlanner.PlanSpan(new BridgePierPlanner.PlanInput
        {
            Span = span,
            Segment = segment,
            Options = rules,
            DeckProfile = profile,
            Obstacles = spline?.Parameters.BridgeObstacles,
            Crossings = network.GradeSeparatedCrossings,
            LowerRoadHalfWidth = LowerHalfWidth,
            AllSpans = network.BridgeSpans,
            GeometryCache = geometryCache,
            GroundZ = p => SampleHeightMap(heightMap, metersPerPixel, p),
            Log = msg => TerrainCreationLogger.Current?.Detail(msg),
        });
    }

    /// <summary>Nearest-cell heightmap sample at a terrain-local point (the
    /// <c>BridgeElevationPlanner.SampleTerrain</c> precedent). NaN outside / on bad cells.</summary>
    private static float SampleHeightMap(float[,] heightMap, float metersPerPixel, Vector2 p)
    {
        if (metersPerPixel <= 0f) return float.NaN;
        var w = heightMap.GetLength(1);
        var h = heightMap.GetLength(0);
        var px = Math.Clamp((int)(p.X / metersPerPixel), 0, w - 1);
        var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, h - 1);
        var v = heightMap[py, px];
        return float.IsFinite(v) ? v : float.NaN;
    }

    /// <summary>Converts a captured span station (terrain coords, pre-base-height) into a world-coordinate
    /// <see cref="RoadCrossSection"/> for the mesh builder. Mirrors
    /// <see cref="CrossSectionConverter.ConvertToWorldCoordinates"/>; the banked edge Z is carried explicitly.</summary>
    private static RoadCrossSection StationToWorldCrossSection(
        BridgeStation st, int terrainSizePixels, float metersPerPixel, float terrainBaseHeight)
    {
        return new RoadCrossSection
        {
            CenterPoint = BeamNgCoordinateTransformer.TerrainToWorld2D(st.Center, terrainSizePixels, metersPerPixel),
            CenterElevation = st.CenterZ + terrainBaseHeight,
            TangentDirection = st.Tangent,
            NormalDirection = st.Normal,
            WidthMeters = st.Width,
            BankAngleRadians = 0f, // banked edges are carried explicitly below
            DistanceAlongRoad = st.DistanceAlongSpline,
            LeftEdgeElevation = st.LeftEdgeZ + terrainBaseHeight,
            RightEdgeElevation = st.RightEdgeZ + terrainBaseHeight
        };
    }

    private static void WriteBeamNgBridgeDae(Mesh deckMesh, int splineId, string outputPath)
        => WriteBeamNgBridgeDae([deckMesh], splineId, outputPath);

    /// <summary>Writes one bridge DAE from its visual meshes (deck, and doc-19 piers when enabled).
    /// Every mesh is cloned into the Colmesh, so piers are drivable-into automatically.</summary>
    private static void WriteBeamNgBridgeDae(List<Mesh> meshes, int splineId, string outputPath)
    {
        var collisionMeshes = meshes
            .Select(CloneAsCollisionMesh)
            .Where(m => m.HasGeometry)
            .ToList();
        var scene = new BeamNgDaeScene
        {
            BaseName = $"bridge_{splineId}",
            LodLevels = [new LodLevel(1, meshes)],
            ColmeshMeshes = collisionMeshes.Count > 0 ? collisionMeshes : null
        };

        new ColladaExporter(new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false
        }).Export(scene, outputPath);
    }

    private static Mesh CloneAsCollisionMesh(Mesh source)
    {
        var collisionMesh = new Mesh { Name = "Colmesh-1" };
        collisionMesh.Vertices.AddRange(source.Vertices);
        collisionMesh.Triangles.AddRange(source.Triangles);
        collisionMesh.MaterialName = null;
        return collisionMesh;
    }

    /// <summary>Absolute cap (m) on a seam extension — safety bound for degenerate tangents.</summary>
    private const float SeamExtensionMaxMeters = 25f;

    /// <summary>Kink cap for the extension length formula (tan ≈ 60°) — sharper "merges" are crossings.</summary>
    private const float SeamExtensionMaxTan = 1.75f;

    /// <summary>Below this extension length (m) the perpendicular end caps already mate visually.</summary>
    private const float SeamExtensionMinMeters = 0.5f;

    /// <summary>Max |end Z − partner surface Z| (m) for a seam extension — the landed end must already
    /// sit ON the partner surface (anchor applied ⇒ zGap ≈ 0), else burying the cap would stab a
    /// foreign deck. Same tolerance that defines the coplanar union roadway for parapet openings.</summary>
    private const float SeamExtensionZCoherenceMeters = ParapetCoplanarToleranceMeters;

    /// <summary>The filler rides this far BELOW the roadway plane (m). Exactly-coincident coplanar
    /// surfaces z-fight (run 130041: conformed extensions rendered as flickering stripes over the
    /// partner deck); sub-flush keeps the partner/own top the visible+drivable one everywhere while
    /// the filler still closes the wedge to within an imperceptible dip.</summary>
    private const float SeamExtensionSubFlushMeters = 0.03f;

    /// <summary>
    /// Butt-seam wedge fix (Manhattan render 2026-07-11, span 1192907311 start on span 39479211 at
    /// junction 24): two z-conformed decks that meet end-to-end with a heading kink (Δ≈39°) each end
    /// with a cap PERPENDICULAR to their own tangent — the caps mate only at the centerline, opening
    /// a pie-slice hole on the outside of the bend (halfWidth·tanΔ ≈ 10 m of missing, non-drivable
    /// surface). A landed span end at a BUTT joint (landing station near the partner's span end —
    /// mid-deck gore landings have the partner continuing underneath, no hole possible) is extended
    /// straight along its end tangent by halfWidth·tanΔ (capped), each extension station riding the
    /// PARTNER deck's surface (own-grade extrapolation past its footprint) MINUS a small sub-flush
    /// so the filler is never coincident with the partner top (coincident coplanar surfaces
    /// z-fight — run 130041 rendered exactly-conformed extensions as flickering stripes). Only ONE
    /// side of a seam fills it: the wider deck (tie → higher span id), the narrower one only when
    /// the wider side has no applied landing of its own at that seam. Runs before trim/bounds/mesh
    /// so parapet masks cover the extension too. Doc-15 flags off ⇒ byte-identical.
    /// </summary>
    private static void ExtendLandedEndsOntoPartnerDecks(UnifiedRoadNetwork network)
    {
        foreach (var span in network.BridgeSpans)
        {
            var spline = network.GetSplineById(span.SplineId);
            var rules = spline?.Parameters.BridgeRules;
            if (rules is not { EnableSeamlessDeckOverlap: true, EnableDeckToDeckContinuity: true })
                continue;

            var seg = spline!.StructureSegments?.FirstOrDefault(s => s.SpanId == span.SpanId);
            if (seg == null || span.Stations.Count < 2)
                continue;

            if (seg is { StartDeckLandingApplied: true, StartDeckLanding: not null })
                ExtendLandedEnd(network, span, seg.StartDeckLanding, atStart: true);
            if (seg is { EndDeckLandingApplied: true, EndDeckLanding: not null })
                ExtendLandedEnd(network, span, seg.EndDeckLanding, atStart: false);
        }
    }

    private static void ExtendLandedEnd(
        UnifiedRoadNetwork network, BridgeSpanSnapshot span, DeckLandingRecord landing, bool atStart)
    {
        var partner = network.BridgeSpans.FirstOrDefault(o =>
            !ReferenceEquals(o, span) &&
            o.SplineId == landing.DeckSplineId &&
            o.Stations.Count >= 2 &&
            landing.DeckStation >= o.Stations[0].DistanceAlongSpline - 1f &&
            landing.DeckStation <= o.Stations[^1].DistanceAlongSpline + 1f);
        if (partner == null)
            return;

        var stations = span.Stations;
        var end = atStart ? stations[0] : stations[^1];
        var next = atStart ? stations[1] : stations[^2];

        var seamStation = NearestPartnerStationByArc(partner, landing.DeckStation);
        if (seamStation == null)
            return;

        // Butt joints only: the landing must sit near one of the PARTNER's span ends. A mid-deck
        // gore landing has the partner deck continuing underneath — nothing to fill, and a filler
        // would only shadow the partner's surface.
        var nearPartnerEnd = MathF.Min(
            MathF.Abs(landing.DeckStation - partner.Stations[0].DistanceAlongSpline),
            MathF.Abs(landing.DeckStation - partner.Stations[^1].DistanceAlongSpline));
        if (nearPartnerEnd > MathF.Max(end.Width, seamStation.Width))
            return;

        // One filler per seam: the wider deck owns it (tie → higher span id). The narrower side
        // steps in only when the owner has no applied landing of its own at this seam.
        var ownIsOwner = end.Width > seamStation.Width + 0.1f ||
                         (MathF.Abs(end.Width - seamStation.Width) <= 0.1f && span.SpanId > partner.SpanId);
        if (!ownIsOwner && PartnerHasAppliedLandingNear(network, partner, end.Center, end.Width))
            return;

        var ownTangent = SafeNormalize(end.Tangent);
        var partnerTangent = SafeNormalize(seamStation.Tangent);
        if (ownTangent == Vector2.Zero || partnerTangent == Vector2.Zero)
            return;

        // Pie-slice gap length at the outer corner: halfWidth · tan(acute heading delta).
        var cos = MathF.Abs(Vector2.Dot(ownTangent, partnerTangent));
        var sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
        if (cos < 1e-3f)
            return; // ~90° — a crossing shape, not a merge seam
        var tan = MathF.Min(sin / cos, SeamExtensionMaxTan);
        var half = end.Width / 2f;
        var length = MathF.Min(half * tan, SeamExtensionMaxMeters);
        if (length < SeamExtensionMinMeters)
            return;

        // Exact butt joints project onto the partner's extremity (rejected) — fall back to the
        // nearest partner station for the coherence test; the per-station conformance below then
        // uses the own-grade extrapolation, which the tiny gradeΔ at an applied landing keeps flush.
        var partnerZ = TryProjectOntoSpan(partner, end.Center, out var projectedZ)
            ? projectedZ
            : NearestPartnerCenterZ(partner, end.Center, maxDistMeters: half + 2f);
        if (float.IsNaN(partnerZ) || MathF.Abs(end.CenterZ - partnerZ) > SeamExtensionZCoherenceMeters)
            return;

        var ds = MathF.Max(MathF.Abs(end.DistanceAlongSpline - next.DistanceAlongSpline), 0.25f);
        var count = Math.Clamp((int)MathF.Ceiling(length / ds), 1, 200);
        var dir = atStart ? -ownTangent : ownTangent;
        var normal = SafeNormalize(end.Normal);
        var centerGrade = (end.CenterZ - next.CenterZ) / ds;   // per meter moving OUT of the span
        var leftBank = end.LeftEdgeZ - end.CenterZ;
        var rightBank = end.RightEdgeZ - end.CenterZ;

        var extension = new List<BridgeStation>(count);
        for (var i = 1; i <= count; i++)
        {
            var offset = ds * i;
            var center = end.Center + dir * offset;
            var centerZ = TryProjectOntoSpan(partner, center, out var pz)
                ? pz
                : end.CenterZ + centerGrade * offset;
            var leftZ = TryProjectOntoSpan(partner, center - normal * half, out var lz)
                ? lz
                : centerZ + leftBank;
            var rightZ = TryProjectOntoSpan(partner, center + normal * half, out var rz)
                ? rz
                : centerZ + rightBank;

            extension.Add(new BridgeStation
            {
                Center = center,
                Normal = end.Normal,
                Tangent = end.Tangent,
                Width = end.Width,
                CenterZ = centerZ - SeamExtensionSubFlushMeters,
                LeftEdgeZ = leftZ - SeamExtensionSubFlushMeters,
                RightEdgeZ = rightZ - SeamExtensionSubFlushMeters,
                DistanceAlongSpline = end.DistanceAlongSpline + (atStart ? -offset : offset)
            });
        }

        if (atStart)
        {
            extension.Reverse(); // farthest first, so station order stays monotonic
            stations.InsertRange(0, extension);
        }
        else
        {
            stations.AddRange(extension);
        }

        TerrainCreationLogger.Current?.InfoFileOnly(
            $"[BRIDGE-MESH] seam extension span={span.SpanId} {(atStart ? "start" : "end")} " +
            $"onto span={partner.SpanId}: headingΔ={MathF.Atan2(sin, cos) * 180f / MathF.PI:F1}deg " +
            $"+{length:F1}m ({count} station(s)) — butt-seam wedge buried in the partner deck");
    }

    /// <summary>Center Z of the partner station nearest in plan, or NaN when none is within
    /// <paramref name="maxDistMeters"/>.</summary>
    private static float NearestPartnerCenterZ(BridgeSpanSnapshot partner, Vector2 point, float maxDistMeters)
    {
        var best = float.NaN;
        var bestD = maxDistMeters;
        foreach (var st in partner.Stations)
        {
            var d = Vector2.Distance(st.Center, point);
            if (d <= bestD)
            {
                bestD = d;
                best = st.CenterZ;
            }
        }

        return best;
    }

    /// <summary>The partner snapshot station nearest to an arc-length station along it.</summary>
    private static BridgeStation? NearestPartnerStationByArc(BridgeSpanSnapshot span, float station)
    {
        BridgeStation? best = null;
        var bestD = float.MaxValue;
        foreach (var st in span.Stations)
        {
            var d = MathF.Abs(st.DistanceAlongSpline - station);
            if (d < bestD)
            {
                bestD = d;
                best = st;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the partner span has an APPLIED deck landing of its own whose end sits near
    /// <paramref name="seamPoint"/> — i.e. the seam's designated filler will actually fill it, so
    /// the narrower side may defer (see <see cref="ExtendLandedEnd"/>'s single-filler rule).
    /// </summary>
    private static bool PartnerHasAppliedLandingNear(
        UnifiedRoadNetwork network, BridgeSpanSnapshot partner, Vector2 seamPoint, float toleranceMeters)
    {
        var partnerSeg = network.GetSplineById(partner.SplineId)?
            .StructureSegments?.FirstOrDefault(s => s.SpanId == partner.SpanId);
        if (partnerSeg == null)
            return false;

        if (partnerSeg is { StartDeckLandingApplied: true, StartDeckLanding: not null } &&
            Vector2.Distance(partner.Stations[0].Center, seamPoint) <= toleranceMeters)
            return true;

        return partnerSeg is { EndDeckLandingApplied: true, EndDeckLanding: not null } &&
               Vector2.Distance(partner.Stations[^1].Center, seamPoint) <= toleranceMeters;
    }

    /// <summary>
    /// Doc 15 (b)/(c): the mesh trims for one span at deck-to-deck merges. (c) End stamps are skipped
    /// at ends whose segment continues onto another deck (the doc-13 terrain abutment suppression,
    /// mirrored to the mesh — at a merge end the stamp hangs through the trunk deck mid-air). (b) A
    /// parapet station is suppressed where its edge point lies ON another span's deck SURFACE —
    /// inside the plan footprint AND vertically coplanar (<see cref="ParapetCoplanarToleranceMeters"/>)
    /// — so the union roadway keeps parapets only on its OUTER boundary: the landing span opens its
    /// inner parapet through the gore and drops both walls at the merge end, the trunk opens exactly
    /// the gore-mouth segment the ramp drives through. The criterion is purely geometric, NOT the
    /// landing graph (Manhattan render 2026-07-07: landing-pair gating both over-opened — a pair's
    /// footprints also overlap where the decks cross at DIFFERENT layers — and under-opened — two
    /// ramps conformed onto the same trunk share a roadway without being a pair); stacked decks are
    /// excluded by elevation, coplanar overlaps open regardless of who landed on whom. Null (legacy
    /// mesh) unless <c>EnableSeamlessDeckOverlap</c> + <c>EnableDeckToDeckContinuity</c> are on for
    /// the owning spline.
    /// </summary>
    private static BridgeDeckTrim? ComputeDeckTrim(
        UnifiedRoadNetwork network,
        BridgeSpanSnapshot span,
        IReadOnlyDictionary<BridgeSpanSnapshot, (Vector2 Min, Vector2 Max)> spanBounds,
        float parapetHeightMeters)
    {
        var spline = network.GetSplineById(span.SplineId);
        var rules = spline?.Parameters.BridgeRules;
        if (rules is not { EnableSeamlessDeckOverlap: true, EnableDeckToDeckContinuity: true })
            return null;

        // The upward Z window: a kept parapet reaches this far above its edge, so any partner
        // roadway surface within it would be pierced by the wall.
        var aboveWindowMeters = parapetHeightMeters + ParapetCoplanarToleranceMeters;

        var seg = spline!.StructureSegments?.FirstOrDefault(s => s.SpanId == span.SpanId);

        // Same-spline spans are consecutive pieces of one corridor (chain-continuous butt joints),
        // never an overlap partner; everything else is prefiltered by bounds only.
        bool[]? left = null, right = null;
        var bounds = spanBounds[span];
        var partners = network.BridgeSpans
            .Where(other => other.SplineId != span.SplineId &&
                            other.Stations.Count >= 2 &&
                            BoundsOverlap(bounds, spanBounds[other]))
            .ToList();
        if (partners.Count > 0)
        {
            var n = span.Stations.Count;
            left = new bool[n];
            right = new bool[n];
            for (var i = 0; i < n; i++)
            {
                var st = span.Stations[i];
                var normal = SafeNormalize(st.Normal);
                var half = st.Width / 2f;
                var leftPt = st.Center - normal * half;
                var rightPt = st.Center + normal * half;
                foreach (var other in partners)
                {
                    var otherBounds = spanBounds[other];
                    left[i] = left[i] ||
                        IsOnDeckSurface(other, otherBounds, leftPt, st.LeftEdgeZ, aboveWindowMeters);
                    right[i] = right[i] ||
                        IsOnDeckSurface(other, otherBounds, rightPt, st.RightEdgeZ, aboveWindowMeters);
                    if (left[i] && right[i])
                        break;
                }
            }

            if (!left.Any(x => x))
                left = null;
            if (!right.Any(x => x))
                right = null;
        }

        var suppressStart = seg?.StartContinuesOntoDeck == true;
        var suppressEnd = seg?.EndContinuesOntoDeck == true;
        if (!suppressStart && !suppressEnd && left == null && right == null)
            return null;

        return new BridgeDeckTrim
        {
            SuppressStartStamp = suppressStart,
            SuppressEndStamp = suppressEnd,
            LeftParapetSuppressed = left,
            RightParapetSuppressed = right,
        };
    }

    /// <summary>Plan-view AABB of a span's deck footprint (station centers inflated by the widest
    /// half-width) — the cheap prefilter for the all-pairs overlap scan.</summary>
    private static (Vector2 Min, Vector2 Max) ComputeSpanBounds(BridgeSpanSnapshot span)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        var maxHalf = 0f;
        foreach (var st in span.Stations)
        {
            min = Vector2.Min(min, st.Center);
            max = Vector2.Max(max, st.Center);
            maxHalf = MathF.Max(maxHalf, st.Width / 2f);
        }

        var inflate = new Vector2(maxHalf + 0.1f);
        return (min - inflate, max + inflate);
    }

    private static bool BoundsOverlap((Vector2 Min, Vector2 Max) a, (Vector2 Min, Vector2 Max) b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;

    /// <summary>
    /// Doc 15 (b): whether a point (with its surface elevation) lies ON — or its parapet would
    /// PIERCE — a span's deck surface: strictly inside the plan footprint (nearest-segment distance
    /// under the local half-width minus <see cref="ParapetInsideEpsilonMeters"/>, projections
    /// clamped past the deck ends rejected) AND vertically within the asymmetric Z window against
    /// the deck surface interpolated at the projection (station-lerped center Z + lateral lerp
    /// toward the banked edge Z): partner surface at most
    /// <see cref="ParapetCoplanarToleranceMeters"/> BELOW the edge (coplanar merge), or at most
    /// <paramref name="aboveWindowMeters"/> (parapet height + tolerance) ABOVE it (the merge
    /// transition zone, where a kept wall pierces the upper roadway). The Z test is what keeps full
    /// parapets on stacked decks whose plan footprints merely cross — genuine crossings clear the
    /// window by design (road clearance ≫ parapet height).
    /// </summary>
    private static bool IsOnDeckSurface(
        BridgeSpanSnapshot span, (Vector2 Min, Vector2 Max) bounds, Vector2 point, float pointZ,
        float aboveWindowMeters)
    {
        if (point.X < bounds.Min.X || point.X > bounds.Max.X ||
            point.Y < bounds.Min.Y || point.Y > bounds.Max.Y)
            return false;

        if (!TryProjectOntoSpan(span, point, out var surfaceZ))
            return false;

        // dz > 0: edge above the partner surface (we are the upper deck — keep the wall beyond the
        // coplanar tolerance). dz < 0: partner surface above the edge — suppress while the wall
        // would still reach into/through that roadway.
        var dz = pointZ - surfaceZ;
        return dz <= ParapetCoplanarToleranceMeters && -dz <= aboveWindowMeters;
    }

    /// <summary>
    /// Projects a plan point onto a span's deck footprint: true when it lies strictly inside
    /// (nearest-segment distance under the local half-width minus
    /// <see cref="ParapetInsideEpsilonMeters"/>, projections clamped past the deck ends rejected),
    /// with the deck surface Z interpolated at the projection (station-lerped center Z + lateral
    /// lerp toward the banked edge Z). Shared by the parapet-suppression Z test and the butt-seam
    /// extension's partner-surface conformance.
    /// </summary>
    private static bool TryProjectOntoSpan(BridgeSpanSnapshot span, Vector2 point, out float surfaceZ)
    {
        surfaceZ = float.NaN;
        var stations = span.Stations;
        var bestDistSq = float.MaxValue;
        BridgeStation? bestA = null, bestB = null;
        var bestT = 0f;
        var clampedAtExtremity = false;
        for (var i = 0; i < stations.Count - 1; i++)
        {
            var a = stations[i];
            var b = stations[i + 1];
            var ab = b.Center - a.Center;
            var lenSq = ab.LengthSquared();
            var t = lenSq > 1e-8f ? Math.Clamp(Vector2.Dot(point - a.Center, ab) / lenSq, 0f, 1f) : 0f;
            var distSq = Vector2.DistanceSquared(point, a.Center + ab * t);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestA = a;
                bestB = b;
                bestT = t;
                clampedAtExtremity = (i == 0 && t <= 0f) || (i == stations.Count - 2 && t >= 1f);
            }
        }

        if (bestA == null || bestB == null || clampedAtExtremity)
            return false;

        var half = (bestT < 0.5f ? bestA.Width : bestB.Width) / 2f;
        var inset = half - ParapetInsideEpsilonMeters;
        if (inset <= 0f || bestDistSq > inset * inset)
            return false;

        var center = Vector2.Lerp(bestA.Center, bestB.Center, bestT);
        var near = bestT < 0.5f ? bestA : bestB;
        var offset = Vector2.Dot(point - center, SafeNormalize(near.Normal));
        var centerZ = bestA.CenterZ + (bestB.CenterZ - bestA.CenterZ) * bestT;
        var edgeZ = offset >= 0f
            ? bestA.RightEdgeZ + (bestB.RightEdgeZ - bestA.RightEdgeZ) * bestT
            : bestA.LeftEdgeZ + (bestB.LeftEdgeZ - bestA.LeftEdgeZ) * bestT;
        var frac = half > 1e-3f ? Math.Clamp(MathF.Abs(offset) / half, 0f, 1f) : 0f;
        surfaceZ = centerZ + (edgeZ - centerZ) * frac;
        return true;
    }

    private static Vector2 SafeNormalize(Vector2 v)
    {
        var lenSq = v.LengthSquared();
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : Vector2.Zero;
    }
}

/// <summary>
/// Result of a single exported bridge deck.
/// </summary>
public class BridgeDeckExportItem
{
    /// <summary>Owning spline id (used for the file name and scene-object name).</summary>
    public int SplineId { get; init; }

    /// <summary>The written DAE file name (e.g. <c>bridge_42.dae</c>), relative to the shapes directory.</summary>
    public required string DaeFileName { get; init; }

    /// <summary>Absolute path the DAE was written to.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Total vertex count of the DAE's visual meshes (deck + piers when enabled).</summary>
    public int Vertices { get; init; }

    /// <summary>Total triangle count of the DAE's visual meshes (deck + piers when enabled).</summary>
    public int Triangles { get; init; }

    /// <summary>Number of doc-19 pier supports built into this DAE (0 when the flag is off).</summary>
    public int Piers { get; init; }
}

/// <summary>
/// Aggregate result of a bridge-deck export run.
/// </summary>
public class BridgeDeckExportResult
{
    /// <summary>Whether the run completed without throwing. Per-bridge skips are reported in <see cref="Warnings"/>.</summary>
    public bool Success { get; set; }

    /// <summary>Directory the DAE files were written to.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>One entry per successfully exported deck.</summary>
    public List<BridgeDeckExportItem> Decks { get; } = [];

    /// <summary>Number of bridge splines that were skipped (no usable deck geometry).</summary>
    public int BridgesSkipped { get; set; }

    /// <summary>Diagnostics for skipped bridges and other non-fatal issues.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Total vertices across all exported decks.</summary>
    public int TotalVertices => Decks.Sum(d => d.Vertices);

    /// <summary>Total triangles across all exported decks.</summary>
    public int TotalTriangles => Decks.Sum(d => d.Triangles);

    /// <summary>Total doc-19 pier supports across all exported decks.</summary>
    public int TotalPiers => Decks.Sum(d => d.Piers);

    public override string ToString()
    {
        return $"Exported {Decks.Count} bridge deck(s) ({TotalVertices:N0} verts, {TotalTriangles:N0} tris), " +
               $"{BridgesSkipped} skipped, to {OutputDirectory}";
    }
}
