# Implementation Session Prompt

Paste this into a new Claude Code session to execute the plan.

---

## Prompt

Execute the implementation plan at `docs/superpowers/plans/2026-03-20-roundabout-decalroad-fixes.md` using subagent-driven development.

**What the plan does:** Fixes DecalRoad junction interruption at roundabouts (3 bugs) and makes roundabout AI roads one-way.

**The plan has 2 tasks with sub-tasks:**

**Task 1** — Fix Roundabout Junction Influence Zones and Corridor Overlap (sub-tasks 1a through 1e):
- 1a: Add `IsClosedLoop` flag to `RoadCorridor` model
- 1b: Set `IsClosedLoop` in `RoadCorridorBuilder` + add roundabout-first layer set resolution
- 1c: Handle closed-loop wrap-around in corridor overlap checker (with TDD tests)
- 1d: Add roundabout-wide junction influence zones (with TDD tests)
- 1e: Exclude roundabout rings from continuity lookup in `DecalRoadGenerator`

**Task 2** — Roundabout AI Road One-Way Configuration (sub-tasks 2a through 2c):
- 2a: Add `"roundabout"` default layer set with one-way AI road defaults
- 2b: Override AI road properties for roundabout splines in generator
- 2c: Resolve roundabout-specific layer set via `IsRoundabout` spline flag

**Key codebase context:**
- .NET 9, C#, xUnit for tests
- DecalRoad generation uses a two-pass corridor-based overlap system for junction suppression
- Roundabout ring splines have `IsRoundabout = true` on `ParameterizedRoadSpline`
- Roundabout junctions are `JunctionType.Roundabout` in `NetworkJunction`
- `ParameterizedRoadSpline` has `required RoadSpline Spline` — tests need `new RoadSpline(points)`
- `NetworkJunction.Contributors` is a get-only `List` — use `AddRange()` not initializer syntax
- Build with `dotnet build`, test with `dotnet test BeamNgTerrainPoc.Tests`

Execute the sub-tasks sequentially using subagent-driven development. Each sub-task should be dispatched to a subagent, then reviewed for spec compliance and code quality before moving on. The plan file has complete code snippets for every step.
