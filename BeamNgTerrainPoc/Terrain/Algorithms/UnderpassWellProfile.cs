using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Doc 28 Step C — the smooth lower-envelope profile of a coherent underpass: ONE continuous well spanning
/// a cluster's first→last crossing on the lower road, instead of independent per-crossing wells that fight
/// each other (the winningen road-164 washboard).
///
/// <para><b>Absolute-Z interior (winningen render 2026-07-02 #2):</b> the interior is an ENGINEERED curve
/// through the per-crossing clearance targets (smoothstep between adjacent targets — zero slope at every
/// crossing station), NOT a depth offset below the natural profile. The base elevation chain the dip pins
/// ride (A0 smoothed centerline DEM) carries real-world artifacts exactly under interchanges (bridge
/// shadows, embankment noise), and a depth-offset well reproduced every wiggle 1:1 in the HARD-pinned well
/// bottom where the smoother is forbidden to fix it — the "kink mid-underpass" render. Inside the well the
/// natural profile is only consulted as a never-raise clamp; it fades back in over the eased end ramps,
/// which BLEND from the end target to the natural grade (<c>w·targetZ + (1−w)·naturalZ</c>).</para>
///
/// <para><b>Depth-space use (road-272 ramp-end humps, 2026-07-21):</b> the sparse-mode emitter
/// (<c>UnifiedRoadSmoother.PinUnderpassClusterWell</c>) evaluates the same math with
/// <c>TargetZ = −depth</c> and <c>naturalZ = 0</c>, turning <c>−ZAt(s, 0)</c> into a smooth relative DROP
/// profile (engineered interior through the per-crossing depths, eased to zero on the ramps, coincident
/// stations keep the deepest). The absolute-Z form above remains the resolver's post-solve active path.</para>
/// </summary>
internal sealed class UnderpassWellProfile
{
    private readonly IReadOnlyList<(float Station, float TargetZ)> _points;
    private readonly float _rampBack;
    private readonly float _rampFwd;

    /// <param name="points">Per-crossing (station on the lower road, absolute target Z the road must reach
    /// under that deck) — MUST be station-sorted.</param>
    /// <param name="rampBackMeters">Eased exit-ramp length before the first crossing (m).</param>
    /// <param name="rampFwdMeters">Eased exit-ramp length after the last crossing (m).</param>
    public UnderpassWellProfile(
        IReadOnlyList<(float Station, float TargetZ)> points, float rampBackMeters, float rampFwdMeters)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) throw new ArgumentException("at least one crossing point required", nameof(points));

        // Coincident stations keep the DEEPEST target: two decks crossing within one cross-section
        // spacing snap to the same lower-road section, and first-sorted-wins would under-dip the deck that
        // needs the deeper clearance (station-sort order is unspecified for equal keys).
        var merged = new List<(float Station, float TargetZ)>(points.Count);
        foreach (var p in points)
        {
            if (merged.Count > 0 && p.Station - merged[^1].Station <= 1e-3f)
                merged[^1] = (merged[^1].Station, MathF.Min(merged[^1].TargetZ, p.TargetZ));
            else
                merged.Add(p);
        }

        _points = merged;
        _rampBack = MathF.Max(0f, rampBackMeters);
        _rampFwd = MathF.Max(0f, rampFwdMeters);
    }

    /// <summary>First crossing's station (the interior well starts here).</summary>
    public float StartStation => _points[0].Station;

    /// <summary>Last crossing's station (the interior well ends here).</summary>
    public float EndStation => _points[^1].Station;

    /// <summary>Station where the back exit ramp reaches natural grade.</summary>
    public float RangeStart => StartStation - _rampBack;

    /// <summary>Station where the forward exit ramp reaches natural grade.</summary>
    public float RangeEnd => EndStation + _rampFwd;

    /// <summary>
    /// The well's absolute road Z at <paramref name="station"/>, given the road's natural (un-dipped)
    /// elevation there. Interior: smoothstep through the crossing targets — independent of
    /// <paramref name="naturalZ"/> except as a never-raise clamp, so base-estimate wiggles cannot enter
    /// the well bottom. End ramps: eased blend from the end target back to the natural profile. Outside
    /// the well the natural elevation is returned unchanged.
    /// </summary>
    public float ZAt(float station, float naturalZ)
    {
        float z;
        if (station <= StartStation)
        {
            if (_rampBack <= 1e-3f)
                z = station >= StartStation - 1e-3f ? _points[0].TargetZ : naturalZ;
            else
            {
                var w = EasedWellWeight((StartStation - station) / _rampBack);
                z = w * _points[0].TargetZ + (1f - w) * naturalZ;
            }
        }
        else if (station >= EndStation)
        {
            if (_rampFwd <= 1e-3f)
                z = station <= EndStation + 1e-3f ? _points[^1].TargetZ : naturalZ;
            else
            {
                var w = EasedWellWeight((station - EndStation) / _rampFwd);
                z = w * _points[^1].TargetZ + (1f - w) * naturalZ;
            }
        }
        else
        {
            z = InteriorZ(station);
        }

        return MathF.Min(naturalZ, z); // never raise the road above its natural profile
    }

    private float InteriorZ(float station)
    {
        for (var i = 1; i < _points.Count; i++)
        {
            if (station > _points[i].Station)
                continue;
            var (s0, z0) = _points[i - 1];
            var (s1, z1) = _points[i];
            var t = s1 - s0 <= 1e-3f ? 0f : (station - s0) / (s1 - s0);
            // Smoothstep between adjacent crossing targets: zero Z-slope at every crossing point, matching
            // the end ramps' w'(0)=0 — no grade break at the deck stations, no DEM noise in between.
            var w = t * t * (3f - 2f * t);
            return z0 + (z1 - z0) * w;
        }

        return _points[^1].TargetZ; // not reachable — station < EndStation always brackets
    }

    /// <summary>The standard dip-well easing weight: 1 at u=0, 0 with zero slope at u≥1 (no kink).</summary>
    public static float EasedWellWeight(float u) =>
        u >= 1f ? 0f : u <= 0f ? 1f : (1f - u) * (1f - u) * (1f + 2f * u);

    /// <summary>
    /// Depth-aware exit-ramp length (winningen render 2026-07-02): a 6 m well recovering over the flat
    /// 60 m default reads as a ~15 % peak-grade V-sag right under the last deck. The ramp is sized so the
    /// recovery grade respects the lower road's §3.3 class slope
    /// (<see cref="BridgeRuleSystemOptions.NormalMaxSlopePercent"/> — primary ⇒ 5 % ⇒ 120 m for 6 m),
    /// never shorter than <paramref name="minimumMeters"/> (the classic dip-ramp default). Callers still
    /// clamp the result to the available room (junctions / way ends / structure spans).
    /// </summary>
    public static float ClassRampLengthMeters(float depthMeters, string? lowerOsmClass, float minimumMeters)
    {
        var slope = BridgeRuleSystemOptions.NormalMaxSlopePercent(
            BridgeRuleSystemOptions.ClassStepFor(lowerOsmClass)) / 100f;
        var forGrade = slope > 1e-4f ? MathF.Max(0f, depthMeters) / slope : minimumMeters;
        return MathF.Max(minimumMeters, forGrade);
    }
}
