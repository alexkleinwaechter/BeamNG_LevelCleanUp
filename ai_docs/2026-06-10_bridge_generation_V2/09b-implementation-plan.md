# In-Solver Natural-Profile Anchor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the bridge machinery from lifting non-bridge roads above their natural elevation, so flat maps (Manhattan) no longer overflow the terrain height ceiling into spike fields — by fixing it *inside* the solve and retiring the post-solve raise pass.

**Architecture:** Doc 09. A0 (`network.EarlyElevationEstimate`) is the reference for a road's natural elevation. A junction is "elevation-inflated" when its solved Z exceeds its A0 by a threshold; at such a junction the affine junction leveling **decays** its correction over a class-slope run (climb to junction, return to natural) instead of tilting the whole road. This generalizes the doc-08 §7c bridge-raised-junction decay to *all* inflated junctions. Once lower roads hold their A0, deck clearance holds by construction, so `GradeSeparationResolver.ApplyApproachRaiseRamps` (the feedback-loop amplifier) is skipped on the new path and replaced by a read-only clearance warning.

**Tech Stack:** C# / .NET 9, `BeamNgTerrainPoc` library, xUnit tests in `BeamNgTerrainPoc.Tests`. Windows-only build (`dotnet build /p:EnableWindowsTargeting=true` for analysis; the app may hold DLL locks while running — MSB3027 lock errors are not compile errors).

**Flag:** everything is gated on a new `BridgeRuleSystemOptions.EnableNaturalProfileAnchor` (default off ⇒ byte-identical legacy), so a Manhattan A/B regen is directly comparable, per doc 08's method.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs` | bridge rule flags | **Modify** — add `EnableNaturalProfileAnchor` + `AnyEnabled` |
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | the solve; affine leveling + decay gate | **Modify** — A0-inflation helpers + wire the two decay-gate sites |
| `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` | post-solve orchestration | **Modify** — skip `ApplyApproachRaiseRamps` when anchor on; add clearance assertion call |
| `BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs` | grade-sep passes | **Modify** — add read-only `AssertCrossingClearances` (C5) |
| `BeamNgTerrainPoc.Tests/Elevation/NaturalProfileAnchorTests.cs` | new tests for the inflation gate | **Create** |

Phases 1–3 fix Manhattan and are the critical path. Phase 4 (RefineSpans fold) is architectural hygiene done **after** validation. Phase 5 (through-road section anchor, doc 09 C3) is **conditional** on the Phase 3 regen still showing humps and ships disabled.

---

## Phase 1 — A0-inflation decay gate (the primary fix)

### Task 1: Add the `EnableNaturalProfileAnchor` flag

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs:92` (after `EnableBridgeStationReprojection`) and `:95-99` (`AnyEnabled`)
- Test: `BeamNgTerrainPoc.Tests/Elevation/BridgeRuleSystemOptionsTests.cs`

- [ ] **Step 1: Write the failing test** — add to `BridgeRuleSystemOptionsTests.cs` (a `[Fact]` next to the existing `AnyEnabled` asserts around line 25):

```csharp
[Fact]
public void NaturalProfileAnchor_CountsTowardAnyEnabled()
{
    Assert.True(new BridgeRuleSystemOptions { EnableNaturalProfileAnchor = true }.AnyEnabled);
    Assert.False(new BridgeRuleSystemOptions().AnyEnabled);
}
```

- [ ] **Step 2: Run it, verify it fails to compile** (`EnableNaturalProfileAnchor` does not exist)

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~NaturalProfileAnchor_CountsTowardAnyEnabled"`
Expected: build error CS0117 `'BridgeRuleSystemOptions' does not contain a definition for 'EnableNaturalProfileAnchor'`.

- [ ] **Step 3: Add the property** in `BridgeRuleSystemOptions.cs` immediately after `EnableBridgeStationReprojection` (line 92):

```csharp
    /// <summary>
    ///     Doc 09: in-solver natural-profile anchor. Non-bridge roads hold their A0 (early-elevation)
    ///     profile; the affine junction leveling decays its correction over a class-slope run at ANY
    ///     junction inflated above A0 (not only BridgeRaisedJunctions), so bridge elevation cannot
    ///     diffuse into side roads. Also skips the post-solve ApplyApproachRaiseRamps (the deficit it
    ///     corrected no longer exists) in favour of a read-only clearance assertion. Off ⇒ legacy.
    /// </summary>
    public bool EnableNaturalProfileAnchor { get; set; }
```

