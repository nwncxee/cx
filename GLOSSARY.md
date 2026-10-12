# CONTEXT

Glossary for the client extender. Domain vocabulary only — no implementation
detail, no decisions (those live in the Plane tracker and `docs/adr/`). Terms crystallised while charting the native chatbox reimplementation.

## Product

- **NWNCX:EE** — the product's full name; **CX** is the shorthand (#530). Every user- and
  operator-visible surface says one of the two: `cx.cfg`, `cx.log`, `cx-launch`, `cx_managed.so`,
  the `cx_*.shd` stages, the `cx-<os>-x64` release zips, the `CX.*` C# namespaces. The
  `CX*`/`CX_*` identifiers in C# and GLSL are internal glue — never shown, never renamed.
  **cx** survives only as that glue and as the pre-rename config stem the read-fallback honours.

## Chatbox

- **ChatLine** — one retained chat message: `channel`, `senderName`, `playerId`,
  `portraitResref`, `colorRuns`, `timestamp`, `rawText`. **Immutable after ingest**:
  a line never mutates once received, so toggling a data-bearing feature (e.g.
  timestamps) affects new lines only, never old ones.
- **Ingest** — the moment a `ChatLine` is received and frozen (the inbound engine
  tap). *Cold* path (main-thread network), distinct from *draw* (the hot frame path).
- **ChatStore** — the retained model: a fixed-capacity **ring** of `ChatLine`
  (oldest overwritten) plus filtered/paged views. The single source every feature reads.
- **ChatWindow** — a movable/resizable chat window: a `rect`, an ordered **list** of
  tabs, an active tab, and a **lock**. Several can exist at once (a small cap); every
  draw/input path takes a window, so the set is a list + a loop.
- **Main window** — the first window (index 0). Undeletable, always has ≥1 tab, and is
  the fallback keyboard owner when no other window is focused.
- **Focused window** — the one window that owns the keyboard and shows the input strip
  + send dropdown. Clicking a window focuses it; the chat-open key focuses the
  last-focused window (the **Main window** if none).
- **Tab** — a *receive* view inside a window: a `label`, a channel-mask **filter**, an
  optional **player pin**, and a scroll offset. A window always has ≥1 tab; tabs can be
  added, removed, reordered, and dragged between windows.
- **Channel** — a chat stream. **Sendable** (Talk, Shout, Whisper, Tell, Party, DM)
  or **receive-only** (System, Combat, Server).
- **Player pin** — a tab bound to one player by **name** (stable across relog), never by
  `playerId` (connection-scoped, changes each session). Sending resolves the live
  `playerId` from the name at send time.
- **Tell tab** — a Tab that is player-pinned and shows the Tell channel: your private
  thread with that player. Sending on Tell from it auto-targets the pinned player.
- **Send channel** — the channel the focused window's input targets, picked from a
  dropdown **constrained to the active tab's visible channels** (you cannot send on a
  channel the active tab hides). Active-tab-scoped, superseding the earlier per-window
  dropdown (#25): each tab remembers its own for the session and gets it back when it
  becomes active (CX-59); a relog starts every tab on Talk.
- **Tells-here window** — the window flagged to receive auto-opened Tell tabs (the Tell
  auto-open setting is Off / new-tab / new-window, default Off); defaults to the focused
  window when none is flagged.
- **Chrome** — the appearance shared by *every* window: font size, colours, animation,
  timestamp style. Global, not per-window.
- **Clone** — copying one window's look-and-behaviour (size, render toggles, input side,
  lock) onto a chosen **target** window. Never copies tabs, position, or **Chrome**.
- **Lock** — a per-window flag that disables moving, resizing, and tab drag-drop for
  that window.
- **Color run** — a span of text with one color, parsed once at ingest from the
  engine's inline `<c…>`…`</c>` markup.
- **Render component** — a stateless unit that draws one aspect of a line
  (timestamp, portrait, sender name, message body). The ordered set of them is the
  **render pipeline**; each is individually toggleable.
- **Ingest observer** — a subscriber notified when a line is stored (audio cue,
  whisper flash, auto-open a whisper tab). Reads the line, never mutates it.
- **Typing state** (`IsTyping`) — whether the chatbox owns the keyboard. While true,
  gameplay hotkeys are suppressed and keys route to the edit control.
