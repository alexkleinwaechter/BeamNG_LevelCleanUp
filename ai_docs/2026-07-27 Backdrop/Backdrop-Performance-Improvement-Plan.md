# Backdrop Performance Improvement Plan

**Status:** §0.1 + §1–§4 IMPLEMENTED 2026-07-28 (commits `cdb262c` timing, `8184028` BucketSize,
`9ab77f3` double-refine, `fc9b6a6` border sets, `4b7c3cd` DEM hot path — each landed separately,
1140/1140 green after every one). §5–§8 remain measurement-gated: wait for the first real-map
`[BACKDROP] timing:` line before doing anything further.
**Trigger:** first real in-app backdrop generations are reported as very slow.
**Companion doc:** [Backdrop-Tutorial.md](Backdrop-Tutorial.md).

Every suspect below was flagged during implementation review as a production-scale watch item that
unit tests could not exercise (the test fixtures are far smaller than real maps). None of this is
guesswork about *where* the code spends time in principle — but **none of it has been profiled on a
real map yet**, so the plan starts with measurement, and the fixes are ordered by expected payoff ÷
risk. All fixes must preserve the feature's determinism guarantee (identical inputs ⇒ bitwise
identical meshes), which is pinned by the existing 1140-test core suite.

---

## 0. Measure first

The generation has three phases with very different bottlenecks. Before changing anything, find out
which phase dominates:

| Phase | Bound by | How to spot it |
|---|---|---|
| Raster load + combine | GDAL / disk | Long pause before the first per-chunk message; `[REDUCE]`-style stalls right after "Generate" |
| Mesh refinement (per chunk) | CPU, single-thread | Long gaps *between* consecutive per-chunk progress messages, no network traffic |
| Texture bake (per chunk) | Network (tile downloads) or CPU (warp) | Per-chunk messages appear quickly but tile/warp work follows; "Using cached map tile overlay" lines mean the warp cache is hitting |

**Step 0.1 — add phase timing (small, do this first): DONE (`cdb262c`).** One `Stopwatch` per phase
in `BackdropGenerator.Generate` (raster load / mesh loop incl. worst chunk / debug artifacts, all
surfaced on `BackdropGenerationResult`) and one in `BackdropOrchestrator.GenerateAsync` around the
texture bake. The combined summary line
(`[BACKDROP] timing: rasters=…s mesh=…s (N chunks, worst=…s <dae>) debug=…s textures=…s`) is sent
through PubSub (user-visible message log, invariant culture) and also Console-logged by the
generator. This turns every user report into a usable measurement.

**Step 0.2 — record a baseline** on the map that prompted the complaint: total wall clock + the
timing line + chunk count + backdrop area. Keep the numbers in this file (table at the bottom).
*Note: §1–§4 landed in the same session as the instrumentation (before any real-map run), so a true
pre-fix baseline was never measurable in-app — the first user run records the post-§1–§4 state,
which is what matters for deciding whether §5–§8 are needed.*

The remaining sections assume the mesh loop dominates, which is what the review predicted. If the
measurement says otherwise, jump to §7/§8.

---

## 1. `BucketSize = 64` → `4` (highest expected payoff, one line) — **DONE (`8184028`)**

`BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs:305` (`private const int BucketSize = 64`).

The restricted-quadtree **Balance** step finds neighbor leaves through a spatial hash whose buckets
are 64×64 lattice cells. In the edge band the tree refines down to unit leaves, so a single bucket
can hold ~4096 leaves and every neighbor query degenerates into a near-linear scan of the bucket —
the review estimated **~60–80 s per edge chunk at defaults** from this constant alone. Bucket size
only partitions the *query*; results, and therefore the mesh, are unchanged by construction.

- Fix: lower the constant to ~4. Optionally make it adaptive (`max(4, chunkLatticeSize / 256)`), but
  measure the constant first — it is probably enough.
- Risk: none to output (verify per §9 anyway).

## 2. Stop refining every chunk twice in the app — **DONE (`9ab77f3`, preferred fix: internal `MeshChunk(chunk, out leaves)` overload feeds the debug artifact)**

`BeamNgTerrainPoc/Terrain/Backdrop/BackdropGenerator.cs:126` runs `mesher.MeshChunk(chunk)` (which
internally calls `RefineChunk`, line 58); when debug artifacts are enabled, line 138 calls
`mesher.RefineChunk(chunk)` **again** for the quadtree level map. The app always enables debug
artifacts (`BackdropOrchestrator` always passes a debug path), so every in-app generation pays the
full refinement cost twice.

- Fix (preferred): surface the leaves from the `MeshChunk` call (add the leaf list to
  `BackdropChunkMeshResult` or an `internal` out-parameter overload) and feed the debug artifact
  from that, deleting the second `RefineChunk` call. Keeps the debug artifacts, halves mesh time.
- Alternative (if the surface change is unwanted): make debug artifacts opt-in from the orchestrator
  (e.g. only when a diagnostics toggle is set). Loses always-on artifacts — less desirable while the
  feature is young.
- Note: refinement is deterministic, so the second call today produces identical leaves — reusing
  them cannot change any artifact byte.

## 3. Remove the redundant edge re-subdivision inside `MeshChunk` — **DONE (`fc9b6a6`, private `BorderSets` record + `ComputeBorderSets` shared by `RefineChunk` and `MeshChunk`)**