- [ ] **Step 4: Add it to `AnyEnabled`** — append to the chain at line 99:

```csharp
        EnableSpanSolveOrder || EnableGradedDeck || EnableSparseDeckConstraints ||
        EnableNaturalProfileAnchor;
```

- [ ] **Step 5: Run test, verify pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~NaturalProfileAnchor_CountsTowardAnyEnabled"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs BeamNgTerrainPoc.Tests/Elevation/BridgeRuleSystemOptionsTests.cs
git commit -m "feat(bridge): doc 09 - add EnableNaturalProfileAnchor flag"
```

---

### Task 2: A0-inflation helpers (pure, unit-tested)

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` — add two `internal static` methods next to the affine-decay helpers (near `AffineDecayRunMeters`, ~line 2907)
- Test: `BeamNgTerrainPoc.Tests/Elevation/NaturalProfileAnchorTests.cs` (new)

The A0 estimate is `network.EarlyElevationEstimate` — a `Dictionary<int, float>?` keyed by `UnifiedCrossSection.Index`. `AffineDecayMinErrorMeters = 1.5f` already exists (line 2890).

- [ ] **Step 1: Write the failing test** — create `NaturalProfileAnchorTests.cs`:

```csharp
using BeamNgTerrainPoc.Terrain.Services;
using BeamNgTerrainPoc.Tests.Helpers;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Elevation;

public class NaturalProfileAnchorTests
{
    [Fact]
    public void JunctionA0Elevation_AveragesContributorEstimates()
    {
        // Two contributors, A0 estimates 10 and 20 → mean 15.
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(
            a0First: 10f, a0Second: 20f);

        var a0 = UnifiedRoadSmoother.JunctionA0Elevation(network, junction);

        Assert.Equal(15f, a0, 3);
    }

    [Theory]
    [InlineData(15f, false)] // solved Z == A0 mean → not inflated
    [InlineData(16.4f, false)] // +1.4 m < 1.5 m threshold → not inflated
    [InlineData(16.6f, true)] // +1.6 m ≥ 1.5 m threshold → inflated
    [InlineData(69f, true)] // the Manhattan dam magnitude → inflated
    public void IsJunctionElevationInflated_FiresAboveThreshold(float solvedZ, bool expected)
    {
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(
            a0First: 10f, a0Second: 20f); // A0 mean = 15

        Assert.Equal(expected,
            UnifiedRoadSmoother.IsJunctionElevationInflated(network, junction, solvedZ));
    }

    [Fact]
    public void IsJunctionElevationInflated_FalseWhenNoEstimate()
    {
        var (network, junction) = NaturalProfileAnchorScenario.TwoContributorJunction(10f, 20f);
        network.EarlyElevationEstimate = null;

        Assert.False(UnifiedRoadSmoother.IsJunctionElevationInflated(network, junction, 99f));
    }
}
```

- [ ] **Step 2: Write the scenario helper** — create `BeamNgTerrainPoc.Tests/Helpers/NaturalProfileAnchorScenario.cs`. (Uses the existing `RoadNetworkTestHelpers`; if a junction-with-contributors builder already exists there, prefer it and delete this file — check first.)

```csharp
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using Xunit;

namespace BeamNgTerrainPoc.Tests.Helpers;

/// <summary>Minimal network: one junction with two endpoint contributors and a populated A0 estimate.</summary>
internal static class NaturalProfileAnchorScenario
{
    public static (UnifiedRoadNetwork network, NetworkJunction junction) TwoContributorJunction(
        float a0First, float a0Second)
    {
        // Two short splines meeting at (100,100). RoadNetworkTestHelpers is the established builder.
        var a = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(0, 100), new(100, 100), priority: 8000);
        var b = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 100), new(200, 100), priority: 8000);
        var network = RoadNetworkTestHelpers.BuildNetwork(a, b); // detects the shared-endpoint junction

        var junction = Assert.Single(network.Junctions);

        // A0 estimate: tag each junction contributor's cross-section index with the requested value.
        network.EarlyElevationEstimate = new System.Collections.Generic.Dictionary<int, float>();
        var contributors = System.Linq.Enumerable.ToList(junction.Contributors);
        network.EarlyElevationEstimate[contributors[0].CrossSection.Index] = a0First;
        network.EarlyElevationEstimate[contributors[1].CrossSection.Index] = a0Second;

        return (network, junction);
    }
}
```