- **Suppression set** — the engine hooks skipped so the extender's chatbox replaces
  the vanilla one; installed only while the Chat feature's master toggle is on (a restart
  effect), so "chat off" is the stock chat window untouched.

## Framework

- **Core vs shell** — *Core* is pure C# logic with no native references (testable
  off-device); the *native shell* is the hooks, GL draw, and SDL calls. Features land
  in the Core behind the shell.
- **Extender-optional** — a stock client plays vanilla; the extender only ever reuses
  existing wire opcodes, so an un-extended client loses nothing.

## Platform port

- **Resolution** — how a Linux symbol lands on Windows: by *Name* (an MSVC export), by *RVA*
  (a build-locked offset from the module base), or *Skip* (no Windows anchor). Avoid: mapping.
- **Build lock** — the one PE stamp the RVAs were read from. A different stamp turns every RVA
  resolution into Skip. Avoid: version pin.
- **Stand down** — a feature whose required symbols do not all resolve runs vanilla, logged with
  the cause (Skip by design, or Skip by rot). Never half-live: a feature stands down as a unit,
  the same word the post stack uses. Avoid: degrade, fall back.
- **Anchor** — the compiler-invariant evidence (a unique string, an immediate constant, an exported
  neighbour) that ties a Windows RVA to its Linux symbol.
- **Engine layout** — where a *field* sits inside an engine class, as opposed to where a *function*
  sits in the image (that is Resolution, above). One table, `EngineLayout`, keyed (os, build).
  Avoid: offsets, struct map.
- **Build key** — the linker's identity for a client build: the ELF `.note.gnu.build-id`, the PE
  CodeView PDB GUID. Not a content hash, so a rewrapped Steam/GOG copy of one build is one key.
  Distinct from the older **Build lock**, which is the PE TimeDateStamp guarding SymbolMap's RVAs.
- **Diverges / Same / Unresolved** — the three, and only three, resolutions an offset can carry.
  *Same* means measured on both and equal; *Unresolved* means nobody looked on Windows yet, and is
  counted on both platforms (only the gate is Windows-only). The pair exists so "verified identical" and "never looked" stop being one bare `const`.
- **Nm / At / LinuxOnly / Hole** — the four, and only four, Windows resolutions a *symbol* can
  carry, the same argument one step up from offsets. *Nm* is a name export, *At* a build-locked RVA,
  *LinuxOnly* a decision with its reason, *Hole* an absence with its reason. Only *Hole* counts, and
  a constant with no row at all is what `build.sh` refuses — before this, a missing key was both
  "never looked" and "decided".
- **Candidate** — an extractor's ranked guess at where a field sits on the other platform. It names
  the function to open next; it is never a resolution on its own. A candidate becomes a row only by
  clearing the bar in `docs/engine-layout.md`. Avoid: match, hit, result.
- **Probe** — an assertion a resolved offset makes about live memory (the dword is 0 or 1; the
  vtable slot holds a code address), checked once at the first instance so a wrong offset stands
  the feature down instead of rendering garbage. Avoid: validation, sanity check.

## UI framework

- **UI framework** (`CX.Ui`) — the shared layer every windowed feature builds on. Owns
  the frame present, the render toolkit, input/focus arbitration, and the cursor. A
  feature supplies **content**; the framework supplies everything a window needs but
  shouldn't reinvent. Formerly all of this lived under the **Chatbox**; Chat is now just
  one consumer among several.
