# Junction Handling Test Cases

## Overview

This document specifies test cases for validating the Junction Surface Constraint implementation. The implementation replaces the flawed `JunctionSlopeAdapter` approach with direct edge elevation constraints at junctions.

**Implementation Status**: Phase 6 - Testing and Validation

---

## Test Case Categories

### Category A: T-Junction Scenarios

These test cases validate that secondary roads correctly terminate against primary roads with smooth surface connections.

---

#### Test Case A1: Flat Primary Road, Flat Secondary Road

**Description**: Basic T-junction where both roads are on flat terrain.

**Setup**:
- Primary road: Straight, no elevation change, no banking
- Secondary road: Perpendicular approach, no elevation change
- Junction type: Standard T-junction

**Expected Behavior**:
1. Secondary road's terminal cross-section aligns flush with primary road surface
2. No visible elevation step at the junction
3. Both edge elevations (left/right) of terminal cross-section match primary road surface
4. Gradient ramp applied to secondary road approaching junction (5-10m blend distance)

**Verification Steps**:
1. Generate terrain with simple T-junction road configuration
2. Inspect terminal cross-section of secondary road:
   - `LeftEdgeTargetElevation` should match primary road surface at left edge
   - `RightEdgeTargetElevation` should match primary road surface at right edge
3. Visual inspection: No "step" or discontinuity at junction
4. Check log output shows "T-junction harmonization" with constraint application

**Pass Criteria**: Terminal cross-section edges are coplanar with primary road surface (±0.01m tolerance)

---

#### Test Case A2: Sloped Primary Road (Uphill), Flat Secondary Road

**Description**: Secondary road terminates at a primary road that has longitudinal slope.

**Setup**:
- Primary road: Straight with 5% uphill grade (0.05 slope)
- Secondary road: Flat approach from the side
- Junction type: T-junction

**Expected Behavior**:
1. Secondary road tilts at junction to match primary road's cross-slope
2. The edge closer to uphill direction sits higher than the edge toward downhill
3. Primary road's longitudinal slope creates cross-slope on secondary road
4. Smooth gradient transition over blend distance

**Verification Steps**:
1. Calculate expected elevation difference across secondary road width at junction:
   - If road width = 8m, slope = 5%, difference = 8 × 0.05 = 0.4m
2. Verify left/right edge elevation difference matches expected
3. Check that the correct edge (uphill side) has higher elevation
4. Visual inspection: Secondary road "tilts" naturally into primary road slope

**Pass Criteria**: 
- Edge elevation difference within ±5% of calculated expected difference
- Correct uphill/downhill orientation of tilt

---

#### Test Case A3: Banked Primary Road (Curve), Flat Secondary Road

**Description**: Secondary road terminates at a primary road that is on a curve with banking.

**Setup**:
- Primary road: Curved section with 5° banking (superelevation)
- Secondary road: Straight approach from outer edge of curve
- Junction type: T-junction on curve

**Expected Behavior**:
1. Secondary road terminal cross-section matches banked surface of primary road
2. The inner edge of curve is lower than outer edge on primary road
3. Secondary road receives corresponding tilt at junction
4. Banking propagates appropriately along secondary road blend distance

**Verification Steps**:
1. Calculate expected elevation difference from banking:
   - If road width = 8m, bank angle = 5°, difference = 8 × sin(5°) ≈ 0.7m
2. Verify edge elevations account for banking
3. Ensure banking direction is correct (inner edge lower)
4. Check that secondary road blend doesn't fight against banking

**Pass Criteria**:
- Edge elevations reflect banked surface geometry
- No oscillation or "fighting" between banking and junction harmonization

---

#### Test Case A4: Sloped AND Banked Primary Road

**Description**: Complex case combining longitudinal slope with lateral banking.

**Setup**:
- Primary road: Curved uphill section with 3% grade and 4° banking
- Secondary road: Approaching perpendicular from outer edge
- Junction type: T-junction with combined effects