> NOTE: verify `RoadNetworkTestHelpers.BuildNetwork(...)` exists and produces a junction with ≥2 contributors; other bridge tests (`BridgeDipAsPinTests`, `BridgeCoherentUnderpassTests`) use these helpers — copy their exact builder call if the signature differs. If the two contributors are not both endpoints, adjust the scenario to a real T-junction the helper supports.

- [ ] **Step 3: Run test, verify it fails** (helpers missing)

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~NaturalProfileAnchorTests"`
Expected: build error — `JunctionA0Elevation` / `IsJunctionElevationInflated` not defined.

- [ ] **Step 4: Implement the helpers** in `UnifiedRoadSmoother.cs` after `AffineDecayRunMeters` (line 2907):

```csharp
    /// <summary>
    ///     Doc 09 C1: the junction's natural (A0) elevation = mean of its contributors' early-elevation
    ///     estimates at the junction. NaN when the estimate is unavailable or no contributor is covered.
    /// </summary>
    internal static float JunctionA0Elevation(UnifiedRoadNetwork network, NetworkJunction junction)
    {
        var est = network.EarlyElevationEstimate;
        if (est == null) return float.NaN;

        var sum = 0f;
        var count = 0;
        foreach (var c in junction.Contributors)
            if (est.TryGetValue(c.CrossSection.Index, out var z) && !float.IsNaN(z))
            {
                sum += z;
                count++;
            }

        return count > 0 ? sum / count : float.NaN;
    }

    /// <summary>
    ///     Doc 09 C1/C2: a junction is elevation-inflated when its solved Z sits ≥
    ///     <see cref="AffineDecayMinErrorMeters"/> above its A0 natural elevation — i.e. bridge elevation
    ///     has been transplanted onto it. Such junctions get the class-slope affine decay (C2) even when
    ///     they are not in <see cref="UnifiedRoadNetwork.BridgeRaisedJunctions"/> (the gap that let a
    ///     side road's endpoint tilt full-length). NaN A0 ⇒ false (never decay on missing data).
    /// </summary>
    internal static bool IsJunctionElevationInflated(
        UnifiedRoadNetwork network, NetworkJunction junction, float junctionZ)
    {
        if (float.IsNaN(junctionZ)) return false;
        var a0 = JunctionA0Elevation(network, junction);
        return !float.IsNaN(a0) && junctionZ - a0 >= AffineDecayMinErrorMeters;
    }
```

- [ ] **Step 5: Run test, verify pass**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~NaturalProfileAnchorTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs BeamNgTerrainPoc.Tests/Elevation/NaturalProfileAnchorTests.cs BeamNgTerrainPoc.Tests/Helpers/NaturalProfileAnchorScenario.cs
git commit -m "feat(bridge): doc 09 - A0 junction-inflation helpers"
```

---

### Task 3: Wire the inflation gate into both affine-decay sites

The decay currently fires only for `network.BridgeRaisedJunctions.Contains(junction)` at two places. Gate the *additional* inflation trigger behind the flag so flag-off is byte-identical.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs:2430` (`BuildEndpointTargetLookup`) and `:2569` (`RetargetTerminatingRoadsToSettledThrough`)
- Test: `NaturalProfileAnchorTests.cs`

- [ ] **Step 1: Write the failing integration test** — add to `NaturalProfileAnchorTests.cs`. It asserts that with the flag on, a side road terminating at an inflated junction NOT in `BridgeRaisedJunctions` sheds its lift over a bounded run (returns near A0 far from the junction) instead of the full-length tilt:

```csharp
[Fact]
public void AffineLeveling_DecaysAtInflatedJunction_NotInBridgeRaisedSet()
{
    // A0 flat at 0; junction target +30 m (dam). Side road 300 m long. Flag ON.
    var scenario = NaturalProfileAnchorScenario.SideRoadAtInflatedJunction(
        junctionTargetZ: 30f, sideRoadLengthMeters: 300f, enableAnchor: true);

    UnifiedRoadSmoother.RunAffineLevelingForTest(scenario.Network); // thin test seam over the two sites

    // Near the junction: climbs to ~30. Far end (beyond the class-slope run): back to ~A0 (0), NOT +30.
    Assert.True(scenario.SideRoadEndpointZ() > 25f, "endpoint should meet the junction");
    Assert.True(scenario.SideRoadFarZ() < 5f,
        $"far end should return to natural profile, was {scenario.SideRoadFarZ()}");
}

[Fact]
public void AffineLeveling_FlagOff_TiltsFullLength_ByteIdenticalLegacy()
{
    var scenario = NaturalProfileAnchorScenario.SideRoadAtInflatedJunction(
        junctionTargetZ: 30f, sideRoadLengthMeters: 300f, enableAnchor: false);

    UnifiedRoadSmoother.RunAffineLevelingForTest(scenario.Network);

    // Legacy: the +30 endpoint error spreads over the whole road → far end still well above A0.
    Assert.True(scenario.SideRoadFarZ() > 20f,
        $"legacy full-length spread expected, was {scenario.SideRoadFarZ()}");
}
```

