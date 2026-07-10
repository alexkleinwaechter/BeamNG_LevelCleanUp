# Rule-1 Infeasible-Raise → Dip Lower-Priority Under-Roads — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a Rule-1 ("ramped viaduct") bridge span can't build its mandated raise within the available approach length, dip strictly-lower-priority under-roads instead of force-raising the deck.

**Architecture:** A single localized change inside the `if (isRamp)` branch of `BridgeElevationPlanner.PlanSpan`. When the raise is infeasible AND at least one under-road is strictly lower priority, those roads emit `DipLowerRoad` crossings and the deck requirement is recomputed from the remaining (non-dippable) obstacles only. All downstream machinery (`isRaised`/`deckZ`/pin emission, `ReconcileDipAgainstDeck`, `GradeSeparationResolver` dips) already handles un-raised, dip-emitting spans unchanged.

**Tech Stack:** C# / .NET 9, xUnit. Library: `BeamNgTerrainPoc`. Tests: `BeamNgTerrainPoc.Tests`.

## Global Constraints

- Gated on the existing `EnableRampFeasibility` flag — no new flag. When the flag is off, output is byte-identical to today.
- When `dipFallback` is NOT triggered, the recomputed `spanPinZ`/`spanLift` MUST equal the original `requiredDeckZFull`/`liftFull` (byte-identical feasible-viaduct behavior).
- Grade/slope targets stay code constants (no new user parameters). Standing no-grade-clamp rule: warn, don't clamp.
- Build/test on non-Windows CI with `-p:EnableWindowsTargeting=true`.
- Spec: `docs/superpowers/specs/2026-07-01-rule1-infeasible-dip-lower-priority-design.md`.

Build command:
```bash
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
```
Test command (single test):
```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BridgeRampFeasibilityTests.TEST_NAME"
```

---

### Task 1: Dip fallback for infeasible Rule-1 spans (core behavior)

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs` (extend `BuildScenario`, add test)
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs:197-220` (the `if (isRamp)` block)

**Interfaces:**
- Consumes (existing, do not change): `BridgeElevationPlanner.Plan(UnifiedRoadNetwork, options)` → `BridgeElevationPlan` with `IReadOnlyList<CrossingPlan> Crossings` and `IReadOnlyList<SpanDeckPlan> Spans`. `CrossingPlan` fields used: `Action` (`BridgeElevationAction`), `DipDepthMeters`, `LowerRoadTargetZ`, `RequiredSeparationMeters`, `Warning`. `SpanDeckPlan.IsRaised` (bool). `GradeSeparatedCrossing` fields: `HasLowerSpline`, `LowerKind` (`BridgeObstacleKind`), `LowerPriority`, `UpperPriority`, `LowerIsBridge`.
- Produces: the `IsDippable(Obstacle)` local function and `dipFallback` behavior inside `PlanSpan`. No new public signatures.

- [ ] **Step 1: Extend the `BuildScenario` test helper**

In `BridgeRampFeasibilityTests.cs`, add two optional parameters to `BuildScenario` so a test can raise the under-road to the corridor level (forces the Rule-1 `isRamp` classification) and give it a distinct priority. Change the signature and the two spots that use the defaults:

```csharp
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, ParameterizedRoadSpline under)
        BuildScenario(
            string upperClass, string lowerClass, BridgeRuleSystemOptions rules,
            float spanStart = 100, float spanEnd = 200,
            float underElev = 8f, int underPriority = 8002)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart, EndDistance = spanEnd, Type = StructureType.Bridge, Layer = 1,
            OsmWayIds = { 99001L },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), osmRoadType: upperClass, priority: 8002,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = rules;

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), osmRoadType: lowerClass, priority: underPriority);
        under.Layer = 0;
```

Then update the elevation loop lower down (the line that currently sets the under-road cross-sections to `8f`) to use the parameter:

```csharp
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = underElev;
```

(Leave the corridor loop at `10f` unchanged. Existing callers pass neither new arg, so their behavior — under at 8, priority 8002 — is preserved.)

- [ ] **Step 2: Write the failing test**

Add to `BridgeRampFeasibilityTests.cs`:

```csharp
    // ── Rule-1 infeasible → dip lower-priority under-road (spec 2026-07-01) ───────────────────────────────

    [Fact]
    public void Rule1_Infeasible_LowerPriorityRoad_Dips()
    {
        // isRamp fires: under-road at the corridor level (10) ⇒ the deck must climb a full clearance (5) above
        // both approaches. The long span [10,390] leaves ~8 m of approach ⇒ motorway absolute slope 5 % allows
        // only ~0.4 m of raise ⇒ the mandated 5 m raise is INFEASIBLE. The under-road is strictly lower
        // priority (3000 < 8002), so it must be DIPPED instead of raising the motorway.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(5f, crossing.DipDepthMeters, Tol);       // deficit = ob.Z 10 + sep 5 − approach 10
        Assert.Equal(5f, crossing.LowerRoadTargetZ, Tol);     // deckRef 10 − sep 5
        Assert.False(Assert.Single(plan.Spans).IsRaised);     // the motorway deck stays at approach level
    }
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Infeasible_LowerPriorityRoad_Dips"
```
Expected: FAIL — `Action` is `RaiseBridge` (not `DipLowerRoad`) and `IsRaised` is `true`. This confirms the scenario reproduces the Rule-1 force-raise bug. (If you instead see `Split` or `RaiseBridgeVeto`, the scenario did NOT classify as Rule-1 — verify `underElev: 10f` so the required raise reaches a full clearance above the approaches.)

- [ ] **Step 4: Implement the dip fallback**

Replace the entire `if (isRamp) { … }` block in `BridgeElevationPlanner.cs` (currently lines ~197-220) with:

```csharp
        if (isRamp)
        {
            // Rule 1 — a ramped viaduct: the deck normally raises to clear everything and leaves every road
            // under it alone. EXCEPTION (spec 2026-07-01): when that raise can't be ramped in the available
            // approach length (feasibility warning) AND an under-road is strictly lower priority, dip those
            // roads instead of forcing the motorway up. The deck then rises only for the obstacles that still
            // demand it (rail/water/equal-or-higher priority/bridge-under/terrain).
            var raiseAboveApproaches = graded
                ? liftFull
                : requiredDeckZFull - MathF.Min(approachLeft, approachRight);
            var infeasible = feasibility && approachesBothSides && raiseAboveApproaches > raiseMaxAbs + Eps;

            // A strictly-lower-priority under-ROAD that may be dipped (mirrors the rail/water/bridge veto).
            bool IsDippable(Obstacle o) =>
                o.Crossing.HasLowerSpline &&
                o.Crossing.LowerKind == BridgeObstacleKind.Road &&
                o.Crossing.LowerPriority < o.Crossing.UpperPriority &&
                !(rules?.EnableSpanSolveOrder == true && o.Crossing.LowerIsBridge);

            var dipFallback = infeasible && obstacles.Any(IsDippable);

            // Recompute the deck requirement from the obstacles that STILL force a raise. When dipFallback is
            // false this reproduces requiredDeckZFull/liftFull exactly (byte-identical feasible viaduct).
            spanPinZ = float.NegativeInfinity;
            spanLift = float.NegativeInfinity;
            if (IsFinite(terrainMaxZ))
            {
                spanPinZ = MathF.Max(spanPinZ, terrainMaxZ + c);
                spanLift = MathF.Max(spanLift, terrainMaxZ + c - MathF.Min(chordStart, chordEnd));
            }
            foreach (var ob in obstacles)
            {
                if (dipFallback && IsDippable(ob)) continue; // dipped ⇒ no longer raises the deck
                var required = ob.Z + SeparationFor(ob);
                spanPinZ = MathF.Max(spanPinZ, required);
                spanLift = MathF.Max(spanLift, required - DeckRefAt(ob));
            }

            // Warn only if the (possibly reduced) raise is still over-steep.
            var reducedRaise = graded
                ? spanLift
                : IsFinite(spanPinZ) ? spanPinZ - MathF.Min(approachLeft, approachRight) : float.NegativeInfinity;
            var rampWarning = feasibility && approachesBothSides && reducedRaise > raiseMaxAbs + Eps
                ? "Rule-1 raise exceeds absolute ramp slope for the approach length"
                : null;

            foreach (var ob in obstacles)
            {
                if (dipFallback && IsDippable(ob))
                {
                    var deckRef = DeckRefAt(ob);
                    var sep = SeparationFor(ob);
                    spanCrossings.Add(new CrossingPlan
                    {
                        Crossing = ob.Crossing,
                        ObstacleZEstimate = ob.Z,
                        Action = BridgeElevationAction.DipLowerRoad,
                        LowerRoadTargetZ = deckRef - sep,
                        DipDepthMeters = ob.Z + sep - deckRef,
                        RequiredSeparationMeters = sep,
                    });
                }
                else
                {
                    spanCrossings.Add(new CrossingPlan
                    {
                        Crossing = ob.Crossing,
                        ObstacleZEstimate = ob.Z,
                        Action = BridgeElevationAction.RaiseBridge,
                        DeckTargetZ = graded ? DeckRefAt(ob) + spanLift : spanPinZ,
                        RequiredSeparationMeters = SeparationFor(ob),
                        Warning = rampWarning,
                    });
                }
            }
        }
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Infeasible_LowerPriorityRoad_Dips"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs
git commit -m "feat(bridge): infeasible Rule-1 raise dips lower-priority under-roads

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Boundary guard tests (equal-priority, feasible, flag-off)

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs`