**Expected Behavior**:
1. Secondary road terminal surface accounts for BOTH slope and banking
2. Surface elevation calculation: `surfaceElevation = centerElevation + lateralOffset × sin(bankAngle) + longitudinalOffset × slopeGrade`
3. Both edge constraints properly calculated
4. The result is a smooth, physically plausible connection

**Verification Steps**:
1. Log the `JunctionSurfaceCalculator` output showing:
   - Lateral offset contribution
   - Longitudinal offset contribution  
   - Final surface elevation
2. Verify combined effect is additive (not multiplicative or averaged)
3. Visual inspection: Road looks naturally connected

**Pass Criteria**:
- Combined elevation effects are correctly summed
- Visual appearance is smooth and natural

---

#### Test Case A5: Same-Priority Roads at T-Junction

**Description**: Two roads of equal priority meeting at T-junction.

**Setup**:
- Both roads: Same road type (e.g., both "secondary")
- Same width, same priority level
- One road terminates, one continues

**Expected Behavior**:
1. Continuity detection still identifies which road is "through" vs "terminating"
2. Through road maintains its elevation profile
3. Terminating road adapts to through road
4. If ambiguous, use weighted average at junction point

**Verification Steps**:
1. Check junction classification in logs
2. Verify one road is identified as continuous, other as terminating
3. Confirm the correct road adapts (terminating one)

**Pass Criteria**:
- Junction correctly classified despite equal priority
- No deadlock or infinite loop in harmonization

---

### Category B: Multi-Way Junction Scenarios

---

#### Test Case B1: Y-Junction with 3 Equal-Priority Roads

**Description**: Three roads meeting at roughly 120° angles (Y-junction).

**Setup**:
- Three roads with equal priority
- Approximately equal angles between them
- Mixed elevations on approach

**Expected Behavior**:
1. Junction classified as Y-junction
2. Plateau smoothing applied at center
3. All three roads blend smoothly toward common center elevation
4. No single road "wins" - weighted average used

**Verification Steps**:
1. Check junction type classification = "Y"
2. Verify plateau smoothing is applied
3. Check that center point is weighted average of approach elevations
4. All three road terminals at similar elevation (accounting for blend distance)

**Pass Criteria**:
- Junction type correctly identified as Y
- Center plateau is reasonable average of approaching roads
- All connections visually smooth

---

#### Test Case B2: Cross-Roads (X-Junction / 4-Way Intersection)

**Description**: Four-way intersection with two crossing roads.

**Setup**:
- Two roads crossing perpendicular
- Different priorities (e.g., primary road crossing secondary road)
- Primary road is straight through; secondary road has stop/yield

**Expected Behavior**:
1. Junction classified as Cross or X-junction
2. Higher-priority road maintains its profile through junction
3. Lower-priority roads adapt to cross at correct elevation
4. All four approaches blend to junction appropriately

**Verification Steps**:
1. Check junction type classification
2. Verify primary road has minimal disruption through junction
3. Secondary road adapts elevations on both sides
4. Plateau smoothing applied for visual quality

**Pass Criteria**:
- Priority correctly determines which road "wins"
- Cross-roads meet at consistent elevation
- No abrupt steps at any approach

---

### Category C: Edge Cases and Error Handling

---

#### Test Case C1: Very Steep Primary Road (>10% Grade)

**Description**: Secondary road meeting primary road with extreme slope.

**Setup**:
- Primary road: 15% grade (steep hill climb)
- Secondary road: Flat approach

**Expected Behavior**:
1. Constraint system handles extreme angles
2. Secondary road may have significant tilt at junction
3. Blend distance may need to be longer to avoid abrupt transitions
4. No numerical instability or NaN values

**Verification Steps**:
1. Check for any NaN or infinity values in logs
2. Verify calculated elevations are within reasonable bounds
3. Gradient doesn't exceed physically reasonable limits

**Pass Criteria**:
- No numerical errors
- Result is physically plausible (even if steep)