> The `SideRoadAtInflatedJunction` builder, `SideRoadEndpointZ()`/`SideRoadFarZ()` accessors, and a `RunAffineLevelingForTest` seam that invokes `BuildEndpointTargetLookup` + `ApplyAffineLeveling` per spline must be added to the scenario/helpers. Model them on the existing `BridgeSideRoadContainmentTests` (which already exercises the decay for `BridgeRaisedJunctions`) — reuse its network builder and just (a) leave the junction OUT of `BridgeRaisedJunctions` and (b) populate `EarlyElevationEstimate` flat at 0. If `BridgeSideRoadContainmentTests` exposes an internal driver, call that instead of adding `RunAffineLevelingForTest`.

- [ ] **Step 2: Run test, verify it fails** (flag has no effect yet — the far end returns to natural even without the wiring? No: without wiring, flag-on also tilts full-length, so `SideRoadFarZ() < 5f` fails).

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~AffineLeveling_DecaysAtInflatedJunction"`
Expected: FAIL — far end ≈ +30 (still tilted), assertion `< 5f` not met.

- [ ] **Step 3: Wire site 1** — `BuildEndpointTargetLookup` (line 2405). Compute the flag once at method top, extend the `decays` line (2430):

```csharp
        var targets = new Dictionary<(int, bool), float>();
        var decayEndpoints = new HashSet<(int, bool)>();
        var anchorEnabled = network.Splines.Any(s =>
            s.Parameters.BridgeRules?.EnableNaturalProfileAnchor == true);
```

Change line 2430 from:
```csharp
            var decays = network.BridgeRaisedJunctions.Contains(junction);
```
to:
```csharp
            var decays = network.BridgeRaisedJunctions.Contains(junction)
                         || (anchorEnabled && IsJunctionElevationInflated(network, junction, target));
```

- [ ] **Step 4: Wire site 2** — `RetargetTerminatingRoadsToSettledThrough` (line 2525). Add the same `anchorEnabled` local once before the `for (var pass …)` loop (line 2541), then change line 2569 from:
```csharp
                var decays = network.BridgeRaisedJunctions.Contains(junction);
```
to:
```csharp
                var decays = network.BridgeRaisedJunctions.Contains(junction)
                             || (anchorEnabled && IsJunctionElevationInflated(network, junction, settled));
```

- [ ] **Step 5: Run the new tests + the full suite, verify pass and no regression**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~NaturalProfileAnchorTests"` → PASS
Run: `dotnet test BeamNgTerrainPoc.Tests` → all green (707+ ; flag-off path unchanged)
Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs BeamNgTerrainPoc.Tests/Elevation/NaturalProfileAnchorTests.cs BeamNgTerrainPoc.Tests/Helpers/NaturalProfileAnchorScenario.cs
git commit -m "feat(bridge): doc 09 C2 - decay affine correction at any A0-inflated junction"
```

---

## Phase 2 — Retire the post-solve raise + assert clearance

### Task 4: Skip `ApplyApproachRaiseRamps` when the anchor is on

With side roads held at A0, the deficits that pass corrected no longer exist; running it would re-introduce the feedback loop against any residual lift. Skip it on the new path (keep it for flag-off maps — full deletion is a later cleanup once the anchor is default-on).

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs:410`

- [ ] **Step 1: Guard the call.** Just before `GradeSeparationResolver.ApplyApproachRaiseRamps(` (line 410), read the flag and skip:

```csharp
                var anchorOn = unifiedResult.Network.Splines.Any(s =>
                    s.Parameters.BridgeRules?.EnableNaturalProfileAnchor == true);

                // Doc 09 C4: the post-solve raise measured clearance against the dam-lifted solved road
                // (the feedback loop). With the natural-profile anchor holding lower roads at A0, deck
                // clearance holds by construction — skip the raise (kept for legacy flag-off maps).
                if (!anchorOn)
                    GradeSeparationResolver.ApplyApproachRaiseRamps(
                        unifiedResult.Network, heightMap2D, parameters.MetersPerPixel,
                        roadSurfaceOwner: roadSurfaceOwner);
```

- [ ] **Step 2: Build, verify it compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj /p:EnableWindowsTargeting=true`
Expected: build succeeds (ignore MSB3027 DLL-lock warnings if the app is running).

- [ ] **Step 3: Run the suite** (the `BridgeApproachRaiseRampTests` exercise the pass directly with the flag off, so they must still pass)

Run: `dotnet test BeamNgTerrainPoc.Tests`
Expected: PASS — no test sets `EnableNaturalProfileAnchor`, so the pass still runs in every existing test.

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/TerrainCreator.cs
git commit -m "feat(bridge): doc 09 C4 - skip post-solve ApplyApproachRaiseRamps under natural-profile anchor"
```

---

### Task 5: Read-only clearance assertion (C5)

Replace the deleted raise with a diagnostic. It measures final clearance and logs a warning if a crossing is short — it must **never** write elevation. A firing warning is a planner-side (pre-solve) escalation, not a post-hoc fix.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs` — add `AssertCrossingClearances` (model its crossing/lowerZ read on `ApplyApproachRaiseRamps`, `:604-641`, but write nothing)
- Modify: `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` — call it where the raise was, when `anchorOn`
- Test: `NaturalProfileAnchorTests.cs`

- [ ] **Step 1: Write the failing test** — a crossing whose deck clears its lower road logs nothing; one that doesn't logs a warning. Use the existing `TerrainCreationLogger` test capture if present (grep `TerrainCreationLogger` in tests); otherwise assert the returned short-crossing count:

```csharp
[Fact]
public void AssertCrossingClearances_CountsOnlyShortCrossings()
{
    // deck at 40, lower road at 16, required 6.7 → clears (deficit < 0): 0 short.
    var clears = NaturalProfileAnchorScenario.CrossingPlan(deckZ: 40f, lowerZ: 16f, required: 6.7f);
    Assert.Equal(0, GradeSeparationResolver.AssertCrossingClearances(clears));

    // deck at 20, lower road at 16, required 6.7 → clearance 4 < 6.7: 1 short.
    var shortC = NaturalProfileAnchorScenario.CrossingPlan(deckZ: 20f, lowerZ: 16f, required: 6.7f);
    Assert.Equal(1, GradeSeparationResolver.AssertCrossingClearances(shortC));
}
```

> `CrossingPlan(...)` builds a `UnifiedRoadNetwork` with a `BridgeElevationPlan` holding one crossing at the given deck/lower Z (reuse the plan builders in `BridgeApproachRaiseRampTests`/`BridgeSparseFloorConstraintTests`).

- [ ] **Step 2: Run it, verify failure** (`AssertCrossingClearances` undefined).

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~AssertCrossingClearances"`
Expected: build error — method not defined.

- [ ] **Step 3: Implement `AssertCrossingClearances`** in `GradeSeparationResolver.cs` (returns the count of short crossings; logs each). It mirrors the read logic of `ApplyApproachRaiseRamps` but writes nothing:

```csharp
    /// <summary>
    ///     Doc 09 C5: read-only clearance check. Logs a [BRIDGE-CLEAR] WARN for every crossing whose
    ///     final deck-vs-lower-road clearance is below the required minimum. Writes NO elevation — a
    ///     firing warning means the pre-solve planner should have dipped / reduced clearance / not
    ///     raised, and is fixed there (in-solve), never with a post-solve raise. Returns the short count.
    /// </summary>
    public static int AssertCrossingClearances(UnifiedRoadNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0) return 0;

        var shortCount = 0;
        foreach (var cp in plan.Crossings)
        {
            if (cp.RequiredSeparationMeters <= 0f || cp.Action == BridgeElevationAction.DipLowerRoad)
                continue;

            var span = plan.Spans.FirstOrDefault(s => s.OwnerSplineId == cp.Crossing.UpperSplineId);
            if (span == null) continue;
            var upper = NearestSection(network, span.OwnerSplineId, cp.Crossing.CrossingXY);
            if (upper == null || !IsFinite(upper.TargetElevation)) continue;

            float lowerZ;
            if (cp.Crossing.HasLowerSpline)
            {
                var lowerSection = NearestSection(network, cp.Crossing.LowerSplineId, cp.Crossing.CrossingXY);
                lowerZ = lowerSection != null && IsFinite(lowerSection.TargetElevation)
                    ? lowerSection.TargetElevation
                    : cp.ObstacleZEstimate;
            }
            else
            {
                lowerZ = IsFinite(cp.LowerRoadTargetZ) ? cp.LowerRoadTargetZ : cp.ObstacleZEstimate;
            }
            if (!IsFinite(lowerZ)) continue;

            var clearance = upper.TargetElevation - lowerZ;
            if (clearance < cp.RequiredSeparationMeters - 0.05f)
            {
                shortCount++;
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[BRIDGE-CLEAR] WARN upper={cp.Crossing.UpperSplineId} lower={cp.Crossing.LowerSplineId} " +
                    $"({cp.Crossing.LowerKind}) clearance={clearance:F2}/{cp.RequiredSeparationMeters:F2}m " +
                    "— planner should dip/reduce/not-raise (doc 09 C5, no post-solve correction)");
            }
        }

        if (shortCount > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-CLEAR] {shortCount} crossing(s) under required clearance after solve");
        return shortCount;
    }
```

> If the test's `CrossingPlan` seam can't easily populate `NearestSection` lookups, keep the assertion logic identical but have the test build a real 2-spline crossing like `BridgeApproachRaiseRampTests` does, so `NearestSection` resolves.

- [ ] **Step 4: Call it from `TerrainCreator`** where the raise was skipped (inside the `anchorOn` path added in Task 4):

```csharp
                if (!anchorOn)
                    GradeSeparationResolver.ApplyApproachRaiseRamps(
                        unifiedResult.Network, heightMap2D, parameters.MetersPerPixel,
                        roadSurfaceOwner: roadSurfaceOwner);
                else
                    GradeSeparationResolver.AssertCrossingClearances(unifiedResult.Network);
```

- [ ] **Step 5: Run tests, verify pass + suite green**