`BackdropQuadtreeMesher.cs:76-85` re-runs the four `BackdropEdgeSubdivider.Subdivide` calls that
`RefineChunk` already executed at `:243-249` (flagged in review as silent coupling). Subdivide is
cheap relative to Balance, but the fix is free once §2 touches this seam anyway: compute the four
border sets once, pass them to both consumers via a shared private helper.

## 4. De-allocate the hot DEM sampling path — **DONE (`4b7c3cd`, array field + indexed loop; the last-hit-raster bonus was deliberately SKIPPED — measure first, and it would add mutable state that blocks §6's one-mesher-per-chunk parallelization)**

`BeamNgTerrainPoc/Terrain/Backdrop/BackdropHeightField.cs:53` — `SampleDemElevation` iterates
`_bandRasters` declared as `IReadOnlyList<BackdropRaster>` (`:11`). A `foreach` over the interface
allocates a boxed enumerator **per call**, and this method runs per error-metric sample and per
vertex — millions of times per generation (review flag from Task 4).

- Fix: store the strips as a concrete array and use an indexed `for` loop.
- Bonus (measure first): consecutive samples nearly always hit the same strip — remember the
  last-hit raster and test it first before scanning the list.

## 5. Normal pass sampling (only if §1–§4 are not enough)

The vertex-normal pass samples the DEM with finite differences — several extra `SampleDemElevation`
calls per vertex in the band (review flag from Task 8). After §4 these calls get much cheaper;
if the pass still shows up in measurements, compute normals from the already-sampled height lattice
(or from mesh face normals) instead of re-querying the DEM.

## 6. Parallelize the chunk loop (bigger change, do last)

Chunks are independent by construction — cross-chunk border identity comes from the deterministic
`BackdropEdgeSubdivider`, not from shared state. The mesher instance however is **non-reentrant**
(`LastFallbackCount`), so parallelization means one mesher instance per chunk:

- `Parallel.ForEach` over chunks (degree ≈ cores − 1), collect results, then **sort by (Cx, Cy)
  before writing** the scene/materials/settings so all output ordering stays deterministic.
- Do this only after §1–§3: parallelizing a 2× redundant, badly-bucketed loop wastes cores on work
  that should not exist.

## 7. If the raster phase dominates instead

- The fresh-combine grid probe goes through `GetGeoTiffInfoExtended`, which runs an exact
  `ComputeRasterMinMax` full-raster scan as a side effect (review flag from Task 14) — on a big
  mosaic that is a full read just to check dimensions + geotransform. Fix: a probe-only variant that
  skips min/max.
- `MaxFarRasterDimension` (default 8192): halving it quarters the far-raster read and memory. This
  is also a **user-side mitigation** available today (documented in the tutorial).

## 8. If the texture bake dominates instead

The bake is per chunk: tile download (network, shared `MT_Tiles\cache` — only cold the first time)
+ warp + adjustments. Little to optimize code-side; the levers are user-side: lower
`Max chunk texture`, coarser `Texel density`, fewer/larger chunks (`Chunk size` up). If profiling
shows the *warp* (not download) dominating on cache-hot runs, per-chunk warps could be parallelized
— network courtesy to tile providers means downloads should stay sequential.

Related user-side lever for **level-loading** time (not generation time): the backdrop panel's
**Collision Mesh** switch (default off since 2026-07-28). Since the same-day rework the DAEs never
embed collision geometry — drivability is purely the TSStatic `collisionType "Visible Mesh Final"`
scene property (user finding: the editor's Collision dropdown), so the switch costs no disk either
way; on ⇒ the game builds physics from the visual mesh at load, off ⇒ that build is skipped
(`05-collision-toggle-followup.md`).

Output-size datum (2026-07-28, rossfeldpanorama 32-chunk bake, collision off): **1.78 GB** DAE /
19.3 M triangles ≈ 92 MB per million triangles; ~700 MB of it was six *far* chunks over rugged
alpine terrain (far tolerance 8 m), ~300 MB the forced-full-res 200 m edge band. App defaults were
reduced the same day to edge band 100 / near 1.0 / far 16.0 (`BackdropSettings.cs`; core/spec §15
values unchanged) — see the tutorial's §3 size rule-of-thumb box.

---

## 9. Validation per step

Each change lands separately with:

1. `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj` — all 1140 green, no test
   edited.
2. Determinism check: one fixed map generated before/after — DAEs, `backdrop.materials.json`,
   `items.level.json` and the debug artifacts must be **byte-identical** (only timings may differ).
   (§6 additionally: run twice after the change — parallel run must equal itself and the serial
   baseline.)
3. The §0 timing line recorded in the table below.

*Status of this checklist for the landed §1–§4:* (1) done after every commit (1140 green, no test
edited). (2) **outstanding** — no app run happened in-session; identity is argued by construction
(§1 partitions only the query, §2/§3 reuse deterministic results, §4 changes iteration mechanics
only) and pinned by the suite (incl. `MeshChunk_IsDeterministic` and the Balance perf/invariant
tests), but the next real-map regen should still be eyeballed against a pre-change generation if
one exists. (3) pending the first real-map run.

## 10. Measurements

| Date | Map / backdrop area | Change | rasters | mesh (worst chunk) | debug | textures | Total |
|---|---|---|---|---|---|---|---|
| _pending_ | first real-map run | after §1–§4 | | | | | |

*(fill in as work proceeds — paste the `[BACKDROP] timing:` line from the app's message log. A
pre-fix baseline does not exist: the instrumentation and §1–§4 landed together, see §0.2 note.)*