---

#### Test Case C2: Very Narrow Junction Connection

**Description**: Secondary road barely intersects primary road edge.

**Setup**:
- Secondary road approaches at extreme angle
- Only small overlap at junction

**Expected Behavior**:
1. Junction still detected correctly
2. Constraint applied even for minimal overlap
3. Edge calculations handle small lateral offsets

**Verification Steps**:
1. Verify junction is detected
2. Check lateral offset calculations
3. Confirm constraints are applied

**Pass Criteria**:
- Junction detected despite minimal overlap
- No crashes or undefined behavior

---

#### Test Case C3: Multiple T-Junctions in Sequence

**Description**: Primary road with several T-junctions close together.

**Setup**:
- Primary road: Continuous straight road
- Multiple secondary roads: Spaced 50m apart
- Each secondary approaches from alternating sides

**Expected Behavior**:
1. Each junction harmonized independently
2. Primary road profile maintained between junctions
3. No interference between adjacent junction harmonizations

**Verification Steps**:
1. Each secondary road terminus is correctly constrained
2. Primary road elevation is consistent between junctions
3. No "ripple" effects propagating between junctions

**Pass Criteria**:
- All junctions handled correctly
- No cross-contamination of constraints

---

## Verification Procedures

### Visual Verification Checklist

When generating terrain for testing, verify:

- [ ] No visible "steps" at any T-junction
- [ ] Road surfaces appear continuous at junctions
- [ ] Banking is preserved correctly on curved roads
- [ ] Secondary roads tilt appropriately when meeting sloped primary roads
- [ ] Gradient ramps appear smooth and natural
- [ ] No sudden elevation changes within blend distance

### Log Verification Checklist

Review terrain generation logs for:

- [ ] "T-junction harmonization" messages for each T-junction
- [ ] Junction Surface Constraint calculations showing:
  - Lateral offset contribution
  - Longitudinal slope contribution (if applicable)
  - Final edge constraint values
- [ ] Propagation messages showing blend distance application
- [ ] No ERROR or WARNING messages related to junction handling
- [ ] Phase 3 summary showing junction counts by type

### Numerical Verification

For automated/semi-automated testing:

```
Expected Formula:
surfaceElevation = centerElevation 
                 + (lateralOffset × sin(bankAngle))
                 + (longitudinalOffset × primarySlope)

Where:
- lateralOffset: Distance from centerline to edge (positive = right)
- bankAngle: Banking angle in radians
- longitudinalOffset: Distance along primary road from reference cross-section
- primarySlope: Longitudinal slope of primary road (rise/run)
```

Tolerance: ±0.02m for edge elevation constraints

---

## Test Data Sources

### Recommended OSM Areas for Testing

1. **Simple T-junctions**: Rural areas with farm access roads
2. **Sloped T-junctions**: Hilly terrain with road network
3. **Banked curves with T-junctions**: Mountain roads with switchbacks
4. **Complex intersections**: Urban areas with multiple road types

### Synthetic Test Cases

For controlled testing, create synthetic road networks with:
- Known exact angles
- Known exact slopes
- Known exact widths
- Predictable expected outcomes

---

## Success Metrics

| Metric | Target | Method |
|--------|--------|--------|
| Visual discontinuities | 0 per 100 junctions | Manual inspection |
| Elevation step at T-junction | <0.02m | Automated measurement |
| Build warnings | 0 junction-related | Compiler output |
| Performance regression | <5% increase | Timing comparison |
| Code complexity | <1000 lines | LOC count |

---

## Regression Testing

After any changes to junction handling:

1. Re-run all Category A tests
2. Verify build still succeeds with no new warnings
3. Generate terrain from known test OSM data
4. Compare results against baseline images/measurements
5. Update this document if test cases need modification

---

## Document History

| Date | Version | Changes |
|------|---------|---------|
| 2026-01-18 | 1.0 | Initial test case documentation for Phase 6 |