Run: `dotnet test BeamNgTerrainPoc.Tests --filter "FullyQualifiedName~AssertCrossingClearances"` → PASS
Run: `dotnet test BeamNgTerrainPoc.Tests` → all green

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs BeamNgTerrainPoc/Terrain/TerrainCreator.cs BeamNgTerrainPoc.Tests/Elevation/NaturalProfileAnchorTests.cs
git commit -m "feat(bridge): doc 09 C5 - read-only crossing-clearance assertion replaces post-solve raise"
```

---

## Phase 3 — Validation (manual, gated checkpoint)

### Task 6: Manhattan A/B regen + no-regression set

- [ ] **Step 1: Enable the flag in the app's bridge preset** for Manhattan (wherever `BridgeRuleSystemOptions` is populated for a generation run — the same place `EnableSparseDeckConstraints` is set). Rebuild the host app (`BeamNG_LevelCleanUp`).

- [ ] **Step 2: Regen Manhattan 2048 twice** — anchor OFF then ON. Logs land in `…/levels/manhattan/MT_TerrainGeneration/logs/`.

- [ ] **Step 3: Compare and confirm the fix.** Expected with anchor ON:
  - `Warnings`: `Over-max values (>= 41,95…m)` count → ~0 (was 31 811).
  - `[DAM-REPORT]`: splines 44/50/162 deltas → small, `len>3m` short and concentrated at genuine bridgeheads (road 50 `meanAbs` ≪ 28.98).
  - No `[BRIDGE-RAMP] raise` lines (pass skipped); `[BRIDGE-CLEAR]` warnings — if any — name real infeasible crossings, not dam artifacts.
  - `grep [BRIDGE-PLAN] affine-decay` shows decays now firing on the previously-uncontained side roads (road 50's start).

- [ ] **Step 4: In-game render** Manhattan/Brooklyn: spikes and gouges gone; real bridges intact with graded approaches; no roads standing on dams.

- [ ] **Step 5: No-regression regen** — `winningen` (dam excess ≤ doc-08 +683 m or better; **no new class-slope ramps on ordinary steep roads** — confirm the inflation gate does not fire on non-bridge junctions there), `franco_same_prio`, `_generated_terrain`. Compare `[DAM-REPORT]` before/after.

- [ ] **Step 6: Decision gate.** If Manhattan is clean and no regressions → Phase 4/5 are optional hygiene/conditional. If through-road humps remain (a *through* road, not an endpoint case) → do Phase 5. Record findings in doc 09 §7.

---

## Phase 4 — Architectural hygiene (post-validation): fold `RefineSpans` into the solve

Doc 09 C4 decision (2026-07-06): `BridgeProfileSolver.RefineSpans` rewrites deck Z and must run inside the solve so the solver owns deck elevation outright. **Not required to fix Manhattan** — do only after Phase 3 is green.

**Files:** `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` (call `RefineSpans` at the end of `SmoothAllRoads`), `BeamNgTerrainPoc/Terrain/TerrainCreator.cs:388` (remove the call), and the input plumbing (`BuildBridgeDeckProfile`, `PlanFloorConstraints`/`PlanConstraints`).

- [ ] **Step 1:** Trace `RefineSpans`' inputs in `TerrainCreator.cs:368-390`: `bridgeDeckProfile = BuildBridgeDeckProfile(parameters)` and `gradeSepConstraints` (`PlanFloorConstraints`/`PlanConstraints`). Determine which of these `SmoothAllRoads` already has access to (it has `network`, `heightMap`, `metersPerPixel`; it does NOT currently have `parameters`/deck profile).
- [ ] **Step 2:** Decide the seam: either pass the deck-profile + clearance params into `SmoothAllRoads` (extend its signature), or compute them from `network` inside the smoother. Prefer extending the signature with a small `BridgeDeckGeometryInputs` record so the smoother stays decoupled from `TerrainCreationParameters`.
- [ ] **Step 3:** Move the `DiagnoseSeams` → `RoadSurfaceOwnerRaster.Build` → `RefineSpans` block’s **elevation** part (RefineSpans only) to the tail of `SmoothAllRoads`, after the iteration loop and post-loop retarget. Leave the terrain passes (stamper/excavator/dips-carve) in `TerrainCreator`.
- [ ] **Step 4:** Verify read-order: deck mesh export, DecalRoad generation, and `BridgeDeckExcavator` still read the refined network. Run a full regen of a bridge map (`franco_same_prio`) and diff the deck DecalRoad Z against the pre-fold run — must be identical.
- [ ] **Step 5:** `dotnet test BeamNgTerrainPoc.Tests` green; commit.

> If Step 2 proves invasive (deep `parameters` coupling), STOP and reassess with the user per doc 09 §7 — the fallback ("no post-solve *road* changes", keep RefineSpans in place as canonical in-solve deck geometry) is acceptable.

---

## Phase 5 — Conditional: through-road section anchor (doc 09 C3)

Only if Phase 3 Step 6 shows a residual hump on a **through** road (interior lift the endpoint decay can't reach). Ships **disabled** behind the same flag with a secondary guard; see doc 09 §4 C3 for the eased-weight formula and the hard gate (only sections above A0 by the threshold AND attributable to a raised structure). Write it TDD (a through-road with a mid-spline inflated junction holds A0 in its interior beyond the ramp) mirroring Task 3. Defer until data justifies it.

---

## Self-Review

- **Spec coverage:** C1 (Task 2 helpers), C2 (Task 3 gate at both sites), C4 raise-retire (Task 4), C5 assertion (Task 5), validation (Task 6), C4 RefineSpans fold (Phase 4), C3 (Phase 5). Flag (Task 1). All doc-09 components mapped.
- **Placeholder scan:** all code steps contain concrete code; the two test-helper builders (`NaturalProfileAnchorScenario`, `CrossingPlan`) explicitly say to reuse `RoadNetworkTestHelpers` / `BridgeApproachRaiseRampTests` builders and to verify their exact signatures first — the one genuinely codebase-specific unknown, flagged not hidden.
- **Type consistency:** `EnableNaturalProfileAnchor` (bool), `JunctionA0Elevation`/`IsJunctionElevationInflated` (internal static, `UnifiedRoadSmoother`), `AssertCrossingClearances` (public static int, `GradeSeparationResolver`), `EarlyElevationEstimate` (`Dictionary<int,float>?` keyed by `cs.Index`), `BridgeRaisedJunctions` (`HashSet<NetworkJunction>`) — names used consistently across tasks.
- **Risk:** the only non-mechanical step is the Phase-4 `RefineSpans` fold; it is explicitly gated behind validation and has a documented fallback.
