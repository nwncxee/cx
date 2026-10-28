# The nameplate / chat-bubble seam

Implementation guide for a NEW managed UI feature: world-anchored overhead elements —
(a) a **nameplate** (portrait + name + health band, on hover / Tab-highlight) and
(b) an overhead **chat bubble** (what that creature just said), shown when the nameplate is not.

Everything below is what already exists, with file:line. Where something does **not** exist,
it says so explicitly — those are the pieces the implementer has to build.

> **Status (ADR 0025, #594).** The feature shipped (`managed/Services/Nameplate/`). The settings
> model this guide was written against is gone: there is one `cx.cfg`, every knob is a
> `KnobGroup` declaration (`NameplateKnobs`, group `nameplate`), the panel is `/settings`, and
> the chat command is the generated `/nameplate`. The whole-extender opt-out below is spelled
> `alloy.enabled=0`; `/np`, `cx-nameplate.cfg`, `ConfigFile.Load/Save`, `Settings.Bool/Int/Float/Text`,
> the "two buckets" of ADR 0013 and the per-feature `SettingsPanel` gear no longer exist. §5 and §6
> are kept as the record of the seam they described; build against `docs/spec/unified-settings.md`.

---

## 0. Executive summary of what is and isn't there

| Need | Status |
|---|---|
| Register a window + per-frame draw | **Exists**, one constructor (`new Window(...)` with `DrawContent`) |
| Filled rect / textured quad / text / clip | **Exists** (`VertexBatch`, `TextRig.DrawBatch`, `TextRig.DrawPortrait`) |
| Rounded rect / border / drop shadow helper | **Does not exist**. Borders = 4 rects; there is no rounded/shadow primitive at all |
| Outlined text | **Exists** — 8-way ±1px black halo, `WindowRenderer.EmitRow` |
| Animation model | **Exists** — two of them (`ChatAnim` exponential residual; Dialog `MoveToward` linear fade) |
| `/command` + per-feature cfg + `/knobs` | **Exists** (`CommandRegistry`, `ConfigFile`, `Settings`) |
| Settings panel rows | **Exists** (`SettingsPanel` + `SetRow`/`SetKind`/`SetAct`) |
| World→screen projection helper | **DOES NOT EXIST.** This is the first world-anchored UI. See §3 |
| Camera valid at present time | **Latched only**, and only when Alloy is not `alloy.enabled=0`. See §3 |
| Speaker object id from chat ingest | **Not available** at the chat tap. Use the bubble hook instead. See §12 |
| Depth occlusion (plate behind a wall) | **Exists, Alloy only**: `nameplate.behindwalls` fades a plate whose creature the frame's depth covers (`UiScene`, `TextRig.Anchor`) |
| Any test framework | **None.** `run-demo.sh` greps the log for `SelfCheck.*FAIL`. That is the whole test surface |

---

## 1. Registration — the minimum code

A window registers itself by **being constructed**. There is no discovery interface for UI, no
`Register` call, no unregister needed.

`managed/Ui/Window.cs:64-68`:

```csharp
public Window(float x, float y, float w, float h)
{
    X = x; Y = y; W = w; H = h;
    UiHost.Register(this);
}
```

`managed/Ui/Window.cs:56-60` — the draw slot:

```csharp
/// <summary>The service's per-frame content draw, invoked by <see cref="UiHost.DrawFrame"/> on the render
/// thread once per frame in z-order. ... Hot: a cached delegate invoke, zero-alloc.</summary>
public Action? DrawContent;
```

Input slots, `managed/Ui/Window.cs:41-47` (copy these signatures verbatim):

```csharp
public Func<bool, int, int, int, bool>? OnMouseButton;   // (down, button, x, y) -> consumed
public Func<int, int, bool>? OnMouseMotion;              // (x, y) -> consumed
public Func<int, bool>? OnMouseWheel;                    // (dy) -> consumed
public Func<uint, int, bool>? OnKey;                     // (sdlKey, sdlMods) -> consumed
public Action<IntPtr>? OnText;                           // SDL_TEXTINPUT payload
public Func<float, float, bool>? HitTestOverride;        // (px, py) -> over our live UI
```

The service shape (`managed/Framework/IExtenderService.cs`) is one method:

```csharp
public interface IExtenderService
{
    void Initialize(ExtenderLog log, HookService hooks);
}
```

**Minimum viable nameplate service** (modelled on `DialogService.Initialize`,
`managed/Services/Dialog/DialogService.cs:130-160`):

```csharp
public sealed unsafe class NameplateService : IExtenderService
{
    private static ExtenderLog? _log;
    private static Window? _window;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        NameplateConfig.SelfCheck(log);          // run-demo's only gate — see §11

        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,                   // never takes focus/keyboard
            DrawContent = Draw,                  // hot, per frame, in z-order
            OnMouseButton = OnMouseButton,       // click-a-plate (§10)
            OnMouseMotion = OnMouseMotion,
            HitTestOverride = (px, py) => OverPlate(px, py),
        };

        CommandRegistry.Global.Register("/nameplate", "report or tune the overhead nameplates", Cmd, "/np");
    }
}
```

Discovery is automatic — `ServiceHost` reflects over every `IExtenderService`, **sorted by name**, and
calls `Initialize` on each; `HookService.Commit()` then installs all hooks at once.

### The per-frame draw — the skeleton

Stitched from `DialogService.Draw` (`managed/Services/Dialog/DialogService.cs:752-829`). This is the
shape; the sections below fill in each line.

```csharp
/// <summary>Hot: the framework Window's content-draw slot. Zero-alloc in the steady state (§7).</summary>
private static void Draw()
{
    if (!_configLoaded) LoadConfig();          // §5: deferred, stays retryable until the user dir resolves

    // Session edge: a module unload must drop every cached plate/bubble (they name a dead world).
    bool inModule = GameGate.InModule;
    if (_wasInModule && !inModule) ResetSession();
    _wasInModule = inModule;

    if (!GameGate.InGame) return;              // Dialog:757 — no module, ESC menu up, or HUD toggled off
    GlyphAtlas? atlas = TextRig.Atlas;
    if (atlas == null || !_cfg.On) return;

    AdvanceAnim(Environment.TickCount64);      // §4 — the only dt source there is
    RefreshPlates();                           // §12: id -> live object -> world pos; retires dead ids

    int n = 0;                                 // float cursor into the reused buffer (§7)
    float su = atlas.SolidU, sv = atlas.SolidV;
    for (int i = 0; i < _plateCount; i++)
    {
        ref Plate p = ref _plates[i];
        if (!WorldToScreen(p.World, out float sx, out float sy)) continue;   // §3; false = behind camera
        EmitPlate(ref n, su, sv, atlas, in p, sx, sy);                       // rects + cached glyphs
    }
    if (n > 0) TextRig.DrawBatch(_verts, n / VertexBatch.Floats, _alpha);    // §2

    DrawPortraits();                           // §9 — batched, second program, resolve-all-then-upload
    PublishHitRects();                         // §10 — cold-swap the immutable rect array for input
}
```

`ResetSession()` on the `InModule` true→false edge is the standing pattern —
`ChatService._wasInModule` (`managed/Services/Chat/ChatService.cs:548`) and `QuickbarService.Sleep()`
(`managed/Services/Quickbar/QuickbarService.cs:~200`) both do exactly this.

### Z-order gotcha (read this)

`UiHost.Register` (`managed/Ui/UiHost.cs:73`) assigns `w.Z = ++_zCounter` in **registration order**,
which is discovery order, which is **type-name sorted**. `DrawFrame` paints ascending z, so the last
registered paints last (on top).

`ChatService` < `DialogService` < `NameplateService` alphabetically ⇒ **nameplates would paint over the
chat box and over the dialog panel.** That is almost certainly wrong for this feature. `Window.Z` is
`internal set` and there is no send-to-back API. Options:

- add a `UiHost.SendToBack(Window)` / a `Z` initialiser to the framework (a real, small framework change), or
- name the class so it sorts first (fragile, don't), or
- accept it and confirm with the user.

Flag this to the user before writing the draw.

### Register ONE window for all plates

`managed/Ui/UiHost.cs:41-48` (type header) states the registry is **immutable after the first frame**
(no Register/Unregister after boot) — that is what makes the render-thread walk lock-free. So the
feature is one `Window` that draws N plates, not N windows. Likewise `Window.Regions` is a
`readonly List` with a standing warning never to mutate it live, so per-plate cursor regions are not
reachable today; use `HitTestOverride` scanning a cold-swapped immutable array of plate rects.

---

## 2. Drawing primitives

Everything is **quads sampling one atlas texture**. Chrome rects sample a solid-white texel in that
same atlas, so chrome and text are one program and one draw call.

### The vertex format

`managed/Ui/VertexBatch.cs:16` — 8 interleaved floats: `x, y, u, v, r, g, b, a`.

### Filled rect

`managed/Ui/VertexBatch.cs:32-33`:

```csharp
/// <summary>Solid-colour rect (chrome): both texcoords pin to the white texel so <c>tex.r==1</c>.</summary>
public void Rect(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
```

Per-corner gradient (the only other shape helper), `managed/Ui/VertexBatch.cs:37-40`:

```csharp
public void GradientRect(float x0, float y0, float x1, float y1,
    (float r, float g, float b, float a) tl, (float r, float g, float b, float a) tr,
    (float r, float g, float b, float a) br, (float r, float g, float b, float a) bl)
```

### Glyph

`managed/Ui/VertexBatch.cs:47` and `:55`:

```csharp
public void Glyph(in Glyph gl, float penX, float baseline, float r, float g, float b, float a)
public void GlyphItalic(in Glyph gl, float penX, float baseline, float r, float g, float b, float a,
                        float shear = 0.2f)
```

### Text draw

There is **no** `DrawText(string, x, y)` in the framework. Every consumer writes its own per-glyph loop.
The two existing loops to copy:

`managed/Ui/SettingsPanel.cs:389-401` (private — copy it, you can't call it):

```csharp
private static float DrawString(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                                uint rgb, float clipX)
{
    float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
    float penX = startX;
    foreach (System.Text.Rune ru in s.EnumerateRunes())
    {
        if (penX >= clipX) break;
        if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
        else penX += atlas.AdvanceOf(ru);
    }
    return penX;
}
```

`managed/Services/Chat/Render/WindowRenderer.cs:633-650` — the same loop with per-rune colour **and the
outline** (see §8).

### Text measure

The one public measurement primitive, `managed/Services/Chat/Render/RowAlign.cs:46-51`:

```csharp
/// <summary>Advance of a rune range starting from <paramref name="startX"/> ...</summary>
public static float Measure(GlyphAtlas atlas, List<Rune> runes, int from, int to, float startX)
```

Per-rune advance, `managed/Ui/GlyphAtlas.cs:47-52`:

```csharp
public float AdvanceOf(System.Text.Rune r)
public bool TryGet(int cp, out Glyph g)
```

Line metrics: `GlyphAtlas.LineHeight`, `GlyphAtlas.Ascent` (`managed/Ui/GlyphAtlas.cs:22`).

### The actual submit

`TextRig` is a **draw recorder, not a renderer**: `DrawBatch` appends a `UiDrawKind.Text` command to the
frame's `UiDrawList`, and `TextRig.Replay` issues the finished list in whichever context draws the frame —
Alloy's Vulkan GUI pass, or the engine's own GL present when Alloy is off. Nothing below decides who draws.

`managed/Ui/TextRig.cs` — this is the one call a window's `DrawContent` makes:

```csharp
public static void DrawBatch(float[] verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                             bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
```

- `count` is the **vertex count** (`n / VertexBatch.Floats`), not the float count.
- `alpha` multiplies the whole batch (`uAlpha`) — the whole-plate fade knob.
- `offX/offY` translate every vertex (`uOffset`) — used by Dialog for scroll; a nameplate could use it
  for a slide-in.
- `clip` → a scissor box in screen px, y-down top-left, carried on the command and applied by the replay.
- The replay sets its own full draw state (blend on, depth off, cull off), snapshots nothing (#210), and
  on the GL side `UiHostService` resyncs the engine after the roll-call.

Textured quad (RGB, a command of its own kind): `managed/Ui/TextRig.cs:455`, see §9.

### Rounded rects / borders / shadows

**None exist.** Everything is axis-aligned quads. The border precedent is four rects,
`managed/Services/Dialog/DialogService.cs:848-854`:

```csharp
private static void EmitBorder(ref int n, float su, float sv, float x, float y, float w, float h)
{
    EmitRect(ref n, su, sv, x, y, x + w, y + BorderW, BorderCol);
    EmitRect(ref n, su, sv, x, y + h - BorderW, x + w, y + h, BorderCol);
    EmitRect(ref n, su, sv, x, y, x + BorderW, y + h, BorderCol);
    EmitRect(ref n, su, sv, x + w - BorderW, y, x + w, y + h, BorderCol);
}
```

For a nameplate you get soft edges only by: (a) `GradientRect` fading alpha at the edges, (b) a stack
of stepped rects, or (c) a new shader program. Don't add a program unless the look demands it.

### Z-order / layering

Within one window there is no layering: order of emission into the batch **is** the order of draw, and
blending is `SRC_ALPHA, ONE_MINUS_SRC_ALPHA` with depth test off. So paint back-to-front by hand.
Across windows, `UiHost.DrawFrame` (`managed/Ui/UiHost.cs:338`) walks ascending `Z`.

---

## 3. Coordinate space, and world→screen

### What a window draws in

**Absolute screen pixels, top-left origin, y grows downward.** `TextRig` publishes the frame's viewport once
per present — Alloy's screen size when Alloy is on, the live GL viewport when it is not — and the replay
builds the ortho from it (`managed/Ui/TextRig.cs`):

```csharp
/// <summary>Column-major ortho for glOrtho(0,w,h,0,-1,1): top-left origin, y grows downward.</summary>
internal static void Ortho(float* m, float w, float h)
```

The viewport is published each frame for cold consumers (`managed/Ui/TextRig.cs:73`):

```csharp
/// <summary>Last swap-time viewport (px), published each hot frame for cold consumers ...</summary>
public static volatile int ViewportW, ViewportH;
```

**These are the pixels the ortho uses. Any world→screen mapping must land in this space.**

### Scale

`managed/Ui/Metrics.cs`:

```csharp
public const float DesignFontPx = 18f;
public static float GameUiScale = 1f;   // engine CAurora::GetGUIScale, synced per frame by UiHostService
public static float Scale = 1f;         // effective font px / DesignFontPx
```

`UiHostService.SyncUiScale()` (`managed/Ui/UiHostService.cs:196`) refreshes `GameUiScale` every in-game
frame. `Metrics.Scale` is recomputed **cold** on a font-size or UI-scale change, and the glyph atlas is
re-baked at the same effective px (`TextRig.SetFont`), so glyph metrics already carry the scale — only
your own chrome constants need `* Metrics.Scale`. `SettingsPanel` shows the pattern
(`managed/Ui/SettingsPanel.cs:51-63`): every metric is `static float X => 22f * Metrics.Scale;`.

Nameplate constants (plate padding, portrait size, band height, font baseline offsets) should follow
the same `design px * Metrics.Scale` rule.

### Drawing at an arbitrary screen point

Trivial: emit your quads at that pixel. There is nothing to opt into.

### World→screen — this is the hard part, and it does not exist yet

**Nothing in `managed/` projects a world position to screen.** `grep -ri "worldtoscreen|screenpos|projectpoint"`
across `managed/` returns one unrelated comment in `SymbolMap.cs:230`. This is the first such consumer.

**Hazard (from repo memory, re-stated):** never read the camera at present time. `CurrentCamera` inside
`SwapBuffers` is the **UI frame's** camera (observed at `(0,0,742)`), not the scene camera. Any camera
used by a nameplate must be a value **latched during the world frame**.

Two options, both of which the implementer must verify live.

**Option A — latch the engine's own VP (exact, one line).**
`managed/Alloy/AlloyRenderer.Frame.cs:94-96` already reads the engine matrix stacks inside the colour pass:

```csharp
Matrix4x4 view = *(Matrix4x4*)(_mtxStacks + ModeView * StackStride + StackTop);
Matrix4x4 proj = *(Matrix4x4*)(_mtxStacks + ModeProjection * StackStride + StackTop);
_projMtx = proj;   // #347: what a spliced stock file reads as m_proj
Matrix4x4 vp = view * proj;
```

(`StackStride = 0x58, StackTop = 0x18, ModeProjection = 0, ModeView = 1` — `AlloyRenderer.cs:156`;
symbol `m_aurMtxStack` — `SymbolMap.cs:361`.)
`vp` is pass-local and unpublished. Publishing it into a `public static Matrix4x4 SceneVp` + a frame
sequence number is the exact, no-reconstruction answer. **Latch it only for the WORLD scene** — GUI 3D
panels (the paperdoll and friends) drive the same `Scene` render virtuals, and latching their camera
would put every plate at the wrong place for a frame. `AlloyRenderer.FrameAnchor` already gates on
`IsWorldScene(scene)`; latch at that gated site, or gate the
colour-pass latch the same way. Note the convention comment above it: **row-vector
`v*M`**, and GLSL reads the raw bytes as the transpose. `System.Numerics` is row-vector too, so
`Vector4.Transform(new Vector4(world,1), vp)` is the right call — the precedent is
`ShadowMath.WorldToLightNdc` (`managed/Alloy/Core/ShadowMath.cs:197-201`):

```csharp
public static Vector3 WorldToLightNdc(Matrix4x4 lightViewProj, Vector3 world)
{
    Vector4 c = Vector4.Transform(new Vector4(world, 1f), lightViewProj);
    return c.W != 0f ? new Vector3(c.X, c.Y, c.Z) / c.W : new Vector3(c.X, c.Y, c.Z);
}
```

**Option B — reconstruct from `FrameInputs` (no Alloy edit).**
`managed/Alloy/Core/FrameInputs.cs:20-45` is already latched once per world frame by
`FrameRead.Tick` at the frame anchor (`AlloyRenderer.FrameAnchor`):

```csharp
public static bool HasCamera;
public static Vector3 CameraPos;      // *CurrentCamera + 0xb0
public static Quaternion CameraRot;   // +0xbc, re-ordered to Numerics (x,y,z,w)
public static bool HasPerspective;
public static float CamFovy, CamAspect, CamNear, CamFar;   // degrees / vpW÷vpH / near / far
public static float CamViewW, CamViewH;
```

Sketch (verify signs live):

```csharp
if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return false;
Matrix4x4 camToWorld = Matrix4x4.CreateFromQuaternion(FrameInputs.CameraRot)
                     * Matrix4x4.CreateTranslation(FrameInputs.CameraPos);
if (!Matrix4x4.Invert(camToWorld, out Matrix4x4 view)) return false;
Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
    FrameInputs.CamFovy * (MathF.PI / 180f), FrameInputs.CamAspect,
    FrameInputs.CamNear, FrameInputs.CamFar);
Vector4 c = Vector4.Transform(new Vector4(world, 1f), view * proj);
if (c.W <= 0f) return false;                          // behind the camera
float sx = ( c.X / c.W * 0.5f + 0.5f) * TextRig.ViewportW;
float sy = (-c.Y / c.W * 0.5f + 0.5f) * TextRig.ViewportH;   // ortho is y-down
```

Caveats for Option B: `CamAspect` is `CamViewW/CamViewH`, the **camera's own** viewport, which
`FrameInputs.cs:43-45` explicitly says is not the GL viewport (the engine upscales both by the same
factor) — same ratio, so it should be fine, but check. And `Matrix4x4.CreatePerspectiveFieldOfView`'s
clip-z range differs from the engine's; irrelevant for x/y, relevant if you ever use `c.Z`.

**Option A is strongly preferred** — it is the matrix the world was actually drawn with.

### The `alloy.enabled=0` hole

`AlloyRenderer.Initialize` returns at the opt-out **before** anything that would latch a camera
(since CX-89 the anchor is a direct call from the shim, so it installs no `FadeDoors` hook at all):

```csharp
if (AlloyGate.OptedOut)
{ log.Info("AlloyRenderer: stood down (alloy.enabled=0) -- the engine owns every resource this session"); return; }
```

So under `alloy.enabled=0` in `cx.cfg`, `FrameRead.Tick` never runs and `FrameInputs` stays empty —
**neither option A nor B has a camera.**

The clean split, and it costs nothing: discovery is **name-sorted**, and `Alloy…` sorts before
`Nameplate…`, so `AlloyGate.OptedOut` is already decided by the time `NameplateService.Initialize` runs.
Branch on it:

```csharp
if (AlloyGate.OptedOut)
    _origFade = (delegate* unmanaged<IntPtr, void>)hooks.Install(   // no collision: AlloyRenderer
        SymbolMap.FadeDoors,                                        // returned before its own Install
        (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnWorldFrame);  // our own camera latch
// else: Alloy is arming; read FrameInputs / the published SceneVp instead.
```

The `FadeDoors` symbol is genuinely free since CX-89 — CX hooks it on no path — so a plain `Install` is
correct here, no `Chain`,
and no need to touch `AlloyRenderer`. The one-service alternative (nameplates simply require Alloy, with
a logged stand-down line like every other gated service) is also acceptable; pick one and say so in the
init log.

---

## 4. Animation

Two models exist. Both are frame-rate independent and take **`Environment.TickCount64`** — there is no
framework delta-time service. Copy one, don't invent a third.

### Model A — exponential decay toward 0 (chat's slide/scroll)

`managed/Services/Chat/Core/Window/ChatAnim.cs:62-75`:

```csharp
/// <summary>Advance the residual to <paramref name="nowMs"/> (monotonic ms) and return it. Called once
/// per frame in the hot draw. <paramref name="tauMs"/> comes from the focused window (#41).</summary>
public static float Tick(long nowMs, float tauMs = 70f)
{
    double tau = tauMs / 1000.0 <= 0 ? 0.001 : tauMs / 1000.0;
    if (!_primed) { _primed = true; _lastTickMs = nowMs; return Residual; }
    double dt = (nowMs - _lastTickMs) / 1000.0;
    _lastTickMs = nowMs;
    if (Residual != 0f)
    {
        Residual *= (float)Math.Exp(-dt / tau);
        if (Residual < SnapPx && Residual > -SnapPx) Residual = 0f;
    }
    return Residual;
}
```

Call site — the dt source, `managed/Services/Chat/Render/ChatRenderer.cs:175`:

```csharp
float globalResidual = ChatAnim.Tick(System.Environment.TickCount64, focusTau);
```

### Model B — hold-then-linear-fade alpha (chat line fade)

`managed/Services/Chat/Core/Window/ChatAnim.cs:81-90`:

```csharp
public static float FadeAlpha(DateTime now, DateTime ts, DateTime lastScroll,
                              bool enabled, double holdSec, double fadeSec)
{
    if (!enabled) return 1f;
    DateTime from = ts > lastScroll ? ts : lastScroll;
    double age = (now - from).TotalSeconds;
    if (age <= holdSec) return 1f;
    if (age >= holdSec + fadeSec) return 0f;
    return (float)(1.0 - (age - holdSec) / fadeSec);
}
```

**This is the chat bubble's decay curve, essentially free.** A bubble that appears, holds N seconds, and
fades over M is exactly `FadeAlpha`, with the per-bubble `DateTime` as `ts`.

### Model C — linear fade + exponential size ease (Dialog)

`managed/Services/Dialog/DialogService.cs:709-743`:

```csharp
private static void AdvanceAnim(long nowMs, float bodyTarget)
{
    ...
    if (!_animPrimed) { _animPrimed = true; _animClockMs = nowMs; _bodyHPub = _bodyH; return; }
    long dtMs = nowMs - _animClockMs; _animClockMs = nowMs;
    if (dtMs > 0)
    {
        float sIn  = _fadeInMs  > 1f ? dtMs / _fadeInMs  : 1f;
        float sOut = _fadeOutMs > 1f ? dtMs / _fadeOutMs : 1f;
        MoveToward(ref _windowAlpha,  winTarget,     winTarget >= _windowAlpha ? sIn : sOut);
        MoveToward(ref _contentAlpha, contentTarget, contentTarget >= _contentAlpha ? sIn : sOut);
        float tau = System.Math.Max(20f, _fadeInMs * 0.4f);
        float k = 1f - (float)System.Math.Exp(-dtMs / tau);
        _bodyH += (bodyTarget - _bodyH) * k;
        if (System.Math.Abs(bodyTarget - _bodyH) < 0.5f) _bodyH = bodyTarget;
    }
    _bodyHPub = _bodyH;
}

private static void MoveToward(ref float v, float target, float step)
{
    if (v < target) { v += step; if (v > target) v = target; }
    else if (v > target) { v -= step; if (v < target) v = target; }
}
```

Called from the draw with `AdvanceAnim(Environment.TickCount64, bodyTarget)`
(`DialogService.cs:764`).

**Recommendation for nameplates:** Model C's `MoveToward` for the hover fade-in/out (targets 0/1 with
per-direction ms knobs), Model B's `FadeAlpha` for the bubble's hold+decay. Both are pure math, both
self-check off device — put a `NameplateAnim.SelfCheck` beside `ChatAnim.SelfCheck` (§11).

---

## 5. Config and knobs (ADR 0013 — superseded by ADR 0025, see the status note at the top)

**Two buckets, and env vars are not one.** Note the exact split — the task framing slightly conflates them:

- **`cx.cfg`** (`managed/Framework/Settings.cs`) is the **boot** config: hand-edited, **never written
  back**, read pre-main via `Settings.Bool/Int/Float/Text`, and **it is what `/knobs` lists**. Only put a
  boot gate here (e.g. `nameplates=0`).
- **`cx-<feature>.cfg`** (`managed/Framework/ConfigFile.cs`) is a per-feature **preference** file the
  feature reads and writes. It does **not** appear in `/knobs`.
- **Chat commands** (`managed/Framework/CommandRegistry.cs`) are bucket 1: anything you tune while
  looking at it. These DO appear in `/knobs`.

### (a) The `/nameplate` command — end to end

Registration (`managed/Framework/CommandRegistry.cs:43`):

```csharp
public bool Register(string name, string help, Handler run, params string[] aliases)
// where: public delegate string? Handler(string args);
```

Real example, `managed/Services/Quickbar/QuickbarService.cs:77-81`:

```csharp
CommandRegistry.Global.Register("/settings", "toggle the quickbar settings panel",
    _ => { ToggleSettings(); return null; });
CommandRegistry.Global.Register("/compass", "toggle the compass", _ => { ToggleCompass(); return null; });
```

Dispatch semantics (`CommandRegistry.cs:63-75`): the whole line is `ToLowerInvariant()`'d — **arguments
included** — split at the first space; the handler gets the trimmed remainder; returning a non-null
string echoes it to the chat box; first registration of a name wins and a collision is refused + logged.

Use a plain `Register` lambda. Do **not** copy `RenderCommands.Tune` (`managed/Alloy/Core/RenderCommands.cs:83`)
— that rides `PassRegistry.PassLifecycle`, which is Alloy-only.

```csharp
private static string? Cmd(string args)
{
    string[] a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (a.Length == 0)
        return $"[nameplate] on={_cfg.On} bubbles={_cfg.Bubbles} hold={_cfg.HoldSec:F1}s "
             + $"fade={_cfg.FadeSec:F1}s width={_cfg.MaxWidth}";
    switch (a[0])
    {
        case "on":      _cfg.On = a.Length > 1 && a[1] != "0"; break;
        case "bubbles": _cfg.Bubbles = a.Length > 1 && a[1] != "0"; break;
        case "hold":    if (a.Length > 1 && float.TryParse(a[1], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float h)) _cfg.HoldSec = h; break;
        default: return "[nameplate] usage: /nameplate | /nameplate <on|bubbles|hold|fade|width> <value>";
    }
    SaveConfig();
    return $"[nameplate] {a[0]} -> ok";
}
```

Note `CultureInfo.InvariantCulture` — `Settings.Float` documents that locale-sensitive parsing silently
dropped dot-decimals under comma-decimal desktops.

### (b) `cx-nameplate.cfg` — the persisted preferences

The whole pattern is `QuickbarService` + `QuickbarConfig`.

Owner fields, `managed/Services/Quickbar/QuickbarService.cs:47-49`:

```csharp
private static QuickbarConfig _config = new();
private static readonly ConfigFile _file = new("cx-quickbar.cfg");
private static bool _configLoaded;
```

Deferred load — **must not be called from `Initialize`** (`QuickbarService.cs:239-246`):

```csharp
/// <summary>Restore the settings. A no-op that stays retryable until <see cref="ConfigFile"/>'s deferral
/// gate opens (our loader runs pre-main), so the callers below just keep asking.</summary>
private static void LoadConfig()
{
    if (!_file.Load(_log, ApplyText)) return;
    _configLoaded = true;
    _file.Baseline(_config.ToText());   // a load must not provoke a write of what is already on disk
    _dirty = true;
}

private static void ApplyText(string text) => _config = QuickbarConfig.Parse(text);
private static void SaveConfig() => _file.Save(_config.ToText(), _log);
```

Call `LoadConfig()` from the **first frame** (top of `DrawContent`), guarded by `_configLoaded`.
`ConfigFile.Load` returns false and stays retryable until the engine user dir resolves
(`managed/Framework/ConfigFile.cs:45`); `Save` writes only on change (`ConfigFile.cs:61`).

The config type — copy `managed/Services/Quickbar/QuickbarConfig.cs` verbatim in shape:
`ToText()` producing `key=value\n` lines, `static Parse(string)` using
`ConfigFile.ForEachPair(text, (key,val) => switch …)` (`ConfigFile.cs:100`), plus a
`SelfCheck(ExtenderLog)` asserting round-trip **and** defaults. That SelfCheck is your only test (§11).

### (c) Boot gate in `cx.cfg`, if you need one

`managed/Framework/Settings.cs`:

```csharp
public static bool  Bool(string key, bool fallback);
public static int   Int(string key, int fallback, int min = int.MinValue, int max = int.MaxValue);
public static float Float(string key, float fallback, float min = float.MinValue, float max = float.MaxValue);
public static string Text(string key, string fallback);
```

Every read is recorded and printed by `/knobs` (`managed/Framework/KnobSurface.cs:50`).

---

## 6. Settings UI

`SettingsPanel` (`managed/Ui/SettingsPanel.cs`) owns position, title drag, close button, metrics,
hit-test-to-row, slider grab, inline numeric entry, and the draw. The **consumer** owns only the row
list and what a press does.

Row model, `managed/Ui/SetRow.cs`:

```csharp
public sealed class SetRow
{
    public SetKind Kind;      // Toggle | Cycle | Slider | Swatch | Button
    public SetAct  Act;       // the consumer's vocabulary; the panel never interprets it
    public string  Label = "";
    public float   Top, Height;
    public bool    On;        // Toggle
    public string  Value = "";// Cycle current text
    public float   Norm;      // Slider fill 0..1
    public uint    Rgba;      // Swatch colour
    public uint    Bit;       // ToggleBit discriminator
    public string  Key = "";
    public int     Channel = -1;
    public float   Min, Max;  // Slider range
}
```

`SetKind` is closed (`managed/Ui/SetKind.cs`). **`SetAct` is a shared enum across all three consumers**
(`managed/Ui/SetAct.cs`) — adding nameplate rows means adding members there (its own doc comment calls
that out as the ponytail ceiling). Reuse `ToggleBit` + a `Bit` discriminator where you can, which needs
no new `SetAct` member at all.

The consumer wires four callbacks and a layout pass. Minimal template —
`managed/Services/Quickbar/QuickbarSettings.cs:24-31`:

```csharp
public QuickbarSettings()
{
    Panel = new SettingsPanel(PanelW)   // PanelW = design px, scaled by Metrics.Scale internally
    {
        Relayout   = () => { if (_cfg != null) Layout(_cfg); },
        RowPressed = OnRowPressed,
        // ApplyValue / CommitLabel stay null when there are no sliders / text rows
    };
}
```

Layout pass — build a fresh `List<SetRow>` and `Publish` it
(`QuickbarSettings.cs:54-101`, abridged):

```csharp
public void Layout(NameplateConfig cfg)
{
    _cfg = cfg;
    var rows = new List<SetRow>();
    float y = Panel.ContentTop;
    float RowH = SettingsPanel.RowH;

    rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Nameplates",
                          On = cfg.On, Top = y, Height = RowH, Bit = BitOn });            y += RowH;
    rows.Add(new SetRow { Kind = SetKind.Toggle, Act = SetAct.ToggleBit, Label = "Chat bubbles",
                          On = cfg.Bubbles, Top = y, Height = RowH, Bit = BitBubbles });  y += RowH;
    rows.Add(new SetRow { Kind = SetKind.Slider, Act = SetAct.SliderHold, Label = "Bubble hold",
                          Norm = (cfg.HoldSec - 1f) / 29f, Min = 1f, Max = 30f,
                          Top = y, Height = RowH });                                      y += RowH;

    Panel.Publish(rows, y);   // panel adds its bottom pad -> H
}
```

Press handler (`QuickbarSettings.cs:128-152`) is a `switch (row.Act)` then `switch (row.Bit)`.
Slider rows never reach `RowPressed` — the panel owns the track drag and the value box; wire
`ApplyValue = (row, v) => { … }` for those (`SettingsPanel.cs:92-94`).

Open/close (`QuickbarSettings.cs:104-113`): `Panel.BeginOpen(); Layout(cfg); X = (vpW - W)/2f; Y = (vpH - H)/2f; Layout(cfg);`.

Draw (`QuickbarService.EmitSettingsPanel`, `QuickbarService.cs:330-341`):

```csharp
_settings.Panel.Emit(b, atlas, atlas.LineHeight, new SettingsPanel.Palette
{
    Body     = (0.02f, 0.02f, 0.03f, 1f),
    TitleBar = (0.10f, 0.12f, 0.16f, 1f),
    Active   = (0.20f, 0.34f, 0.52f, 0.95f),
    TextRgb  = 0xd8dee9u,
});
```

Input routing: forward the window's `OnMouseButton`/`OnMouseMotion` to `Panel.MouseDown/MouseMove/MouseUp`
and use `Panel.Contains` in `HitTestOverride` (`QuickbarService.cs:344-401`). Add
`NameplateSettings.SelfCheck` mirroring `QuickbarSettings.SelfCheck` (`QuickbarSettings.cs:161+`).

---

## 7. Hot-path rules

The rule (CLAUDE.md, and `ADR 0004` "Consequences"): **the per-frame path is a hard zero-alloc
invariant** — it runs inside the engine's `SwapBuffers`, and NativeAOT's GC runs on whatever
thread the hook fired on, so churn there is a visible stutter.

**May not** happen in `DrawContent`: `new` of anything, `List<T>` growth, string concatenation/interpolation,
`$"..."` log lines, boxing, LINQ, lambdas that capture, `Split`, `ToString`, `Array.Resize` in the steady state.

**How existing code avoids it:**

- **Reused, grow-once buffers.** `managed/Services/Dialog/DialogService.cs:834-840`:
  ```csharp
  private static float[] _chrome = new float[1024];
  private static void EmitRect(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                               (float r, float g, float b, float a) c)
  {
      if (n + 6 * VertexBatch.Floats > _chrome.Length)
          System.Array.Resize(ref _chrome, _chrome.Length * 2);
      ...
  }
  ```
  Doubling is a guard, not a path — it settles after a few frames.
- **Pooled `VertexBatch`** whose internal `List<float>` keeps capacity across rebuilds
  (`QuickbarService.cs:294-296`: `VertexBatch b = _bandBatch ??= new VertexBatch(0,0); b.Reset(atlas.SolidU, atlas.SolidV);`;
  `VertexBatch.Reset` doc, `managed/Ui/VertexBatch.cs:26`).
- **`stackalloc`** for small per-frame arrays: `TextRig.DrawBatch` (`int* vp = stackalloc int[4]`,
  `float* m = stackalloc float[16]`), `ChatRenderer.DrawPortraits` (`int* tids`, `float* verts`),
  `TextRig.ResolveTexture` (`Span<byte> name = stackalloc byte[17]`).
- **`readonly struct`s, not classes**, for value payloads: `HitRegion`, `Glyph`, `PortraitQuad`,
  `TextWrapper.LineSpan`, `Settings.Known`.
- **Cached delegates**: `GlyphAtlas.AdvanceOfCached` (`managed/Ui/GlyphAtlas.cs:36-38`) exists purely so
  the wrap call doesn't allocate a `Func<Rune,float>` per call.
- **Zero-alloc registry walks**: `UiHost.NextBelow` / `DrawFrame` are deliberately O(n²) min-scans with
  no sort buffer, no LINQ, no closures (`managed/Ui/UiHost.cs:41-48` + `DrawFrame` docs).

**The nameplate-specific trap the existing code doesn't cover:** *strings*. Marshalling a creature's
`CExoString` name (or `CClientExoApp::GetObjectName`) per frame allocates a `string` per plate per frame.
Names, portrait resrefs and bubble text must be **resolved cold** (on the hover edge, on the bubble hook,
on the highlight-set change) into a per-plate record and only re-read when a generation counter changes.
The per-frame emit then walks cached `string`s — `s.EnumerateRunes()` is a struct enumerator and is
alloc-free, as is `foreach (Rune)`.

Second trap: **`TextWrapper.WrapRunes` allocates a `List<LineSpan>` per call** — see §14.

Log lines are the other one: `_log?.Info($"...")` allocates. Existing hot paths gate them behind a
one-shot (`_portraitDrew`, `_announced`) or an `n % LogEveryNth` counter.

---

## 8. Text: fonts, sizes, outline, colour runs

### Font + atlas

`TextRig.Init` (`managed/Ui/TextRig.cs`) reads DejaVuSans out of the image (an embedded resource,
no font search) and bakes a single-channel (R8) atlas with the client's own `stb_truetype` exports. Ranges baked (`TextRig.cs:44`): Basic Latin, Latin-1 Supplement,
Latin Extended-A, Cyrillic, plus ▪ ▾ ⚙. **No CJK.**

The atlas is baked at the **effective** px (`font size × game UI scale`) and re-baked cold by
`TextRig.SetFont(px)` (`TextRig.cs:140`), which bumps `TextRig.AtlasEpoch`. A consumer caching
vertex data must invalidate on that epoch — the precedent is
`ChatRenderer.DrawOverlay` (`ChatRenderer.cs:384-388`): `if (_attrVerts == null || _attrEpoch != TextRig.AtlasEpoch) { … }`.
Bounds: `MinFontPx = 10f`, `MaxFontPx = 32f`, `AtlasMaxPx = 96f`.

The atlas's bottom-right texel is forced opaque white with `SolidU/SolidV` pointing at it
(`GlyphAtlas.cs:139-142`), which is why chrome rects and glyphs share one texture and one draw call.

### Outlined text — **yes, this already exists**

`managed/Services/Chat/Render/WindowRenderer.cs:41-42`:

```csharp
/// <summary>8-way (±1px) offsets for the outline halo, as flat (dx,dy) pairs.</summary>
private static readonly float[] OutlineOffsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };
```

and the emit, `WindowRenderer.cs:636-649`:

```csharp
float penX = startX;
for (int k = span.Start; k < span.End; k++)
{
    Rune r = runes[k];
    if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
    uint c = cols[k];
    if (outline)
        for (int o = 0; o < OutlineOffsets.Length; o += 2)
            b.Glyph(g, penX + OutlineOffsets[o], baseline + OutlineOffsets[o + 1], 0f, 0f, 0f, 1f);
    b.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
    penX += g.Advance;
    if (r.Value == ' ') penX += spaceExtra;
}
```

**This is the nameplate readability answer, and it is exactly the right tool** — 9× the glyph quads for
outlined text, which is fine at nameplate text volumes. It is per-window opt-in in chat
(`ChatConfig` key `outline`, `SetAct.OutlineOn`); nameplates should default it **on**.
There is no separate drop-shadow path; a shadow is the same loop with one offset instead of eight.

### Colour runs

`ColorRun` (`managed/Services/Chat/Core/Model/ColorRun.cs`) + `ColorRunParser.Parse(string)`
(`ColorRunParser.cs:22`) turn the Aurora inline grammar (`<c` + 3 raw RGB bytes + `>` … `</c>`,
16-deep nesting stack) into runs, **once at ingest** — it allocates, and it is deliberately cold.
A chat bubble showing server-coloured text should reuse it at the same cold moment (the bubble hook),
never per frame.

Colours elsewhere are `uint` `0xRRGGBB` unpacked inline
(`((rgb >> 16) & 0xFF) / 255f`, `SettingsPanel.cs:392`) or `(float r,g,b,a)` tuples
(`DialogService.cs` chrome constants). There is no `Color` type.

---

## 9. Textures — how a UI quad gets a texture

### The API

`managed/Ui/TextRig.cs:455`:

```csharp
public static void DrawPortrait(string resref, float x, float y, float w, float h, float alpha = 1f,
                                bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
```

`public const float PortraitAspect = 0.64f;` (`managed/Ui/TextRig.cs:497`) — size the rect to that or it distorts.

It records a command of its own kind, carrying the engine texture id the resref resolved to and six
vertices, so the replay binds that texture for the run and the text commands around it are unaffected.

### The lifetime hazard, and the guard that already exists

The engine's `CAurTexture*` is **borrowed** and freed on **area transitions AND module unload**.
`AurTextureGetTID` dereferences the object's vtable and calls through slot 0x18 — a freed pointer is a
SIGSEGV, not a wrong texture.

Rule 1 (#33), `TextRig.ResolveTid` (`managed/Ui/TextRig.cs:172`): **never cache the pointer, re-fetch
per frame** via a resref-keyed `AurTextureGet`.

Rule 2 (#138, `managed/Framework/AurTex.cs`): re-fetching is **not enough**, because
`Material::GetTexture` is a plain field read and returns whatever the material was left holding. So the
pointer is **checked**, not trusted — the engine has exactly two texture vtables, so "is this still a
texture" is two compares:

```csharp
private static bool Live(IntPtr tex)
{
    if (!Ready || tex == IntPtr.Zero) return false;
    IntPtr vt = *(IntPtr*)tex;
    if (vt == _vtbl || vt == _vtblBasic) return true;
    _refused++;
    return false;
}

/// <summary>This texture's GL id, or 0 if the pointer is null or no longer a texture. Hot ...</summary>
public static int Tid(IntPtr tex) => Live(tex) ? _getTid(tex) : 0;
```

`_vtbl`/`_vtblBasic` are `Native.Sym(_ZTV11CAurTexture) + 0x10` (Itanium ABI: the symbol points at the
RTTI pair). `AurTex.Refused` is the counter to watch. `AurTex.TidByName(resref)` (`AurTex.cs:81-89`) does
resolve + guard in one call and caches nothing — that is the front door for a nameplate portrait.

**Use `TextRig.ResolveTid` / `TextRig.DrawPortrait`, never a stored `CAurTexture*`.**

### Batching N portraits

`DrawPortrait` is one command per call — fine for one dialog portrait, wasteful for N plates.
The batched precedent is `ChatRenderer.DrawPortraits` (`managed/Services/Chat/Render/ChatRenderer.cs`):
resolve **every** TID and build **all** geometry into a single `stackalloc` buffer first, then record the
run, so the replay changes texture once per portrait and uploads once for the batch. It also caps at
`MaxPortraitsPerFrame` and logs once when the cap bites. Copy that shape.

A texture id of 0 means "engine hasn't uploaded it yet" — skip that plate this frame, don't error.

---

## 10. Input

### Routing

`UiInputService` (`managed/Ui/UiInputService.cs`) captures raw SDL events and feeds `UiHost`, which walks
windows topmost-first and stops at the first consumer:

```csharp
public static bool DispatchMouseButton(bool down, int button, int x, int y)   // two passes: modal, then rest
public static bool DispatchMouseMotion(int x, int y)
public static bool DispatchMouseWheel(int dy)
```

Coordinates are the **raw SDL event fields** — `*(int*)(ev + 0x14)` / `+ 0x18`
(`UiInputService.cs:32, 191, 207, 219`), passed through unscaled. `Metrics.Scale` is never applied on the
input side. So the hit-test space is SDL window pixels; the draw space is the frame's viewport
(`TextRig.ViewportW/H`). These agree on a normal desktop; on HiDPI where SDL logical ≠ drawable pixels they
would not. Every existing consumer assumes they agree — if a plate hit-test ever feels offset on a
scaled display, this is the first thing to check.

### Hover

There is **no hover callback**. `OnMouseMotion` is the hover source: consume nothing (`return false`)
and just record the last pointer position, then compute the hovered plate at draw time. `HoverToFocus`
is a framework focus mode (`UiHost.HoverToFocus`), unrelated.

### Non-rectangular / world-anchored participation — yes

`Window.HitTestOverride` (`managed/Ui/Window.cs:50-52`) is exactly the mechanism:

```csharp
/// <summary>Overrides the rect hit-test for cursor pick + click routing: true when the point is over this
/// window's live UI ... Null falls back to <see cref="Contains"/>. Zero-alloc: read on the render
/// thread by the cursor pick and on the input thread by focus routing.</summary>
public Func<float, float, bool>? HitTestOverride;
```

Both Chat and Dialog use it (`ChatService.cs:552`, `DialogService.cs:158`) precisely because their live UI
extends past any single rect. For nameplates:

- keep a **cold-swapped immutable array** of `(rect, objectId)` — the render thread writes a new array,
  the input thread reads whichever reference it sees. Never mutate one in place; the `UiHost` header
  spells out why a mid-growth list read on the cursor thread is a real crash.
- `HitTestOverride` scans that array; `OnMouseButton` scans it and dispatches on the object id.
- Set `Focusable = false` — the plate window should never steal focus or the keyboard.
- Set `Draggable = false`, `Resizable = false` (defaults), so no built-in chrome cursor zones fire.
- Do **not** add per-plate `Window.Regions` entries: `Regions` is `readonly List` with a standing
  never-mutate-live warning, so a per-plate `CursorKind.Link` is **not reachable today**. Getting a link
  cursor over a plate needs a framework change (a `Func<float,float,CursorKind>?` override alongside
  `HitTestOverride` would be the minimal one).

Cursor pick reads `Window.CursorAt` via `UiHost.PickCursor` (`managed/Ui/UiHost.cs:161`).

### Keyboard (Tab-held highlight-all)

Nameplates shown "while Tab is held" is a **key-held** state, and `UiHost.DispatchKey` is press-only —
there is no key-up route to windows. Two options, in preference order:

1. Read the engine's own highlight set. `AlloyRenderer.OnRefreshHilite`
   (`managed/Alloy/AlloyRenderer.Highlight.cs:69-95`) already hooks `CNWCAnimBase::RefreshHilite` and
   maintains a small per-highlighted-object table keyed on the winning hilite **channel** — channel 2 is
   `HiliteAreaObjects` (the Tab / mouseover path), channel 3 is VFX. **This is the correct source: the
   engine tells you what is highlighted, so you never track Tab yourself.** Read that set (it needs a
   small public accessor added) instead of watching keys.
2. If Alloy is stood down, an `IChordConsumer` (`UiHost.cs:22-33`) sees fallen-through key presses only —
   not releases — so a held-key state would need a timeout. Prefer option 1.

---

## 11. Build / verify loop

```sh
./build.sh          # .NET 10 SDK, clang/gcc, ld.lld, zlib, cmake; stages cx-linux-x64/
./run-demo.sh       # boots the framework inside /usr/bin/env — no game, no X11, no GL
./sym.sh [-m] <pat> # symbol search; -m = mangled, for HookService.Install
```

`build.sh` still greps `managed/Alloy/Scene/` for `Gl.` without a `gl-ok:` marker, and the broader rule is
the compiler's now: CX0003 (`tools/analyzers/EngineGl.cs`) confines GL to a named list of files, since Alloy
is Vulkan-only. A UI service draws by recording into `TextRig`'s list, so neither should come up.

### What `run-demo.sh` can and cannot verify

**There are no unit tests, no test project, no test runner.** The entire automated surface is:

```sh
if ! grep -q 'SelfCheck' "$LOG"; then
  echo ">> FAIL: no SelfCheck lines in $LOG -- services never ran"; exit 1
fi
if grep -qE 'SelfCheck.*FAIL' "$LOG"; then
  echo ">> FAIL: a SelfCheck suite failed:"; …; exit 1
fi
```

**Can verify:** the managed lib loads under NativeAOT, discovery constructs and initialises your service,
hook staging doesn't throw, and every `SelfCheck` you write passes. `UiHost.SelfCheck` even exercises the
window registry, z-order, hit-test, cursor pick, draw roll-call and input precedence off-device —
including a hermetic save/restore of the live registry.

**Cannot verify:** anything that needs a render device, a font (`GlyphAtlas.Bake` returns null headless —
"stbtt exports unresolved"), an engine symbol, a camera, a texture, or a pixel. Layout, projection maths
and look are live-only.

**So: every piece of pure logic you write gets a `SelfCheck(ExtenderLog log)`** — the config round-trip,
the animation curves, the world→screen maths (with a synthetic camera), the plate layout, the settings
rows. That is both the test suite and the CI gate. Pattern
(`managed/Services/Quickbar/QuickbarConfig.cs:52+`):

```csharp
public static void SelfCheck(ExtenderLog log)
{
    bool ok = true;
    void E(bool c, string w) { if (!c) { ok = false; log.Info($"NameplateConfig.SelfCheck FAIL: {w}"); } }
    …
    log.Info(ok ? "NameplateConfig.SelfCheck: passed" : "NameplateConfig.SelfCheck: FAILED");
}
```

Call it from `Initialize`, **before** any stand-down return, so a headless run still exercises it.

### Live verification

```sh
NWN_BIN=/path/to/bin/linux-x86/nwmain-linux ./cx-linux-x64/cx-launch
```

The log is `$HOME/cx.log`. Strings to grep:

| grep | means |
|---|---|
| `first frame presented -- hook is LIVE` | `UiHostService` owns the present |
| `window roll-call straddles NUI` | your `DrawContent` is called by `Layer`: below NUI before nuklear's flush, above NUI after it (Alloy on: around NUI's captured layers) |
| `TextRig: atlas ready` / `atlas uploaded as GL tex` | fonts are up |
| `[uigate] win=... -> inGame=True inModule=True` | you're in a module and the gate is open |
| `SelfCheck: passed` / `FAILED` | your logic checks |
| `rendering: ALLOY` (`/vanilla`) | whether `FrameInputs` has a camera at all (§3) |
| `NameplateService: ...` | your own stand-down / arm lines — write them |

Every service logs a one-line verdict at init saying what it installed or why it stood down. Copy that.

`/knobs` in-game prints every registered command and every `cx.cfg` setting plus the file path it read.

---

## 12. Where inbound chat arrives, and the right seam for a bubble

### The existing chat tap — and why it is the WRONG seam for bubbles

`ChatService.InitMessageCore` (`managed/Services/Chat/ChatService.cs:84-95`) hooks
`CGuiInGameChatWindow::AppendToMsgBuffer`:

```csharp
// AppendToMsgBuffer(this, CExoString* text, uint channel, uint playerId, int hasPortrait, CResRef).
private static delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, CResRef16, void> _tapOrig;
```

(symbol `SymbolMap.AppendToMsgBuffer`, `SymbolMap.cs:89` for its sibling `AppendToChatBuffer`).
The parsed model is `ChatLine` (`managed/Services/Chat/Core/Model/ChatLine.cs`), immutable:

```csharp
public uint Channel { get; }
public string SenderName { get; }      // "" — the tap does not carry it; it's inline in RawText
public uint PlayerId { get; }          // PLAYER id, not an object id
public string PortraitResref { get; }
public ColorRun[] ColorRuns { get; }
public DateTime Timestamp { get; }
public GameStamp GameTime { get; }
public string RawText { get; }
public string TellRecipient { get; }
```

**The clean second-consumer seam already exists** — `ChatStore` is an observer list, not a window
(`managed/Services/Chat/Core/Model/ChatStore.cs:28-38`):

```csharp
/// <summary>Register an ingest observer, invoked (in add order) for every <see cref="Append"/>.</summary>
public void AddObserver(Action<ChatLine> observer) => _observers.Add(observer);
```

`ChatService` itself subscribes this way (`ChatService.cs:534`: `_store.AddObserver(OnIngest);`). A second
consumer subscribes identically and never touches `ChatWindow`. **But** the store is a private field of
`ChatService`, so this needs a small accessor (`public static void AddChatObserver(Action<ChatLine>)`) —
a two-line addition, the shape the codebase already uses for `Notice.Sink` (`ChatService.cs:536`).

**The blocker: `ChatLine` carries no game object id.** `PlayerId` is a *player* id (0/invalid for NPCs),
`SenderName` is `""` (the ponytail comment at `ChatService.cs:110` says so — the name is inline in
`RawText`). **You cannot join a `ChatLine` to a world position.**

### The right seam for bubbles: the engine's own bubble path

`./sym.sh Bubble` turns up the client's per-object bubble API, and `this` **is** the speaking object:

```
00000000007e8050 T CNWCObject::SetPopUpBubbleText(CExoString, int, float, float)
00000000007e8000 T CNWCObject::SetPopUpBubblePortrait(CResRef)
00000000007e86e0 T CNWCObject::SetPopUpBubbleDefaultText(CExoString)
00000000007e8880 T CNWCObject::SetPopUpBubbleDefaultCreatureID(unsigned int)
00000000007e8250 T CNWCObject::ShowPopUpBubble(Vector)
00000000007e7ed0 T CNWCObject::HidePopUpBubble()
00000000007e7fd0 T CNWCObject::KillPopUpBubble()
00000000007e8330 T CNWCObject::UpdatePopUpBubble()
00000000007275c0 T CNWCCreature::SetPopUpBubbleDecayTime(float)
0000000000719c70 W CNWCObject::ShowFeedbackBubbleText()
000000000072e600 T CNWCCreature::UpdatePopUpBubble()
000000000075b280 T CNWCMessage::HandleServerToPlayerChat(unsigned char)
00000000018aae04 D g_optHideTextBubbles          # the vanilla-suppression switch
00000000018aa7a0 D g_pBubbleModels
```

**Recommended:** hook `CNWCObject::SetPopUpBubbleText` (after-original, cold — it fires on a speech event,
never per frame). You get:
- `this` = `CNWCObject*` — **the speaking object**, from which you read its **object id** (see the rule
  below), which is the join key to its world position *and* to the nameplate for the same creature
  (they share an anchor);
- the `CExoString` text, decoded with `Native.ReadExo` (CX-701), then
  `ColorRunParser.Parse` for colours;
- `SetPopUpBubblePortrait(CResRef)` on the same object for the portrait resref.

### THE JOIN KEY IS AN ID, NOT A POINTER

**Never store a `CNWCObject*` across frames.** It is a borrowed pointer with exactly the lifetime
hazard #33/#138/#145 were spent learning (§9): the creature despawns, the object is freed and the heap
recycles it, and your next per-frame position read walks garbage — a SIGSEGV, not a wrong plate. The
whole `AurTex` vtable guard exists because "re-fetch per frame" alone was not enough for textures; a
plate table holding object pointers is the same bug with no guard at all.

The rule:

1. **At the hook (cold):** read the object's **id** out of `this` and store only that `uint`, plus the
   cold-resolved strings (name, bubble text, portrait resref) and the timestamp. *Open RE item: which
   `CGameObject` field holds the id — confirm in Ghidra.*
2. **Every frame (hot):** `CClientExoApp::GetGameObject(id)` (`SymbolMap.cs:113`, already used by
   `DialogService` for its speaker) → a **live** pointer or null. Read the world position off that
   pointer, immediately, and let it go.
3. **Null means retire.** A `GetGameObject` miss is the despawn signal — drop the plate/bubble, don't
   guess. That is the plate table's whole eviction rule.

The same id is what `CClientExoApp::GetObjectName(uint, CExoString*)` (`SymbolMap.cs:118`) takes, so the
name path needs no second key.

Suppress the vanilla bubble by writing `g_optHideTextBubbles`, or by skip-originaling
`ShowPopUpBubble`/`UpdatePopUpBubble` — the exact same technique `ChatService.InitSuppression`
(`ChatService.cs:651-658`) uses for the vanilla chat window. **Check in Ghidra which one is safe:** if
`g_optHideTextBubbles` gates the *call* to `SetPopUpBubbleText` rather than only the draw, flipping it
silences your own hook, and skip-original on `ShowPopUpBubble`/`UpdatePopUpBubble` is then the only
suppression that leaves your text source intact.

**Confirm every one of these in Ghidra before writing the handler** — signatures, the by-value
`CExoString`/`CResRef` lowering (see the `CResRef16` trap, `ChatService.cs:74-78` and
`docs/hook-pattern.md`), and where `CNWCObject` keeps its world position.

Open RE items for the implementer (leads, not answers):
- `CNWCObject` world position — the anchor. Alloy's gob/part tables have world matrices
  (`AlloyRenderer.Highlight.cs` keys on gobs), so the highlight table may already give you a position for
  a highlighted object without new RE.
- Creature HP / max HP for the health band — no client-side lead found here.
- Name: `CClientExoApp::GetObjectName(unsigned int, CExoString*)` @0x636ab0
  (`SymbolMap.cs:118`, already used by Dialog for the speaker) + `CClientExoApp::GetGameObject(uint)`
  (`SymbolMap.cs:113`) is the id→object→name path.
- Portrait: `CNWCCreature::GetPortrait(unsigned char)` @0x68bc60 / `GetPortraitId()` @0x731970.

---

## 13. Is anything already drawing at a world-anchored screen position?

**No. This is the first.** Searches across `managed/` for `WorldToScreen`, `ScreenPos`, `ProjectPoint`,
`nameplate` return nothing but one unrelated `SymbolMap` comment. Every UI consumer today
(chat, dialog, quickbar, settings, FPS overlay, attribution overlay) is screen-space-anchored.

The nearest thing is `ShadowMath.WorldToLightNdc` (`managed/Alloy/Core/ShadowMath.cs:197`) — the right
*maths* shape, wrong space (light, not camera), and Alloy-internal.

So §3 is net-new code. Put the projection helper somewhere pure and self-checkable (no GL, no native
refs) so it gets a `SelfCheck` with a synthetic camera — the `ShadowMath.SelfCheck` pattern
(`ShadowMath.cs:215+`) is the model: construct a known camera, assert the centre maps to the screen
centre, an off-axis point maps to the expected side, and a behind-camera point is rejected.

Depth occlusion happens at replay, not at the roll-call. Each plate's batch is anchored
(`TextRig.Anchor`) to its creature's mid-body point and its plate anchor pixel. Alloy's GUI pass replays
the windows after the world frame's composite, and `UiScene` holds that frame's camera and depth. The
vertex stage then samples the depth around the anchor pixel and fades the plate to `nameplate.behindwalls`
where the scene sits in front of the creature. With no world frame (a menu), or with Alloy off, nothing fades.

---

## 14. Text wrapping for a narrow bubble

`managed/Services/Chat/Core/Text/TextWrapper.cs:23`:

```csharp
public static List<LineSpan> WrapRunes(IReadOnlyList<Rune> runes, float maxWidth, Func<Rune, float> advanceOf)
```

`public readonly record struct LineSpan(int Start, int End);` — a half-open rune-index range.

**Reusable? Yes.** It is pure Core (no native refs, no GL), takes an arbitrary pixel `maxWidth`, and
measures through a caller-supplied per-`Rune` advance — which is exactly why it self-checks headless with
a fixed 10px advance. It does greedy word wrap at spaces, hard-breaks an over-long word, and honours
explicit `'\n'` (consuming the break character). That is precisely a narrow bubble's requirement.

Pass `atlas.AdvanceOfCached` (`managed/Ui/GlyphAtlas.cs:36-38`), the cached delegate that exists
specifically to avoid a per-call `Func<Rune,float>` allocation.

**Allocation-free enough for a per-frame hot path? NO.**
`WrapRunes` does `var spans = new List<LineSpan>();` at line 25 and grows it — one allocation plus
regrowth **per call**. `Wrap(string, …)` is worse (a `List<Rune>`, a `StringBuilder`, and a `string` per
line) and is explicitly documented as "used by the self-check".

Chat solves this by **wrapping cold and caching**: `ChatWindow.WrapCache` holds a `LayoutCache`
(`managed/Services/Chat/Core/Window/LayoutCache.cs`) — an *immutable* snapshot of the wrap results,
swapped atomically so concurrent builds never see a torn state, with a `Hit(...)` predicate keyed on
every input that could change the layout (wrap width, tab mask, toggles, store version, timestamp
format, **and the atlas reference**, so a font re-bake invalidates it).

**Do the same for bubbles:** wrap once, on the bubble hook (cold) or on a width/atlas change, into a
per-bubble immutable record holding the rune list + spans; the per-frame draw only walks the cached
spans and emits glyphs. Include `TextRig.AtlasEpoch` (or the atlas object reference, as `LayoutCache`
does) and `Metrics.Scale` in the invalidation key.

If a truly per-frame wrap is ever needed, the fix is a caller-owned `LineSpan[]` + a count
(`int WrapInto(IReadOnlyList<Rune>, float, Func<Rune,float>, LineSpan[] dest)`), mirroring
`ChatStore.View`'s "the only slice is the caller-owned dest" contract. That overload does not exist yet.

---

## Checklist for the implementer

1. Confirm the **z-order** decision with the user (§1) — nameplates currently paint over chat + dialog.
2. Decide the **camera source** (§3): publish `SceneVp` from `AlloyRenderer.Frame.cs:96` (preferred), or
   reconstruct from `FrameInputs`. Either way, state what happens under `alloy.enabled=0`.
3. Read the **highlight set** from `AlloyRenderer.Highlight.cs` rather than tracking Tab (§10).
4. Hook `CNWCObject::SetPopUpBubbleText` for bubbles — **verify in Ghidra first** (§12). Do not try to
   join `ChatLine` to a world object; it has no object id.
5. **Store object IDs, never `CNWCObject*`** (§12). Cold: id + strings. Hot: `GetGameObject(id)` →
   position, null → retire. A stored object pointer is the #33/#138/#145 crash class with no guard.
6. Branch on `GameGate.InGame` at the top of `Draw`, and clear the plate table on the
   `GameGate.InModule` true→false edge (§1 skeleton).
7. One `Window`, `Focusable = false`, `HitTestOverride` over a cold-swapped plate-rect array (§1, §10).
8. Cache every string, every wrap, every colour parse **cold**; the frame emit walks cached data only
   (§7, §14).
9. Outlined text on by default (§8); portraits via `AurTex`/`TextRig`, batched like
   `ChatRenderer.DrawPortraits` (§9).
10. `/nameplate` command + `cx-nameplate.cfg` via `ConfigFile` with the deferred first-frame load (§5).
11. `SettingsPanel` rows reusing `SetAct.ToggleBit` + `Bit` discriminators where possible (§6).
12. A `SelfCheck` for **every** pure piece — it is the only test that exists (§11).