- **Frame host** — the single owner of the frame present (`SwapBuffers`), the in-game
  gate, and UI-scale sync. Drives every registered **Window** once per frame. (Was
  Chat's `OnSwap` roll-call.)
- **Window** (framework) — a retained UI window the framework owns: rect, z-order,
  focus, drag/resize, hit-regions, cursor, and input dispatch. A service fills only its
  **content**; the shared behaviour is done for it. Chat's tabs/tells and Dialog's panel
  are *content* inside such a window, not windows of their own making.
- **Chrome vs content** — *chrome* is the shared, framework-owned window behaviour
  (frame, drag/resize, focus, cursor, input routing); *content* is the service-specific
  interior (chat lines, dialog rows). The split between them is the framework's core seam.
  (Distinct from the Chatbox **Chrome** term, which is chat's shared per-window *look*.)
- **Selectable text** — an opt-in capability of the framework's text layer: any content
  can mark its text select-and-copyable. Lifted from the Dialog service.
- **Cursor (as UI concern)** — the pointer shape is a byproduct of the per-frame
  hit-test: whichever Window hit-region is under the pointer names the cursor. No
  separate cursor subsystem. Absorbs the former `CursorService`.
- **Golden regression gate** — the Chat service is the reference the migration must not
  regress: the most-polished consumer, carrying live-verified fixes (AurTexture lifetime,
  drive-latch, keybind gating). Its look, feel, and fixes survive the extraction
  byte-for-byte; other consumers may shift to adopt the unified behaviour.
- **Focus** — the one **Window** that owns the keyboard at a time, across every service.
  Acquired per the **focus mode**: *click-to-focus* (default) or *hover-to-focus* (opt-in,
  user-configured).
- **Input precedence** — the fixed order the **frame host** resolves every input event:
  (1) the focused Window's content, (2) registered **input consumers** (chords),
  (3) engine gameplay keybinds. Each layer consumes the event or passes it down. A global
  hotkey fires only when no window wants the key.
- **Input consumer** — a service that registers to receive input at a defined priority
  *without* owning a Window (e.g. the Hotkeys service's chords). A Window, by contrast,
  receives input by being focused or hit-tested.
- **Keyboard-owned gate** — the single framework signal that some UI owns the keyboard;
  while set, engine gameplay keybinds are suppressed. The drive-latch and keybind-dispatch
  fixes live here once, for all consumers.
- **Capability flags** — per-Window switches (`Focusable`, `Draggable`, `Resizable`,
  default on) that let one Window type cover docked bars and read-only panels without a
  second abstraction. A non-focusable Window is skipped by focus hit-testing but still
  drawn and still drives the cursor.

## Settings

- **Knob** — one user-adjustable value the extender exposes, declared once by the feature that owns
  it: what it is called, what kind of control edits it, its range or choices, its default, its
  **tier**, its **effect**, and its **placement**. A knob that is not declared does not exist —
  nothing can list, edit, or persist it. Avoid: option, setting, tunable (when the declaration is meant).
- **Settings panel** — the one window every knob is edited in. Three columns: **category**,
  **submenu**, the submenu's knobs. Reached two ways only: the `/settings` chat command in game,
  and the **gear** at the main menu. Every feature's former settings icon and per-feature panel is
  gone; the panel is the single place.
- **Knob group** — the unit a feature declares its knobs in. One identity wears five hats: the
  group is the **submenu** the knobs appear in, the chat command that edits them, the reset unit,
  and the first segment of every knob's key (`grass.density`, `chase.fov`, `chat.font` — keys are
  two levels, group then knob). A group may carry a **feature master toggle** and may name a
  parent group whose master greys it out. Avoid: feature (when the declaration unit is meant), pass.
- **Category / submenu** — the two-level tree the panel navigates: a category (UI, Graphics,
  Camera, General, Hotkeys) holds submenus (UI → Chat, Camera → Chase); a submenu is one knob
  group, its knobs under optional section headers.
- **Boot value** — what a knob was when the client launched, as distinct from its current value.
  A **restart**-effect feature reads its boot value; the panel edits the current one, and the two
  differing is what "restart to apply" means.
- **Tier** — how visible a knob is: **User** (shown, with help text), **Advanced** (shown under a
  collapsed *Advanced* section of its submenu; expert dials), **Chat-only** (diagnostics and
  bisect switches: reachable by chat command, listed by `/knobs`, never a panel row), **Hidden**
  (persisted only — no row, no command, not listed: state a feature edits on its own surface, such
  as the chat window's position and tabs). Absence from the panel is a declared tier, never an
  omission. A submenu's **Reset to defaults** covers its rows (User and Advanced) only.
- **Effect** — when a change takes hold: **live** (immediately, the default and the goal) or
  **restart** (written now, read at the next launch — boot decisions and engine-path replacements).
  The row says which. Live wins wherever it can without costing the frame.
- **Feature master toggle** — the first row of a feature's submenu: "Enable <feature>". Off means
  the feature stands aside and the client behaves vanilla for that feature; the rows below grey out.
  A feature's effect (live / restart) is its master toggle's effect. There is no whole-extender
  toggle: "everything off" is every master off, and "no CX" is an uninstall.
- **Layout-affecting knob** — a knob whose change re-lays-out the UI that edits it (font size, UI
  scale). Always a stepped choice (a **dropdown**), never a slider: a slider that moves the
  slider under the pointer defeats the user's intent.
- **Command gate** — the one config knob that makes the extender's chat commands live at all.
  Off by default: a server may use the same command names for its own ends, so commands fall
  through to the server unless the user opts in. `/settings` is the sole exemption.
- **Settings file** — `cx.cfg`, the one file every knob persists to, dotted keys sorted
  alphabetically, written by the panel and hand-editable. It holds only knobs that differ from
  their default (a reset removes the key), plus any key no declaration claims, kept verbatim. It is
  read once at launch and regenerated on every write, so hand edits happen with the client closed.
  The per-feature `cx-<feature>.cfg` files are retired into it. The hotkey consent store is state,
  not settings, and stays its own file.
- **Dropdown** — the enumerated-choice control: click to open a list, pick one. Replaces the
  cycle button, which hid the choices and needed N clicks to reach one.
- **Tooltip** — the help text behind a faint "(?)" beside a row's label, shown on hover. Every
  User-tier knob has one.
- **Off-default marker** — the visible sign on a row that its value differs from the declaration's
  default; the submenu's **Reset to defaults** clears every one of them at once.
- **Status line** — a group's one read-only line (Renderer's "Running: Alloy — …"): the panel
  shows it at the top of the submenu and bare `/<group>` prints it. A group with no knobs (About,
  Debug info) is its status line.
- **Link row** — a row that is an address, not a value: pressing it copies the address to the
  clipboard and the row flashes "copied". Like an **action row** it stays live under an off master.
- **Point (pt)** — the unit CX text size is stated in: 1 pt = 1 design pixel before GUI scale.
  Not a typographic point; no screen DPI is assumed. Box sizes and distances stay in design px.
- **Panel face** — the one fixed text size the settings panel draws itself in (scaled by GUI
  scale, never by the user's text-size knob), so the panel supports one size, not every size a
  user can pick.
- **Per-window state** — what a chat window keeps for itself (rect, tabs, colours, animation,
  timestamps, lock): edited on the window's own gear, never a panel row. Hidden-tier settings.
- **Camera mode group** — one knob group per engine camera mode (chase, top-down, stiff chase),
  identical rows with per-mode values, each with its own master: off means the stock camera runs
  untouched in that mode. Tuning shared by the modes (recenter, drive, walls) is its own group,
  never a mode's.
- **Action row** — a panel row that does something when pressed instead of holding a value (enter
  the free camera, revoke every approved hotkey). Never persisted; the reset leaves it alone; and it
  stays live under an off master toggle — a value row greys, an action row does not. Avoid: button knob.
- **Link row** — a read-only panel row holding an address (the releases page); pressing it copies the
  address to the clipboard rather than opening anything. Not a knob.
- **Consent store** — the per-server record of hotkeys the player approved, one token per server,
  kept in its own file as state. **Revoke** forgets this server's approvals and tells it to unbind;
  **Forget every server** wipes the whole store. Never a knob; never reset by the panel.
- **Stock setting** — a value the player set in the game's own Options (`settings.tml`, the
  engine's `CExoConfig`), as opposed to a knob. Read-only to the extender, taken as a snapshot at
  the world edges (`StockConfig`, #571). A stock **toggle** is a hard gate over the matching Alloy
  stage where one exists (grass, keyholing, bloom, enhanced lighting); a stock **numeric** is not
  imported where a knob already covers the same thing -- the knob wins. Avoid: engine option,
  vanilla setting.

## Rendering (Alloy)

- **Builder intent** — the part of an area's authored lighting that Alloy must preserve regardless
  of how it shades: *a light reaches what the builder made it reach, and what the builder left dark
  stays dark*. Range and the lit/unlit boundary are a content contract, because builders use them
  for gameplay — an area left unlit is meant to be unlit, and a decor light is not meant to extend a
  sightline. How a lit surface *responds* — colour, specular, roughness, bloom, tessellation, shadow
  softness — is Alloy's and is free to differ from the engine's. Distinct from visual parity with
  the engine's renderer, which is not a goal anywhere.
- **Parasitic input** — Alloy state produced by observing the engine render rather than by reading
  what the engine simulates: a part discovered because the engine tried to draw it, a camera
  harvested off a transform call site, a pose produced by re-entering a draw function. The
  contrast is a **direct read** — the same fact taken from the engine's own state at a moment Alloy
  chooses. Retiring parasitic input is what M5 means by *build it properly*; it is a statement about
  Alloy's inputs, never about the image.
- **Scene enumeration** — Alloy's own walk of the world's objects, covering everything in the area
  rather than the visible slice, producing the persistent records every pass reads. Contrast the
  **draw traversal**, the engine's per-frame walk of its draw buckets, which Alloy inherited
  discovery and visibility from through M4.
- **Owned** vs **staged** — a scene part is *owned* when Alloy draws it (engine
  submission suppressed for it) and *staged* when the engine still draws it during the
  migration. Every part is drawn every frame by exactly one owner, never both, never neither.
- **Cache classification** — a caster is cached or re-baked based on whether its
  transform/pose changed since its last bake, not on a static/dynamic *kind*. A tile is
  simply a caster that never moves; a creature is one that changes every frame.
- **Embedded stage** — the GLSL for an Alloy pass, embedded in the extender as a `.glsl` resource
  (`managed/Alloy/Shaders/`, read through `ShaderSource`; #707 -- it was a C# `const string` before).
  This is the shader that ships: it travels with the renderer it agrees with, so the per-draw record,
  the uniform blocks and the clip expression can never disagree with the CPU side. Every overridable
  stage has one. Every file, stage or library, is named by a `cx_*` resref of at most 16 characters (the
  hak/NWSync limit), so a library can be overridden on its own too.
- **Shader override** — a `cx_*.shd` a *user* places where the engine's resman will serve it
  (`development/`, `override/`, a hak), replacing that stage's **embedded stage** at load. A
  first-class customization surface — CX ships defaults and lets them be replaced — and explicitly
  **not** a delivery channel: no content repo ever ships one, because a shipped copy is a second copy
  of a contract that lives in C#. Refused by the stage guards when it breaks that contract; a refused
  or uncompilable override falls back to the embedded stage with a log line.
- **Shader dump** — the current **embedded stages** written out as editable `.shd` files, one per
  overridable resref, into the client's user directory. The starting point for a **shader override**,
  and current by construction because it dumps what is compiled in. Distinct from vanilla shaders,
  which CX never modifies.
- **Tuning knob** — see **Knob** under *Settings*. (Formerly: a value in exactly one of three
  kinds — chat command, config setting, deleted. Superseded by the unified settings model, where a
  knob is one declaration and the chat command and the settings panel are two views of it.)
- **Render mode** — which renderer draws the 3D scene: the *owned* mode (Alloy) or the
  *vanilla* mode (the engine's own path). Both ship permanently; a device that cannot run the
  Alloy mode still runs every other extender feature. The mode is whole-scene — never a
  per-class mixture — and it is **chosen once per session** (`alloy.enabled=0` in `cx.cfg`,
  restart required), because Alloy's residency is exclusive and the two resource worlds
  cannot both be warm. Bare `/alloy` reports which one is running and why.
- **Permanent engine resident** — a responsibility the engine keeps for good, not one staged
  back to it during a migration: the 2D UI, resource loading, simulation state, and
  animation/bone state. Rendering is not on the list. The rule that draws the line is *the
  engine simulates, we draw* — a class whose motion the engine computes can still have its
  geometry submitted by Alloy.
- **Class-granular ownership** — the unit at which a content class may be *owned* or *staged*.
  Opaque classes may stage part-by-part; classes whose draw order is load-bearing (transparent,
  additive, fade, water) are all-owned-or-none, because a half-owned class has no coherent
  order. The one-owner rule holds at whichever granularity a class uses: drawn every frame by
  exactly one owner, never both, never neither.
- **Vertex paint** — a per-vertex RGBA colour an artist paints onto a mesh in the modelling tool and
  which travels with the model, read by a shader as a mask rather than as a colour: which parts of a
  tree sway, where moss grows, how much a strand of hair moves. Distinct from the mesh's *diffuse
  colour*, which tints what is drawn. Alloy carries it into every vertex stage (one packed word per
  vertex, white where a mesh has none), so a material's own shader can weight an effect by it. The
  engine's own name for the second colour stream is `vCustomColor`.
- **Palette rows** — the ten numbers that colour a creature: one master-palette row per PLT layer
  (skin, hair, metal, cloth, leather, tattoo), chosen by the player or the server. They are a property
  of the *wearer*, not of the model or the material, which is why two characters in one armour model
  look different. A shader reads them as the engine's `PLTscheme` feed.
- **Material synthesis** — deriving material parameters for content that declares none, rather
  than requiring every model to be re-authored. Content that *does* declare parameters is
  always honoured over synthesised ones.
- **Vanilla view** / **Alloy view** (of a material) — one `.mtr` carries both. The *vanilla view* is
  the stock parameters and texture slots, which both clients read with the engine's meaning; the
  *Alloy view* is the `Cx`-prefixed parameters (glTF's metal/rough vocabulary) and `texture11`+,
  which vanilla parses and ignores. Where both speak, the Alloy view wins in Alloy (ADR 0026). A
  **physical material** is one whose Alloy view declares metalness or roughness: shaded as glTF
  shades it, metal with no diffuse.
- **Shader generation** — which vintage of the engine's stock shader set a `.shd` belongs to.
  Two exist and they are not interchangeable: an older set where the transform and fog live
  inline, and a newer split-include set where they are separate headers. A shader claim is
  meaningless without naming the generation it was read from, and the generation the running
  client loads is the one that counts.
- **Pass slot** — the fixed, unconditional point in the engine's frame where extender passes that
  must run *before* anything is drawn fire: after `Scene::AddPartsToDrawBuckets`, buckets complete,
  nothing drawn. Chosen because it executes every frame in every zone, unlike the engine's own
  shadow cadence. The light walk and the world-caster shadow bakes live here.
- **Frame anchor** — the point the Alloy frame's draw work fires from. Distinct from the **pass
  slot**: the anchor is where we *draw*, the pass slot is where we *prepare*. An anchor must fire
  unconditionally once per scene render; whether it also supplies usable engine frame state
  (viewport, framebuffer, matrices) is a separate property, and the one the Alloy pipeline sheds as
  it matures. Since #132 it is `Scene::FadeDoors`, after-original — the last engine call inside
  `Scene::RenderSinglePass` before the first bucket walk, and after the clear the engine's own frame
  setup does. It cannot be RSP *entry*: the world FBO is bound and cleared inside RSP, so a resolve
  written at entry is wiped.
- **Cutout vs blended** — the two classes every non-opaque part is sorted into. *Cutout* is
  alpha-discard geometry: it draws with the opaque set, writes depth, and needs no sort. *Blended*
  is genuine alpha: it draws after, sorted back-to-front, and never writes depth. The split is what
  makes a missing sort harmless for the majority of content and makes alpha-blended geometry
  incapable of occluding what it does not draw.
- **Resolve stage** — the single fullscreen pass that encodes the linear Alloy frame to display
  space at the end of the frame. Distinct from a *post stack*: the resolve is a colour-space
  correction that makes Alloy blending mathematically right, not a place for tonemapping, exposure,
  or bloom — those plug in later, at the same point.
- **Ambient occlusion pass** — screen-space visibility (GTAO) computed from the depth prepass BEFORE the
  colour pass and read by the lit stage as a scale on its ambient term, beside the material's AO map
  (ADR 0021, #379). A lighting input, not a post stage: vanilla's SSAO darkens the finished frame.
- **G-buffer** — the four render-size targets the colour pass writes beside the colour (draw buffers 4–7,
  opaque and cutout of the main view only) so the deferred pass can add the point lights per pixel instead
  of per fragment per light (ADR 0029). It holds what the light walk reads off the surface plus the
  **transfer weights**; a pixel no twin covered reads back "not mine" and is left alone by construction.
- **Deferred twin** — the lit fragment stage compiled to skip the point-light walk and write the
  **G-buffer** instead. Not a second shader: the same stage, one `#define` apart, so the loop's registers
  are compiled out rather than branched over. A frame is all-twin or all-forward, never half.
- **Transfer weight** — a G-buffer lane holding the derivative of the finished pixel with respect to one of
  the light walk's results (its diffuse, its specular, the baked term's share), so the deferred pass adds an
  exact linear correction and every nonlinear composition — the env pattern, the coat probe, the ghost
  divide, keyhole, fog — stays in the lit stage. A lane says what the surface *does with one more unit of
  point light*, not what the surface is.
- **Post stack** — the ordered display-space stages that run after the resolve, where the engine ran
  `Camera::DoFrameBufferEffects` (ADR 0020, #375; the aa stage = SMAA, #377). Each stage reads the previous output and hands
  display space on; the last one writes the engine's target and carries the frame's one dither. A
  stage stands down alone; the stack never takes the frame down. Gamma is the first stage.
- **Replacement class** — a content class the Alloy frame reimplements rather than reproduces: the engine's
  own path is left untouched and simply not called, and the Alloy one is built to the technique the class
  should have had. Distinct from *owned*, which only says who submits the same geometry. Grass (#127) is the
  first: its punch-through is not fixed, it is made structurally impossible, because nothing in the owned
  class is ever semi-transparent and depth-writing at once.
- **Patch** — the Alloy grass unit of culling: one engine triangle list (a bin's faces sharing one grass
  material), resident as its faces. Frustum- and distance-culled whole on the CPU; the GPU scatter then
  regenerates its blades from the faces' seeds each frame, culls them one by one, and appends them to its
  material's indirect draw (CX-24).
- **Blade / card** — the two ways a grass atlas cell draws (CX-24). A *blade* is a procedural, opaque, lit triangle
  strip tinted with the cell's colour ramp (the default `grass.style`); a *card* is the atlas's own alpha-cut
  crossed quad. Under the blade style each live atlas is classified per cell, biased toward card, so flower
  cells stay cards; under the texture style every cell is a card.
- **Screen-door fade** — fading geometry out by discarding a growing share of its fragments against a
  per-pixel dither, instead of ramping its alpha. Every surviving fragment stays fully opaque, so a fade
  cannot introduce a blended fragment — which is what lets a fading class keep writing depth.
- **Bracket** — one entry into the owned GL context and back (fence, flush, two `glXMakeCurrent`,
  error drain). Every bracket is a GPU pipeline bubble, so the count per frame is a **budget**, not
  an implementation detail: Alloy passes are grouped to fit inside existing brackets rather than
  taking their own.
- **Rain occlusion map** (#617) — one orthographic depth view of the still casters, looking along the
  **fall vector**, baked and held exactly as a sun cascade is (the texel-snapped fit matrix is the key) and
  stored as the spare layer of the sun array. The one primitive of Alloy weather: a shadow-map test against
  it answers "is this point under cover" for a falling streak and for a surface alike. Avoid: roof mask.
- **Fall vector** — the unit direction precipitation travels: gravity leaning into the area wind the
  renderer already uploads (`normalize(wind * lean, -1)`). What the field falls along and what the rain
  occlusion map looks along, so an overhang shelters downwind and not upwind.
- **Exposure** — a surface point's 0..1 answer from the rain occlusion map: 1 under open sky along the fall
  vector, 0 under cover. The only per-fragment weather input; every response is exposure x an amount.
- **Soak** — the CPU-side amounts the world carries: wetness and snow integrate density x time while the
  weather runs and decay after the stop edge; puddles fill once wetness passes a threshold and drain slower
  than the sheen dries. Uniforms, never per-texel storage; a surface point's response is its exposure x the
  soak. Avoid: accumulation buffer.
- **Silence** (weather) — the vanilla weather emitter removed at its source: `CNWCArea::SetWeather` is
  forwarded density 0 while the master is on and the row is one Alloy draws, which is the engine's own
  "no weather" path (it deletes its six fx objects itself). Nothing downstream is suppressed, and an
  un-mapped row (fog, a dry storm) keeps its stock emitter. Distinct from the particle pass's per-emitter
  decline, which suppresses a build the engine still owns.
- **Static set** (lightmap) — the geometry a bake may light: tile geometry plus placeables flagged
  Static, each keeping its real world transform. Anything a script moves, hides or destroys leaves the
  set for good. Redrawn in #656 from "tiles only": areas are a flat tile plane plus Static placeables.
- **Chart** — one connected patch of a mesh's triangles flattened onto lightmap texture space, with no
  two triangles sharing a texel. A mesh's charts are made once per mesh content by the **charter**
  (vendored xatlas) and cached forever; a chart cut may split a vertex the engine did not.
- **Chart file** — the cached charts of one mesh, an interchange format at the mesh level (versioned
  header, content key, per-vertex UV2 and the split index run), never Alloy pool indices. A toolset
  could ship it inside the module.
- **Instance bake** — the baked light of one placed instance: the mesh's charts sized from the
  instance's world-space area (visual transforms included) and packed per instance into atlas pages.
  Fifty pillars are fifty rectangles, never one shared bake.
- **Directional lightmap** — what an instance bake stores: the irradiance a texel receives from every
  static light plus the dominant direction it came from, so the surface's own normal detail still shapes
  the light at draw time. Avoid: flat lightmap, irradiance map alone.
- **Hot light** — a static light with a mover under it this frame. A baked surface owes it a live shadow
  for that mover only; the light's own contribution stays baked. Avoid: dynamic light (that is a light
  that is never static).
- **Mover shadow** (lightmap) — the shadow a hot light's mover casts onto a baked surface, taken back out
  of the baked light rather than added by the real-time loop. The `lightmap.movers` toggle; off means
  movers cast no shadow from static lights on baked surfaces.
- **Bake key** — the identity of one instance bake: the mesh's chart, the instance's placement, the
  texel density, and only the static lights that reach the instance. A torch a script toggles moves the
  keys of the instances it reaches, nothing else.
- **Baked surface** — a drawn instance whose instance bake has landed: it reads its light from the
  pages and the real-time loop skips every static light for it. Everything else is **unbaked** (a mover,
  a refused mesh, a bake still in flight, an instance past the page cap) and takes the whole loop. The
  two looks are meant to match; a surface flipping between them is a cost change, never a visual one.
- **Bake record** — the per-draw pointer from a surface to its bake: which page and where on it. An
  unbaked surface carries the empty record. Written when a bake lands or leaves, never per frame.
- **Static bit** — the per-light mark "this light is in the pages". A baked surface skips a static
  light outright unless it is hot, and then takes only the mover shadow from it. Ambient-class lights
  are never static.
- **Static-set fingerprint** — the identity of the area's static lights as a whole. It moves only at
  area load and when a script toggles a static light, and that move is the one edge on which instances
  re-key: nothing re-keys per frame.
- **Page cap** — how many lightmap pages an area may hold (a quarter of the card's memory in 128 MiB
  pages, at most 32) unless `lightmap.pages` names a number. An area that would overflow it bakes at a
  lower density area-wide (1/2, 1/4 ... of `lightmap.density`) rather than leaving instances unbaked.
- **Density tier** — the density an instance's rect is baked at, by its distance from the player: tier
  0 is the full `lightmap.density` inside `lightmap.near` metres, each tier out doubles the radius and
  halves the density, and the last tier (`lightmap.tiers` - 1) is the **baseline** that covers the whole
  area and bakes first. A rect refines to a finer tier as the player approaches; its readers keep the
  baseline record until the fine one lands, and a fine page far past its tier is evicted whole when the
  pack needs the room -- its readers fall back to the baseline. Avoid: LOD (that is the mesh's), mip.
- **Area-loaded hold** — the client keeps the server's "place the player" message (the
  area-loaded send) back until the lightmap bake has landed, so the player never stands in a
  world that is still baking. Ends at the first of: the baseline in (every chart answered and every
  baseline rect landed -- one page is not the area), the player skips, the hard cap. The server keeps
  streaming the area's objects to a client under the hold, so the static set closes under it.
  Avoid: loading delay, stall.
- **Hold budget** — the `lightmap.hold` seconds after which the hold offers a skip. 0 = no hold.
- **Hard cap** — the fixed ceiling past which the hold always releases, set by the server's own
  readiness deadline. Not a knob: a safety ceiling nobody tunes.