**Interfaces:**
- Consumes: everything from Task 1. No new production code — these lock the trigger boundaries so the fallback can't over-reach.

- [ ] **Step 1: Add the three guard tests**

```csharp
    [Fact]
    public void Rule1_Infeasible_EqualPriorityRoad_StillRaises()
    {
        // Same infeasible Rule-1 geometry, but the under-road is EQUAL priority (8002 == 8002) ⇒ NOT dippable
        // ⇒ the deck still raises and warns, exactly as before the fallback existed.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "motorway", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 8002);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.NotNull(crossing.Warning);
        Assert.Contains("ramp slope", crossing.Warning);
    }

    [Fact]
    public void Rule1_Feasible_LowerPriorityRoad_StillRaises()
    {
        // isRamp fires (under at corridor level) but the span [140,160] leaves ~138 m of approach ⇒ motorway
        // absolute slope 5 % allows ~6.9 m of raise ≥ the 5 m mandate ⇒ FEASIBLE. Only *infeasible* Rule-1
        // spans dip, so a lower-priority under-road here is still cleared by raising the deck.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 140, spanEnd: 160, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Null(crossing.Warning);                    // feasible ⇒ no over-steep warning
        Assert.True(Assert.Single(plan.Spans).IsRaised);
    }

    [Fact]
    public void Rule1_Infeasible_FeasibilityFlagOff_StillRaises()
    {
        // EnableRampFeasibility OFF ⇒ no feasibility test is computed ⇒ dipFallback can never trigger ⇒
        // byte-identical old behavior: full Rule-1 raise, no warning.
        var rules = new BridgeRuleSystemOptions();
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Null(crossing.Warning);
    }
```

- [ ] **Step 2: Run the three tests to verify they pass**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Infeasible_EqualPriorityRoad_StillRaises|FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Feasible_LowerPriorityRoad_StillRaises|FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Infeasible_FeasibilityFlagOff_StillRaises"
```
Expected: PASS (3 passed). If `Rule1_Feasible_…` fails with a warning present / `Action != RaiseBridge`, the span is being seen as infeasible — nudge `spanEnd` down (e.g. `150`) to lengthen the shorter approach.

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs
git commit -m "test(bridge): guard Rule-1 dip-fallback trigger boundaries

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Mixed-obstacle test (dip the road, raise for the peer)

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs`

**Interfaces:**
- Consumes: everything from Task 1. Verifies the partition: within one infeasible Rule-1 span, a strictly-lower-priority road dips while an equal-priority peer keeps the deck raised.

- [ ] **Step 1: Add the mixed-obstacle test**

This test adds a SECOND under-road crossing the same span at a different station: one strictly-lower-priority (dips), one equal-priority (raises). Add to `BridgeRampFeasibilityTests.cs`:

```csharp
    [Fact]
    public void Rule1_Infeasible_MixedObstacles_RaisesForPeer_DipsLowerRoad()
    {
        // Infeasible Rule-1 span crossing TWO under-roads: one equal priority (non-dippable ⇒ raises the deck)
        // and one strictly lower priority (dippable ⇒ dips). The deck must end up raised (for the peer) and the
        // lower-priority road must be dipped.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, corridor, _) = BuildScenario(
            "motorway", "motorway", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 8002); // peer: equal priority

        // Second under-road, strictly lower priority, crossing the corridor at x=300 (inside the span).
        var lower = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(300, 100), new(300, 200), osmRoadType: "residential", priority: 3000);
        lower.Layer = 0;
        var network2 = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, lower);
        foreach (var cs in network2.GetCrossSectionsForSpline(lower.SplineId))
            cs.TargetElevation = 10f;
        // Merge the second under-road's crossings into the original network.
        foreach (var c in network2.GradeSeparatedCrossings)
            if (!network.GradeSeparatedCrossings.Contains(c))
                network.GradeSeparatedCrossings.Add(c);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.True(Assert.Single(plan.Spans).IsRaised); // raised for the equal-priority peer
        Assert.Contains(plan.Crossings, c => c.Action == BridgeElevationAction.DipLowerRoad);
        Assert.Contains(plan.Crossings, c => c.Action == BridgeElevationAction.RaiseBridge);
    }
```

- [ ] **Step 2: Run the test**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~BridgeRampFeasibilityTests.Rule1_Infeasible_MixedObstacles_RaisesForPeer_DipsLowerRoad"
```
Expected: PASS. If the second crossing isn't detected (only one crossing in `plan.Crossings`), the harness builds crossings per network — instead construct the whole network once with both under-roads via `RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under, lower)` if that overload exists; otherwise keep the merge above. Confirm `GradeSeparatedCrossings` is the correct collection name by checking `UnifiedRoadNetwork` (adjust if it is `Crossings`).

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs
git commit -m "test(bridge): mixed-obstacle Rule-1 span dips road, raises for peer

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Full build + full suite verification

**Files:** none (verification only)

**Interfaces:** none.

- [ ] **Step 1: Build the library**

```bash
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true
```
Expected: all tests pass (the pre-change baseline was 508 green on the parent branch; this adds 5 new tests → expect ~513, with zero regressions). If any previously-green test now fails, it is a regression from the planner change — investigate before proceeding (most likely a Rule-1 span whose feasible path must stay byte-identical; confirm the `dipFallback==false` recompute reproduces `requiredDeckZFull`/`liftFull`).

- [ ] **Step 3: Commit any incidental fixes**

Only if Step 2 required a fix:
```bash
git add -A
git commit -m "fix(bridge): address regression from Rule-1 dip fallback

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- Trigger (feasibility on + infeasible + dippable exists) → Task 1 Step 4 (`dipFallback`), guarded by Task 2 (equal-priority, flag-off) and Task 2 (feasible).
- Dippable predicate (road, strictly-lower priority, not bridge-under) → Task 1 `IsDippable`.
- Burden split "no raise, dip full deficit" → Task 1 dip branch (`LowerRoadTargetZ = deckRef − sep`), asserted by Task 1 Step 2 (`IsRaised == false`).
- Mixed obstacle (raise for non-dippable, dip dippable) → Task 3.
- Byte-identical when flag off / feasible → Task 2.
- Winningen outcome (deck flat, road dips) → Task 1 (the `LowerPriorityRoad_Dips` case is the winningen shape).

**Placeholder scan:** none — all steps contain concrete code and commands. Task 3 Step 2 flags one harness-name uncertainty (`GradeSeparatedCrossings`) with an explicit fallback instruction rather than a placeholder.

**Type consistency:** `IsDippable(Obstacle)`, `dipFallback` (bool), `spanPinZ`/`spanLift` (float, reassigned span-locals), `DeckRefAt`/`SeparationFor` (existing local funcs), `BridgeElevationAction.DipLowerRoad`/`.RaiseBridge`, `CrossingPlan`/`SpanDeckPlan.IsRaised`, `plan.Crossings`/`plan.Spans` — all consistent across tasks and verified against the current source.
