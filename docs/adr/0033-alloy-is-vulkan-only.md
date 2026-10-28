# 0033 — Alloy is Vulkan-only, and a machine Vulkan refuses runs vanilla

Status: accepted (2026-10-28). Supersedes ADR 0010's "two render modes" in its GL reading, and retires the
GL frame ADR 0011, ADR 0016 and ADR 0032 costed.

## Context

Alloy shipped on OpenGL 4.6 and gained a Vulkan backend beside it (`managed/Rhi/`, the `CX.Rhi` seam;
`managed/Graphics/Vulkan/VkBackend*`, on Vortice.Vulkan with VMA and shaderc staged beside the extender).
For the length of the port both halves were live, kept in step by a whole-frame A/B, and the GL half paid
for the pairing twice over: every pass existed twice, and Alloy's GL work had to share the engine's own GL
with a **bracket** — a fence, a flush, two `glXMakeCurrent` and an error drain each way — plus a context
hand-over for the render thread and a graveyard that could only free a GL name on the thread holding the
context. None of that is a Vulkan cost. Keeping it would have meant maintaining GL interop forever to keep
a backend the Vulkan one had overtaken.

## Decision

**Vulkan is Alloy's only backend, and GL interop goes rather than being preserved.**

- **`Renderer.On => Enabled.Boot && VulkanUp()`** (`managed/Alloy/Core/Renderer.cs`). `VulkanUp` asks
  `VkBackend.Refusal` once per session and says the verdict in `cx.log`. A refusal — no Vulkan loader, a
  loader or device below 1.3, no `VK_KHR_surface`/window surface extension, no device, no graphics+compute
  queue, or the staged shaderc/VMA libraries not loading — **stands Alloy down for the session and the game
  runs vanilla**, reported as the opt-out, not as a fault. `alloy.backend` is retired: there is nothing to
  pick.
- **A Vulkan session has no GL context** (`managed/Graphics/NullGl*.cs`). Decided once at
  `SDL_CreateWindow`: the window is a Vulkan window, `SDL_GL_CreateContext` answers with a token nothing
  dereferences, and every GL entry nwmain imports or GLEW resolves is a CX stub. The engine keeps running
  its GL-shaped code against nothing; its texture uploads are captured into Vulkan images, and screenshots
  and save thumbnails are read back from the Vulkan composite. Vulkan presents the frame itself
  (`VkBackend.Present`) — the engine's swap never runs.
- **Shaders stay GLSL.** `managed/Alloy/Shaders/` is GLSL under `cx_*` resrefs of at most 16 characters, so
  every stage and library is still overridable as `<resref>.shd` from a hak or NWSync (ADR 0012, ADR 0024).
  `VkGlsl` translates the text for Vulkan and shaderc compiles it to SPIR-V at load; a content `.mtr`'s
  `customShaderVS`/`customShaderFS` pair runs the same way. The override contract is unchanged, and
  authors keep writing GLSL.
- **The one GL path CX keeps is for the session Vulkan refused.** CX's own windows replay through
  `TextRig`/`TextAtlas` on the engine's renderer when Alloy is off, and so does the NUI model-view solve
  (`NuiModelEngine`). That is the vanilla half of a feature, not a second renderer: nothing in it draws the
  world, and no Alloy pass has a GL arm to fall back to.

## Consequences

- **No bracket, no render thread.** The frame draws on the game thread; `GlContextService` survives as the
  present counter and the deferred-work pump, and its `Enter` refuses every asker loudly because there is
  no context to enter. The `alloy.gl46` knob no longer gates a GL context — what `alloy.gl46=0` now turns
  off is that pump.
- **"Alloy is on" and "Alloy is on Vulkan" are the same statement** (`Renderer.OnVulkan => On`). A refusal
  entry whose wording still names GL — the share-listed-context one in `AlloyGate` — can only mean "the
  render device did not come up"; the capability question is `VkBackend.Refusal`, asked at boot.
- **The GL context-loss edge is unreachable.** Its handlers are still compiled and `PassRegistry` still
  walks them, but a Vulkan session's `Available` never goes false. Vulkan device loss is not yet wired to
  that edge; a lost device is a fault on the submit, and recovering from one is open work.
- **GL objects no longer have a graveyard to go to.** `GpuGraveyard` retires GL names only and stays empty
  on Vulkan; a Vulkan object is destroyed through the backend, which holds it until the frames in flight
  are done with it.
- **Windows is the same decision.** `version.dll` loads the same managed image, NullGl takes the same
  window, and the Windows refusal is a Vulkan refusal like any other.
- **Not covered here:** what the engine's own GL renderer draws when Alloy stands down. That is the engine's
  behaviour and this decision does not touch it; a stood-down session is a vanilla client with the
  extender's other features on.
