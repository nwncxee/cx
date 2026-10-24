# 0032 — One GL bracket per world view

Status: accepted (2026-10-24). Amends ADR 0011 §"Two GL brackets per frame, as a budget".

## Context

ADR 0011 costed the Alloy frame at two GL brackets — one for the shadow bake at the engine's pass
slot, one for the colour pass at the anchor — and rejected collapsing them into one, for two
reasons that were true when it was written:

1. A single bracket "couples the shadow bake to the colour pass's re-entry". The bake hung off
   `RenderSinglePass`, an engine-chosen cadence; a bracket spanning both would have put Alloy's bake
   inside a window the engine decided the width of. That is the ADR 0007 bug.
2. "GUI 3D panels run `RenderSinglePass` too", so the colour bracket could open for a panel and the
   bake would be dragged in with it.

ADR 0028's scene database removed both. `RspShim` does not run: the bake is called from
`AlloyRenderer.DbWorldPass`, which is Alloy's own code, and a GUI panel view goes to `DbGui.Draw`
and never reaches the owned pass chain at all. What was left was a round trip for nothing — a fence,
a `glFlush`, a `MakeCurrent` and an error drain each way, twice, across a gap that issues no engine
GL whatsoever. The main-thread profile of a heavy area put `GlContextService.MakeCur` at 4.7%
self+driver with the bake's own Enter/Leave pair at 1.8% of a 17 ms frame.

## Decision

**A world view opens one bracket and closes one bracket.** `DbWorldPass` enters before the shadow
bake and leaves after the colour pass; `Enter`/`Leave` are reentrant on the game thread, so each
pass keeps its own pair and pays a depth bump instead of a switch.

- ADR 0011's **budget survives as a ceiling, not a target**: a pass that needs a bracket of its own
  is still a pass in the wrong slot. `FrameGraph`'s two `Bracket` members are now the frame's two
  *phases* — the bake's views and the colour pass's — and a third is still the design change
  `FrameGraph.SelfCheck` refuses.
- **Per-context GL state is read before the switch.** The engine's target block (viewport, the sRGB
  enable, the clear values) is per-context, so `AlloyRenderer.LatchFrameTarget` reads it in the
  engine's context ahead of the bracket and the colour pass reads the latch. This is the one thing
  the merge actually required.
- **The engine never issues GL inside the bracket.** Everything between the two passes reads the
  database (the particle sim, the beam and halo enrolment, the pose, the camera). The one site that
  must hand the context back mid-pass — the shader-compile notice, which presents a backdrop from
  the engine's context — uses `GlContextService.Suspend`/`Resume`, because a nested `Leave` would
  decrement a depth and hand nothing back.
- **The 2D UI keeps its own brackets.** `GuiPass`'s panel flush and its NUI flush are separated by
  CX's own window roll-call, which draws raw GL in the engine's context and resyncs `GLRender`'s
  shadow cache. Those two are not merged, and merging them would mean Alloy drawing the roll-call.

## Consequences

- Two fewer `MakeCurrent` calls, two fewer fences and two fewer flushes per world view. Measured
  from `cx.log`: the `[gl46]` line carries `ctx switches N (X.X/frame)`, and `CasterPass`'s period
  line carries what its own Enter/Leave pair costs — near zero once the view's bracket is open.
- A bracket crosses method boundaries (`BeginOwnedFrame` enters, its caller leaves), so the pairing
  is not a shape an analyzer can check. `GlContextService.OnFrame` is the check instead: the one
  point in the frame where the depth must be zero, and a frame that reaches the present with a
  bracket open says so and closes it rather than letting the engine present on Alloy's context.
- `GpuSpanLine`'s span now brackets the bake through the colour frame with no gap in the middle, so
  it reads as one block on the GPU timeline instead of two.
