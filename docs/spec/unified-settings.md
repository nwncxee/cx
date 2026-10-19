# Unified settings — build spec

The contract for the one settings panel, the knob registry behind it, and the one `cx.cfg`.
Decided on wayfinder map #556 (tickets #557–#568); the decisions are recorded there and in
[ADR 0025](../adr/0025-unified-settings-model.md), as amended by
[ADR 0030](../adr/0030-player-facing-settings-panel.md) (tier by audience, shared submenus,
quality presets). This file assembles them so a build session needs nothing else. Glossary:
`GLOSSARY.md` → *Settings*.

Research it rests on: the research on CX-142 (CX-142)
(live vs restart per feature, with file:line evidence), the research on CX-149 (CX-149)
(present, input and layout at the main menu; the `/settings` seam), and
`docs/prototypes/settings-panel-560/index.html?variant=B` on branch `prototype/settings-panel-560`
(the pinned look). Rejected door: the research on CX-166 §4 (Path A′).

Conventions in the tables: kind **T** toggle · **S** slider · **D** dropdown · **Sw** swatch ·
**X** text · **L** list · **B** button (action row) · **Lk** link row. Tier **U** User ·
**A** Advanced · **C** ChatOnly · **H** Hidden. Effect **L** live · **R** restart. Range ·
default is the declaration's (min–max step · default). Help is written for U rows (mandatory --
the registry demotes a U row that has none); an A row gets a one-liner only where the label is
not enough. A ChatOnly family is one summary row per group: the key list, no help, label = key
where the knob has no player-facing one.

## 1. The registry

```csharp
public abstract class Knob            // metadata: what the panel, cfg, chat and /knobs see
{
    public string Key;                // "grass.density": cfg key, chat form "/grass density"
    public string Name;               // "density": the knob segment chat types after the group
    public KnobGroup Group;
    public string Label;              // "Density"
    public KnobKind Kind;             // Toggle | Slider | Dropdown | Swatch | Button | Text | Link | List
    public Tier Tier;                 // User | Advanced | ChatOnly | Hidden (set once, at declaration)
    public Effect Effect;             // Live | Restart
    public string Section;            // optional header inside the group's rows ("Wind")
    public string Help;               // row help / tooltip; REQUIRED when Tier == User
    public string Hint;               // placeholder of a List's empty add box
    public bool Layout;               // layout-affecting: Dropdown only, by construction
    public bool Disabled;             // a row that exists but cannot be edited yet: greyed, inert
    public Action? Changed;           // side effect after a set; a Button's press
    public abstract string Format();  // cfg / chat / /knobs form
    public abstract bool Parse(string s);
}
public sealed class Knob<T> : Knob
{
    public T V;                       // the live value: what the panel edits and the cfg persists
    public T Boot;                    // bound at construction, never changes; Restart readers read THIS
    public T Default { get; }         // fixed, or the rung of a Ladder's driver
    public readonly T Min, Max, Step; public readonly string Unit;   // Slider (float or int)
    public readonly string[] Choices; public readonly T[] Values;    // Dropdown: labels shown / values stored
    public Knob<T> Ladder(Knob driver, params T[] rungs);            // preset member (below)
}
```

Built-in `T`: `bool` (Toggle), `float`/`int` (Slider, Dropdown), `string` (Text, Dropdown),
`uint` (Swatch, `rrggbbaa`), `TextList` (List). Any other `T` is allowed and supplies its own
Format/Parse (`G.Custom`); the panel renders it by `Kind`. `KnobButton` is the valueless kind
and `KnobLink` an address row -- neither is persisted.

**The knob is the storage.** A feature reads `Grass.Density.V` where it read `_density`: a field
read, hot-path safe. Every `Tune`/`Set` switch and every `|`-joined key list is deleted.

### The group

```csharp
static readonly KnobGroup G = KnobGroup.Declare("grass", category: "Graphics", submenu: "World",
    master: "Grass", masterHelp: "…", order: 340, title: "Grass");   // master -> "grass.enabled", first row
static readonly Knob<float> Density = G.Slider("density", "Density", 0f, 4f, 0.25f, def: 2f, help: "…")
                                       .Ladder(Quality.Grass, 0.75f, 1.25f, 2f, 3f);
```

- **Feature = group = chat command = reset unit = key prefix.** Keys are two-level
  `<group>.<knob>`; `.` is the only structural character, `_` is allowed inside the knob segment.
- `master:` declares `<group>.enabled` as the first row; `masterDefault:`, `masterEffect:` and
  `masterChanged:` are its default, effect and side effect. Optional. A group with no master says
  so in its `header:`.
- `masterTier:` (ADR 0030) puts the master below User: a core feature a player has no reason to
  turn off keeps its `cx.cfg` key and its `/<group> enabled` command and draws no row. While
  such a master is off, the group's rows grey under a strip that names the key and turns it back
  on when pressed (§3).
- `submenu:` is panel placement, not identity: several groups may name one submenu. Each then
  sits under its `title:` heading (default: the submenu name).
- `order:` is one global sort key. Submenus appear in the order of their lowest-ordered group,
  and a shared submenu's groups in this order (ties: declaration order). The declarations leave
  roughly 100 per category and 10 per submenu, so a new group fits between two.
- `parent:` names another group's master BY KEY (`"particles.enabled"`), resolved lazily, so the
  parent may declare later. When an ancestor is off, this group's *value* rows grey out and a
  strip says where to turn it on.
- `G.Section("Wind")` sets the section for knobs declared after it; `G.Section("")` ends one.
  Row order = declaration order; Advanced collapses below User.
- `Status` is the group's **diagnostic** line: bare `/<group>` and `/knobs` print it instead of
  every row's value. Counters, engine state, refusal ledgers -- developer text, never the panel's.
- `PanelStatus` is the line the panel shows atop the group's rows, written for a player
  ("Approved on this server: …", "High, 3 settings changed from it."). Nearly every group has
  none. **A group with no User/Advanced row and no `PanelStatus` has no submenu** at all
  (`KnobPanel.Shown`).
- `G.Bag("w")` holds Hidden dynamic keys (`<group>.w<N>.*`) outside the rows, the command,
  `/knobs` and `Reset`.
- `KnobGroup.Declare` registers the group for enumeration and its generated `/<group>` handler.
  A group that stands down this session still declares, so its rows exist and read
  "restart / unavailable" rather than vanish.

### Quality presets: `Ladder`

`knob.Ladder(driver, rungs…)` makes the knob's `Default` the rung of a dropdown `driver`'s
current choice (`rungs[driver.ChoiceIndex]`), so:

- an **unset** key binds to that rung -- `V`, `Boot` and `Default` all move with the preset, and a
  later release's retune reaches every knob nobody touched;
- a **set of the driver** moves every member to the new rung as a set (`Rung`), dropping the
  player's deviation exactly as a reset does; the cascade is recursive, so
  `quality.preset` → `quality.<area>` → the real knobs;
- `cx.cfg` keeps only what differs from the rung (off-default knobs only, §2), which is what
  makes a preset a default rather than a stamp.

Declared right after the knob, before any reader. A ladder whose rung count does not match the
driver's choices, or whose rung is not a value of this knob, is **refused** (logged, and the
SelfCheck fails). Chat still accepts any in-range value for a ladder dropdown. §4.6 lists every
ladder.

### Binding

- `Settings.Load` stays pre-main and unchanged: it loads the raw dictionary from `Settings.Path`.
- Constructing a knob binds it: look up its key, `Parse` per T, clamp to `[Min,Max]`, set `V` and
  `Boot`. Unparseable → `Default`. A static field is therefore correct on first touch.
- Keys no declaration claims this session (a Windows-only knob on Linux) are preserved on write.

### Constraints the registry enforces

- Layout-affecting knobs are dropdowns **by construction**: only the `Dropdown` factory has a
  `layout:` parameter.
- User tier requires help: the factory refuses (logs, demotes to Advanced); the SelfCheck fails on
  it so `run-demo.sh` catches it.
- A Restart knob's `Boot` is immutable after a set (SelfCheck).
- A Button has no value, no default, is never persisted; tier decides row vs command.
- A key declared twice, a knob colliding with a `Bag` prefix, and a refused `Ladder` are
  violations: logged at declaration, and the SelfCheck fails on the recorded list.

### The live lint (`KnobGroup.SelfCheck`)

The suite walks every LIVE declaration, not a throwaway group only, so a real knob that breaks a
rule fails `run-demo.sh` (ADR 0030 §5):

- the three constraints above, over every declared knob;
- **`MaxUserRows = 14`**: User rows one submenu may hold, every group in it counted (link rows
  excepted). Past that it is a wall of dials -- merge, or move tuning to Advanced.
- **`DevWords = { "A/B", "debug", "diagnostic", "bisect" }`**: no User or Advanced row's label or
  help may contain one. A knob that needs those words is ChatOnly.
- the command gate: `/settings` always dispatches; every generated `/<group>` only when
  `general.commands` is on.

### Threading

Panel/chat set `V` on the input thread; the render thread reads it. A scalar field write is
atomic on x64/Win64 and no two knobs need to change together, so **plain field**: no
`Volatile`, no store-swap (the `KnobPanel.Publish` precedent stays for row lists).
`Changed` runs on the setter's thread.
Ceiling: the day two knobs must flip together, that pair becomes one `Knob<T>` with an object `T`.

### Derived surfaces

- **Chat:** one generated handler per group, whether or not the group is panel content.
  `/<group>` reports (`Status` where it has one, else every row's value, then each link row's
  address), `/<group> <knob> <value>` sets (Toggle accepts on/off/0/1, Dropdown a value, a label,
  a number inside a numeric ladder's span, or an index), `/<group> <button>` presses,
  `/<group> reset` restores every Default. No aliases.
- **cx.cfg:** Format per T: Toggle `0/1`, Slider invariant float / int, Dropdown the stored
  `Values[i]` (`post.tonemap=aces`, `ui.font=14`), Swatch `rrggbbaa`, Text raw, List the items
  joined with `|`.
- **/knobs:** walks groups in declaration order, `key=value (default d) [tier] [restart]` one
  per knob, `(boot b)` when it differs, then every `KnobSurface.Note` supplier. General » About's
  *Copy my settings for a bug report* puts the same text on the clipboard. Gated command.
- **Panel row = the Knob.** No `SetAct` for anything global.

## 2. The file

- The panel writes **`Settings.Path`**, the file `Settings.Load` latched pre-main; never
  `Native.ConfigPath`. The `Ready`/deferral gate leaves the write path. A `-userdirectory`
  client keeps its cx.cfg under the default dir, as its boot keys already do.
- **Off-default knobs only** (`V != Default`). Reset deletes the keys; an untouched knob follows
  a later release's default.
- **Generated:** one fixed header comment ("written by CX; comments are not preserved; edit
  with the client closed"), then one alphabetical list of (off-default declared keys ∪ unknown
  keys verbatim).
- **Read once, pre-main.** No re-read on panel open, no watcher; a hand edit mid-session is
  overwritten by the next write-back, which the header says.
- **Write:** every set restarts a ~1 s trailing timer; the timer fires on a pool thread, walks
  the registry, `Format()`s each knob, writes `cx.cfg.tmp`, then `File.Move(overwrite)`. Last
  writer wins. No write-if-changed compare. The render thread never touches the disk; the input
  thread only arms the timer.
- **Boot keys** are renamed `<group>.<knob>` (`alloy.gl46`, `shadow.size`, `mouselook.sens`,
  `general.logs`) with **no legacy read** of the flat name. `general.logs` is still consumed
  inside `Settings.Load`.
- **Restart rows** show `V` with the `↻ restart` badge, amber when `V != Boot`.
- **Retired:** `cx-camera/chat/combattext/dialog/nameplate/quickbar/ui.cfg` writers and their
  `ToText`/`Parse` round-trips; `ConfigFile.Load/Save/Baseline`. Surviving: `ForEachPair`,
  `ReadTextNow`, `ReadPath` (the #530 `sowx.cfg` fallback), `Ready` (HotkeyStore's gate).
  `cx-hotkeys.cfg` keeps exactly the `[token=hex]` consent sections.
- No migration code: there are no users yet.

## 3. The panel

Pinned by the #560 prototype, Variant B (Roomy).

- **Geometry:** one centred window ≈70 %×75 % of the screen, draggable, not resizable, Escape
  closes, takes keyboard focus. Columns ≈ 18 / 22 / 60, each scrolling independently, scrollbar
  in the chat window's chrome palette.
- **Header:** title, a dashed *reserved* search slot (empty; future search spans all
  categories), close.
- **Categories:** **General · Interface · Graphics · Audio · Controls**
  (`KnobPanel.KnobCategories`), in that order.
- **Submenus are shared.** The middle column lists the category's submenu *names*; every group
  that names one is content of it, in `Order`, and a shared submenu heads each group with its
  `Title`. A group that has no User or Advanced row and no `PanelStatus` is not panel content at
  all (`Shown`) -- it has no submenu and no heading, only its chat command and its `/knobs` lines.
- **Row** = label with **one line of help beneath it**, control right-aligned; ≈3.3 em at 14 px
  base (a row without help, or an Advanced row, is compact at 2.4 em). The `(?)` tooltip marker
  appears only on Advanced rows that have help.
- **Master toggle** = boxed first row of its group, bolder label. **Sections** = small-caps
  headers. **Advanced** = ONE collapsed `▸ Advanced` disclosure at the bottom of the submenu;
  open, it holds every group's Advanced rows, each under its `Title` heading in a shared submenu.
- **Greying is per group.** Each group of the submenu carries its own gate -- its own master plus
  every ancestor's through `parent:` -- and an off gate greys that group's *value* rows to 42 %
  and makes them inert. One group's off master never touches another's rows. **Master rows,
  action rows and link rows stay live**, tree-wide, no per-row flag. A `Disabled` or
  server-enforced knob greys on its own.
- **The strip** sits above a group's rows whenever a gate is off, and comes in two shapes:
  an off *ancestor* reads "<Label> is off — these settings apply once it is enabled in
  <Category> » <Submenu>" (plus "(restart)" for a Restart master) and is not pressable; the
  group's own **ChatOnly master** reads "<Label> is off (<key>=0 in cx.cfg) — click here to turn
  it back on" and **pressing it sets the master on** and re-lays out. A gate flipped from chat
  since the layout re-lays out before the next hit test (`StripsStale`).
- **Off-default marker** = blue dot at the row edge + a dot on the submenu name. **Reset to
  defaults** top-right of the knob column, per submenu: `ResetRows` on every group in it, User +
  Advanced rows only.
- **Restart badge** `↻ restart`, amber when `V != Boot`.
- **Dropdown:** click opens a list; the list scrolls (font size has 27 entries); past 8 entries a
  search row sits above it and typed text filters by substring, Enter picks a lone match (CX-681). Replaces the
  cycle button everywhere.
- **Slider:** inline value box; applies while dragging.
- **Swatch:** expands to R/G/B/A channel rows in place.
- **Status line:** a read-only panel element above a group's rows, and **`PanelStatus` only**
  (Display's "Running: Alloy / Vanilla — <reason>"; Preset's "High, 3 settings changed from
  it."; Hotkeys' "Approved on this server: …"). A group's diagnostic `Status` never reaches the
  panel. A group's `Header` note, where it has one, leads the status line.
- **Link row:** address text; press copies it to the clipboard
  (`SymbolMap.SdlSetClipboardText`, resolved on both platforms), the row flashes "copied".
- **Face:** the panel draws in its own fixed **20 pt design × GUI scale** (the FPS overlay's
  `FacePx` pattern), never in `ui.font`.
- **Apply model:** instant apply, debounced write.

### Doors

1. **`/settings`** — always dispatches, regardless of `general.commands` and of
   `chat.enabled`. Seam: a `Chain` on `CGuiInGameChatPrompt::HandleEnter` (Linux `0x679c50`
   nm; Windows needs an `At` row, the class is not exported) that reads the prompt's edit text,
   runs `CommandRegistry.TryDispatch`, skips the original on a hit. The hotkey observer already
   sits on the same symbol. Consequence: with the gate on, every `/<group>` command dispatches
   from the vanilla prompt too.
2. **The gear** at the main menu: 64 px at 1080p, 24 px inset lower-left, scales with the game
   UI scale, brightens and turns on hover; a glyph-atlas sprite. Main-menu only by requirement.
   Build facts from the research: present and the roll-call already run at the menu (only
   content gates on `InGame`); `SyncUiScale`'s `InGame` guard lifts; the #451 yield
   (`GuiObjectHitCheck` mask 1) would hand every menu press to the viewport-filling
   `CMainMenuBackgroundPanel`, so the gear needs an exemption from the yield; discriminators
   `CClientExoAppInternal+0x98` (live `CMainMenuPanel*`) and `+0x5f0` (reached-menu latch),
   both `Same` on Windows. Owed live checks before the gear ships: `/keydiag` click over empty
   menu background (`gui=` non-zero?); the pixel anchor via `MapGuiSceneToScreen` (sret) at the
   user's resolution; the Windows menu was not looked at.

## 4. The tree

Categories in order: **General · Interface · Graphics · Audio · Controls**
(`KnobPanel.KnobCategories`). Every group declares an `order:`; submenus appear in the order of
their lowest-ordered group, and a shared submenu heads each group with its `title`. The orders
below are the declarations'.

Groups with no User or Advanced row and no `PanelStatus` have no submenu at all: `water`,
`emitsim`, `view`, `models`, `mdl`, `dbtiles`, `knobs`. Their keys, their `/<group>` command and
their `/knobs` lines are unchanged; they are simply not panel content (ADR 0030 §2).

A ChatOnly family is one row per group here — the key list, the kind where it is uniform, and
what the family is for. `/knobs` is the complete list.

### 4.1 General

**General** — group `general` (100), no master. Header: "No master switch: every feature has
its own, and *no CX* is an uninstall."

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| general.commands | Enable chat commands | T | off | U | L | — | Lets `/grass`, `/shadow` and the other CX commands work in chat. Off, they go to the server like any other text — some servers use the same names. `/settings` always works. |
| general.logs | Write cx.log for bug reports | T | off | A | L | — | Writes what CX is doing to cx.log (beside cx.cfg on Windows, in your home folder on Linux) from now on; restart to capture the launch lines too. Attach the file to a bug report. |
| general.hookdebug / presentprobe / pin / resizeat | — | T, S | as declared | C | R | — | funchook trace, the present-time pixel probe, main-thread affinity, the timed window resize |

`general.logs` is live because `ExtenderLog.Enabled` is one static bool. No ↻ badge.

**About** — group `about` (110), no master, no value row. `PanelStatus` (and `Status`) =
`Product.About`: product name + version (`AssemblyInformationalVersion`), author, contact, from
the one `CX.Framework.Product` const set that `HotkeyProtocol.AbuseContact` also reads. Bare
`/about` prints the same three lines, then the link.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| about.releases | Releases | Lk | `Product.Url` | U | — | — | link row: press copies the address |
| about.copy | Copy my settings for a bug report | B | — | U | — | — | Copies every CX setting, and what CX found when it started, to the clipboard -- paste it into a bug report. |

The `knobs` group — the `/knobs` walk plus every `KnobSurface.Note` supplier (symbol rot,
EngineLayout, font registry state, VRAM, camera feel, post-stack hosting) — has no row and no
`PanelStatus`, so it is a chat surface with **no submenu**. *Copy my settings for a bug report*
puts exactly that text on the clipboard; a refused clipboard logs loudly and never looks like a
copy.

### 4.2 Interface

**Windows & text** — groups `ui` (200), `fps` (202, *FPS overlay*), `nui` (204, *NUI windows*).

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| ui.font | Text size | D | 6 … 32 pt, every point · 18 | U | L | — | Size of the text in every CX window and label. Each element may set its own in its Text rows; a chat window on its gear. |
| ui.typeface | Typeface | D | `default` + every `font.<name>` + every installed font (full name) · default | U | L | — | The font every CX window draws with: the built-in DejaVu Sans, a `font.<name>` from cx.cfg, or one installed on this machine. Each element may pick its own in its Text rows. Type in the list to search. |
| ui.look | Look | D | Game art / Flat colour · Game art | U | L | Look | Every CX window, chat included: Game art wears NWN's own GUI textures and colours (a server may re-skin them or hold this choice); Flat colour draws plain panels in the palette below. Server-holdable. |
| ui.palette | Palette | D (open) | Slate / Ember / Moss / Wine / Ash / Custom / every `cxpal_<id>` found · Slate | U | L | Look | The colours of every CX window in the Flat colour look: a built-in set, Custom, or a palette file (the player's in override/, or the server's content). An id not found yet is kept and draws Slate. Server-holdable. |
| ui.custom_&lt;role&gt; | the role's label (Window, Accent, Text, ...) | Sw | slate's colour per role | A | L | Custom palette | 28 rows, one per role; editable while Palette is Custom. |
| ui.customfrom / customsave / customcopy / custompaste | Start from the previous palette / Save to override folder / Copy palette to clipboard / Paste palette from clipboard | B | | A | | Custom palette | Seed Custom from the last palette; write `override/cxpal_<customname>.txt`; the palette as file text to the clipboard; read that text into Custom. |
| ui.customname | Palette file name | X | mine | A | L | Custom palette | The id Custom saves under: 1-10 of a-z, 0-9, _. |
| ui.edgesnap | Snap to screen edges | T | on | U | L | — | Dragged windows stick to the screen edge and to each other. |
| ui.textedit | Modern editing in game text boxes | T | on | U | L | — | The game's own text boxes get selection (mouse drag, double-click, Shift+arrows, Ctrl+A), Ctrl+C/X/V, and Ctrl+arrow/Backspace/Delete by word. Needs the Alloy renderer. |
| ui.streaming | Show streaming progress | T | on | U | L | — | A thin bar at the top of the screen while the area is still arriving after the load screen: textures, geometry and light charts. You can play meanwhile; it hides once everything has landed. Needs the Alloy renderer. |
| ui.hoverfocus | Focus a window by hovering | T | off | A | L | — | A CX window takes the keyboard when the pointer rests on it, without a click. |
| ui.snap | Snap windows to a grid | T | off | A | L | — | Dragged windows land on grid points. |
| ui.gridpx | Grid size | D | 25 / 50 / 75 / 100 / 150 / 200 px · 50 | A | L | — | Spacing of the grid points, in screen px. |
| fps.enabled | Show FPS and ping | T | **off** | U | L | — | Frame rate, frame time and server round-trip in the top-right corner. |
| fps.font / typeface / outline | Text size / Typeface / Text outline | D | 14 · default · off | A | L | Text | the readout's element text rows (below) |
| nui.enabled | Enable CX NUI fixes | T | on | U | L | — | Speed and usability fixes for the windows servers build with NUI. Off, they behave exactly as in the stock client. |
| nui.smooth | Smooth scrolling | T | on | U | L | — | The mouse wheel glides a NUI list to its new position instead of jumping there. |
| nui.autohide | Hide scrollbars with nothing to scroll | T | on | U | L | — | A NUI list whose content fits shows no scrollbar, instead of a full-length one that does nothing. |
| nui.hoverbars | Show scrollbars only under the mouse | T | off | A | L | — | A NUI list's scrollbar fades out while the pointer is elsewhere and back in when it is over the list. Text boxes keep theirs. |
| nui.itemicons | Item icons in NUI windows | T | on | U | L | — | A server's NUI window can show an item's own icon in its real colours, as the inventory does. Off, those images show the stock error sprite. |
| nui.explain | Explain broken layouts | T | on | A | L | — | Lists the rules that contradict each other and the elements they belong to, instead of only "The constraint can not be satisfied." |
| nui.thumb | Shortest scrollbar handle | S | 0 … 4 × its width, step 0.5 · 2 | A | L | — | A long list's scrollbar handle never gets shorter than this. 0 = stock. |
| nui.cull / solve / solvecheck | — | T | on / on / off | C | L | — | the widget skip, the fast layout solve and its step-by-step check |

Bare `/nui` prints how many widget draws the skip saved since the last ask, and how many
tableau rows the layout solves visited of how many. The `@item:` contract: a NUI image whose
resref string is `"@item:" + ObjectToString(oItem)` draws that item's own inventory icon
(CX-155).

**Chat** — groups `chat` (210), `snippets` (212, *Shortcuts*, parent `chat.enabled`). Chat's
header: "Window animation, background shape and tabs are on each window's own gear." The chrome
colours are not chat's: every chat window reads the shared look (`ui.look` / `ui.palette`), so its
background is `PanelBg`, a gradient runs to `NavOn`, the header band is `HeaderBg`, the accent
`Accent`, the input bar `Input`, the selection `Selected` and the outline `Border`, each at the
alpha that surface draws. Under `ui.look=native` the window wears NUI's own art instead — the
nine-sliced frame, a button per tab, the edit box under the input line, and the engine's 18 px
scrollbar, which the text width reserves and the thumb hit-test shares.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| chat.enabled | Enable the CX chatbox | T | on | U | **R** | — | CX's own chat: multiple windows, tabs, portraits, tells routing. Off, the stock chat window runs. Takes effect at the next launch. |
| chat.tellopen | When a tell arrives | D | nothing / open a tab / open a window · nothing | U | L | — | Where a tell from someone new lands: nowhere special, a new tab in the main window, or its own window. |
| chat.notify | Desktop notification when a player speaks | T | **off** | U | L | — | While the game window is in the background, a line another player's character says to you (talk, whisper, shout, party, tell, or a DM speaking) shows as a desktop notification. Server messages, combat and feedback never do. |
| chat.complete | Complete names with Tab | T | on | U | L | Typing | Tab finishes the name you started typing: the people and creatures around you, nearest first, then everyone online. Press Tab again for the next match, Shift+Tab for the one before. |
| chat.markup | Colour emotes, speech and OOC | T | **off** | U | L | Typing | While you type, `*emotes*`, "speech" and ((out of character)) text take their own colours, so a missing closing `*` shows before you send. |
| chat.mouse | Place the cursor with the mouse | T | on | U | L | Editing | Click in the input bar to move the cursor, drag to select, double-click to select a word, triple-click for the whole line. Shift+click extends the selection. |
| chat.drafts | Keep unsent lines | T | on | U | L | Editing | Escape closes the input without losing what you typed; it is back the next time you open chat. Each tab keeps its own unsent line. |
| chat.histsave | Remember sent lines between sessions | T | on | U | L | History | Lines you sent stay in the Up-arrow history after a restart, in `cx-chat-history.txt` beside `cx.cfg`; turning this off deletes the file. |
| chat.pairs | Close brackets and quotes | T | on | A | L | Typing | `(` `[` `"` or `*` adds the closing mark; typing it again steps over it; with text selected the mark wraps it. |
| chat.counter | Show a character count | T | off | A | L | Typing | A running count of the characters in the line, at the right end of the input bar. A line holds up to 4000. |
| chat.delword | Delete whole words with Ctrl | T | on | A | L | Editing | Ctrl+Backspace deletes the word before the cursor, Ctrl+Delete the word after it. |
| chat.undo | Undo and redo | T | on | A | L | Editing | Ctrl+Z takes back the last change to the line, Ctrl+Y (or Ctrl+Shift+Z) puts it back. |
| chat.rows | Arrow keys move through a long message | T | on | A | L | Editing | Up and Down move the cursor between a wrapped line's rows; they reach the history only from the first or last row. |
| chat.histsearch | Search history by what you typed | T | on | A | L | History | With text in the line, Up recalls only earlier lines that start with it. |
| chat.emotecol / speechcol / ooccol | Emote / Speech / OOC colour | Sw | e0b872ff / f2f2e6ff / 8c96a0ff | A | L | Markup colours | alpha ignored (text draws opaque) |
| `w<N>.*` (rect, toggles, inputtop, sendch, anim, hold, fade, tau, outline, font, typeface, tsfmt, tssrc, align, bgmode, bgangle, bgstart, bgend, autohide, autohideframe, locked, tellshere, t<M>.*) | — | per-window state (`KnobBag "w"`) | | H | | — | edited on the window; a submenu reset leaves it alone. A per-window colour key from an older file is counted, logged once and dropped |
| snippets.enabled | Expand chat shortcuts | T | on | U | L | — | Type a shortcut, then a space (or Enter), and it turns into its text. Ctrl+Z right after takes it back. |
| snippets.list | Shortcuts | L | empty, hint `;bow *bows deeply*` | U | L | — | Each entry is the word to type, a space, then what it becomes. Enter adds the next one; clear an entry or press × to remove it. |

**List** (kind **L**, `KnobKind.List`, `Knob<TextList>`): an immutable string list with value equality. The
panel lays one item row per entry under the label -- a text box and a remove × -- and a trailing empty box
showing the knob's `Hint`, which appends; Enter there appends and opens the next add box; an empty commit
removes an entry. Items take 240 characters (`KnobPanel.ItemMax`; other boxes keep 64) and a box being typed in
scrolls to its end. `cx.cfg` and the chat form hold the items on one line joined with `|`, a `|` or `\` inside
an item escaped with `\`.

`/newwin` / `/closewin` stay (actions). Chat's `SettingsWindow` survives as the per-window
editor: since CX-696 it *is* `KnobPanel` (a row `Source` + `Publish`, `SubFrac = 0`, `Modal`)
with ONE category column -- *Window settings*, one entry per kept tab, *Add new tab...* -- and
the font rows are the window's own (`w<N>.font` / `w<N>.typeface`, absent = follow `ui.font` /
`ui.typeface`; `w<N>.outline` off / thin / thick), drawn from a face of its own on the shared
sheet. Its *Background* section holds only the fill's shape — `bgmode`, and for a gradient the
direction and the two stops — since the colours it fills with come from the shared look.
The tab bar keeps only the gear: a new window is the panel header's *Copy this window*,
closing is the window category's *Close this window* (two presses; Main has no row).

**Names & combat text** — groups `nameplate` (220, *Nameplates*), `combattext` (222, *Combat text*).

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| nameplate.enabled | Enable CX nameplates and chat bubbles | T | on | U | L | — | Names, health and speech bubbles drawn by CX: sharper text, portraits, stacked lines. Off, the stock game's names and bubbles draw. |
| nameplate.background | Background | T | on | U | L | Nameplates and bubbles | A box behind names and bubbles, as in the stock game. |
| nameplate.maxdist | Visible distance | S | 0–100 m step 5 · 45 | U | L | Nameplates and bubbles | Names and bubbles past this distance are not drawn. 0 draws them all. |
| nameplate.portraits | Portrait | T | on | U | L | Nameplates | A portrait beside the name. |
| nameplate.healthband | Health colour | T | on | U | L | Nameplates | The name is tinted by the creature's health, as in the stock game. |
| nameplate.healthbar | Health bar | T | off | U | L | Nameplates | A thin bar under the name. Exact for you and your party; others by injury level. Not in the stock game. |
| nameplate.whiletalking | Show while talking | T | off | U | L | Nameplates | Hovering or Tab still shows the nameplate of someone talking, under their bubble. The stock game hides it until they finish. |
| nameplate.speaker | Speaker | D | Name and portrait / Name only / Portrait only / Neither (stock) · Name and portrait | U | L | Chat bubbles | Who is talking: their name above the bubble, their portrait beside it. A nameplate shown under the bubble takes over what it already shows. |
| nameplate.queue | Messages kept | D | 1 / 2 / 3 / 4 · 2 | U | L | Chat bubbles | How many recent things someone said stack in their bubble. The stock game keeps 1. |
| nameplate.fadedist | Fade band | S | 0–100 m step 1 · 10 | A | L | Nameplates and bubbles | They fade out over this distance before Visible distance. |
| nameplate.width | Text width | S | 120–480 px step 10 · 300 | A | L | Nameplates and bubbles | The widest a name or a bubble gets before wrapping. |
| nameplate.clicktotell | Click a name to tell | T | off | A | L | Nameplates | Clicking the name starts a tell. Takes the click the world would have had. |
| nameplate.font / typeface / outline | Text size / Typeface / Text outline | D | as UI · default · thin | A | L | Nameplates | the names' element text rows |
| nameplate.channelstyle | Colour by channel | T | on | A | L | Chat bubbles | Shouts, whispers and emotes get their own colour and emphasis. |
| nameplate.bubblefont / bubbletypeface / bubbleoutline | Text size / Typeface / Text outline | D | as UI · default · thin | A | L | Chat bubbles | the bubbles' element text rows |
| nameplate.anim / portraitsize / lines | — | S, D | 0–1000 ms · 180; 2–6 lines · 3; 1–32 · 32 | C | L | — | bubble animation, portrait height, wrapped lines kept per message |
| nameplate.probe | — | B | — | C | — | — | anchor dump on the next frame |
| combattext.enabled | Enable CX combat text | T | on | U | L | — | Damage numbers and combat banners drawn by CX: stacked per target, outlined, animated. Needs CX nameplates, which anchor them. Off, the stock floating text draws. |
| combattext.max | Numbers per target | S | 1–16 · 6 | U | L | — | Damage numbers shown on one creature at once; older ones are dropped. |
| combattext.life | Number lifetime | S | 200–6000 ms step 100 · 1200 | U | L | — | How long a damage number stays. |
| combattext.bannerlife | Banner lifetime | S | 200–6000 ms step 100 · 1500 | A | L | — | How long a banner (*Sneak Attack!*) stays. |
| combattext.bannersize | Banner size | S | 1–4 × step 0.1 · 1.3 | A | L | — | Banner text relative to a damage number. |
| combattext.font / typeface / outline | Text size / Typeface / Text outline | D | as UI · default · thin | A | L | Text | the numbers' and banners' element text rows; the resting size of a number, the peak face follows |
| combattext.rise / stack / lift | — | S | 0–400 px · 52; 0–120 px · 20; 0–300 px · 34 | C | L | — | rise distance, stack spacing, banner lift |

**HUD** — groups `quickbar` (230, *Quickbar & compass*), `radial` (232, *Radial menu*).

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| quickbar.enabled | Enable the CX quickbar layout | T | on | U | L | — | Centres the quickbar correctly and lets it sit left or right with background bars. Off, the stock quickbar position is used. |
| quickbar.align | Position | D | left / centre / right · centre | U | L | Quickbar | Where the quickbar sits along the bottom edge. |
| quickbar.bars | Black bars | T | on | U | L | Quickbar | Full-width bars behind the quickbar and under the chat. |
| quickbar.hidden | Hide the quickbar | T | off | U | L | Quickbar | Moves the quickbar off-screen. |
| quickbar.compass | Show the compass | T | on | U | L | Compass | The compass panel of the stock HUD. |
| radial.enabled | Enable the CX radial menu | T | on | U | L | — | Right-click menus as a nested dropdown drawn by CX. Off, the stock radial menu runs. |
| radial.lock | Lock the layout | T | off | U | L | — | Rows cannot be dragged and right-clicking one opens no edit popup. Turn off to rearrange again. |
| radial.mode | Submenus | D | Cascade beside the row / Drill down in place · cascade | U | L | — | Cascade: a submenu opens beside its row, on hover. Drill down: it replaces the list where it is, on click, with a Back row on top -- the pointer never travels. |
| radial.opacity | Opacity | S | 0.2–1 step 0.05 · 1 | U | L | Look | The whole menu's opacity. Its colours follow Interface > Look. |
| radial.anim | Animate | T | on | U | L | Animation | Lists fade and slide in as they open. |
| radial.dwell | Submenu delay | S | 0–600 ms step 10 · 250 | A | L | — | Cascade only: how long the pointer rests on a row before its submenu opens; 0 opens it at once. |
| radial.animms | Animation length | S | 40–400 ms step 10 · 120 | A | L | Animation | How long a list takes to appear. |
| radial.font / typeface / outline | — | D | as UI · default · off | C | L | Text | the menu's element text rows |
| `radial.p<N>.*` (who, root, path, at, fn, param, hide, label, icon, folder, in) | — | pinned / reordered / hidden rows, folders (`KnobBag "p"`) | | H | | — | edited by drag-and-drop in the menu and its row actions; the group reset leaves it alone |

Radial's header: "Drag a row to reorder it, or drag a deep row onto the top list to keep it
there; right-click a row for the same edits, Hide and Reset."

The rearranged layout (CX-692) is content, not settings, exactly as chat's `w<N>.*`: one
`KnobBag` (`radial.p<N>.*`), in-file, no row, no command, reset-proof. Not a separate state
file: the hotkey-consent exemption exists because a token is a secret, not because it is
dynamic. One layout per (server, character): every row carries `who=<server ip:port>|<character
name>` and the menu shows the rows of the identity it opened under, carrying the others through
each save; a row without `who` (a pre-CX-694 file) is adopted by the first identity that loads.
The server half is the connected `ip:port` (`CNetLayer::GetServerNetworkAddress`): a server
that changes address starts a fresh layout, and through the master-server relay every relayed
server shares the relay's address. The character half is the creature's first + last name off
its stats block, not the display name: a disguise (`SetName`) rewrites the override field and
leaves these alone, so it keeps the layout. A real rename starts a fresh one. The servervault
`.bic` resref would be stabler still, but the client keeps no copy of it after login: `CClientExoApp::SetLocalFileName` is called only by the local/DM login sends
(`SendPlayerToServerLogin_LoginLocalCharacter` / `_LoginDMCharacter`, and
`CChooseCharTypePanel::CheckForDefaultCharacter`), and `_LoginServerCharacter` writes its
by-value `CResRef` straight into the message from `CPlayModulePanel::HandlePlayButton` (Ghidra,
2026-09-21); reading it means hooking those three unexported sends. `ponytail:` swap the half
if renames turn out to matter. Each row `p<N>` is one of four things, told apart by `fn`,
`hide` and `folder`:

- a **hoist** (`fn` set): `root=<RootKind>` (`Creature`, `Placeable`, ...), `path=1/4/3/0`
  (the vanilla slot path from the root, the leaf last), `fn=<the leaf handler's Itanium
  name, e.g. _ZN16CClientSpellMenu14RadialMenuCastEl>` (`vt:<off>:<_ZTV>+<n>` for a virtual
  handler, `sub` for a hoisted submenu, `rva:<hex>` on Windows -- the handlers are not
  exported there, so the symbolic half is Linux-only by design), `param=0x<hex>` (the raw
  packed param, the engine's own quickbar identity), `at=<index among the slot rows of the top
  list>`, `label=<the breadcrumb at pin time, "Cast Spell > Wizard > 3 > Fireball">`;
- a **reorder** (`fn` absent): `root`, `path=<the row's own slot path>`, `at=<index within
  its own list>` -- a top-level row moved, or a row moved inside a submenu;
- a **hide** (CX-694, `hide=1`, `fn` absent): `root`, `path=<the row's own slot path>` -- the
  row is dropped from its list; it claims no index. A list's last slot row cannot be hidden, and
  hides that would empty a list are ignored (every row shows), so *Show hidden* stays reachable.
  *Show hidden (N)* in a row's popup drops the list's hides; *Reset layout* drops them too;
- a **folder** (CX-694, `folder=<name>`, no `path`, no `fn`): `root`, `at=<index in the top
  list>` -- a named submenu row of the top list. A hoist carries `in=<folder name>` to sit in
  it; the folder's members are a list of their own for the position model, so a reorder inside
  it never moves a top-level row. One level only (`ponytail:` a folder in a folder is the
  cascade depth the feature exists to remove). Names are unique per root: a rename onto an
  existing name merges the two. *Remove folder* returns the members to the top list. A member is
  a hoist or a **filed slot**: a top-level vanilla row (*Attack*, *Cast Spell*) has a one-step
  path no hoist can walk, so it files as its reorder row carrying `in=<folder name>` -- the slot
  leaves the top list and the folder shows the root's own row for it (a slot the root has no row
  for shows greyed under the label saved at file time). It is the same row kind as a reorder, so
  on the creature kinds (one vptr, self / other / party) it moves whatever sits in that slot,
  never greys. A hide of the slot drops its filing, as it drops a reorder. A slot that already
  carries a self pin at its path (*Everywhere (as self)*) is that pin's row: its popup edits the
  pin, so *Move to ▸* files the self pin, not the slot.

**Also show on** (CX-694): a hoist copied to another root kind is a row of that kind with the
same `path`, `fn` and `param` -- not a synthetic action: the other root must already build the
same subtree, and then the copy is that root's own deep leaf surfaced at its top. It resolves because every world root builds the same subtree
under the same slot with *its* target: slot 1 on the self, other-creature, placeable, door and
ground roots constructs the caster's own `CClientSpellMenu` (`CGuiCreatureMenu::CreateClientSpellMenu`
@0x73b360, `CGuiPlaceableMenu::CreateMenuByID` @0x7ecc30, `CGuiDoorMenu::CreateMenuByID`
@0x7569f0, `CNWCTile::CreateMenuByID` @0x80c160 -- the placeable's with its position and id,
the ground's with `CNWCTile::m_vSpawnPoint` and no object; Ghidra 2026-09-21), and
`CRadialMenu::CreateSubMenu` @0x584790 runs a slot's creator whether or not the slot is
disabled. So *Fireball* pinned from the self menu and shown on *Placeables* fires the
placeable root's own cast leaf at the placeable, through the engine's handler, with no new
native surface. The target rule is the engine's: the spell creators (`CreateSpellSubRadial`
@0x6f0590, `CreateSequentialSpellMenu` @0x6f1f80) call `CClientSpellMenu::TargetIsValid`
@0x6f0450 -- `spells.2da TargetType` & the clicked object's type (self / creature / ground /
item / door / placeable / trigger) -- and `DisableEntry` the leaf when it fails, so a spell that
cannot target what was clicked shows greyed on that root exactly as it would in the vanilla
cast menu there. Not built: casting at the object's *position* when only the ground bit fits
(vanilla has no such fallback either; the *Ground* root carries the same pin). The popup
offers Creatures, Placeables, Doors and Ground: the item root's slot 1 lists the item's own
spells, and the quickbar and chat roots have no target. A row whose subtree does not exist on
the other root (a special ability shown on Placeables) greys there like any stale hoist.
**Show everywhere (as self)** (CX-694): an action the self menu fires is a self action -- its
target is always the local creature -- so it may sit on every root. The popup offers it on a leaf
whose handler is one of a fixed table (`SelfActions`): `CGuiCreatureMenu::RadialMenuUseFeat` /
`Emote` / `ToggleMode` / `QuickChat` / `UseSkill` / `Rest` / `CharacterSheet` and
`CClientSpellMenu::RadialMenuCast` / `CastSpellLikeAbility` / `UseFeat`, each with its Windows
anchor. Picking it writes one row per world root (Creatures -- every other creature's menu too --,
Placeables, Doors, Items, Ground, Traps) with `fn=self:<key>`, `param`, `label`, `icon` (there
is no live leaf to read one from); a walked hoist already at that path turns into the self pin
where it sits. It is offered on a top-level self leaf too (*Character sheet*), whose one-step
path no walked copy could take. The row is fired on a **stand-in** of
the self menu: the engine's own `CGuiCreatureMenu(CNWCCreature* self, Vector)` /
`CClientSpellMenu(Vector, self id, null, build=0)` ctor on `operator new` memory, ring-less,
`m_bAutoDelete` set, torn down with the held root -- so the handler behaves exactly as on the
self menu (a feat that needs a target opens the targeting cursor there too). A spell-class row is
greyed when `TargetIsValid` refuses the spell at self, which is the self menu's own greying of a
hostile-only spell. No count column (uses left are not read). `ponytail:` the table is the
clonability rule; a self-menu leaf outside it (associate commands, party actions) is not offered.
Validation masks bit 37 of the param on both sides: `CreateSpellSubRadial` sets it only while a
spontaneous caster has no uses of the spell left and `CGuiQuickButton::SetButtonSpellMenuID`
@0x6aac50 clears it as transient.

Editing (CX-694): left-drag a row to a gap of its own list to reorder it; drag a deeper row to
a gap of the top list to hoist it there (a Wizard drops *Fireball* onto the top list instead of
walking *Cast Spell > Wizard > 3* each time). A leaf activates on the release of an undragged
press. The right-click popup keeps the same edits as rows (Move to top / Pin to top, Unpin,
Move up, Move down, Hide, Show hidden, Reset layout) for keyboard-free precision, and adds
*Move to ▸* (the root's folders, *Top level* from inside one, *New folder…*) on every row and
*Also show on ▸* on any hoist or deeper row (a top-level slot only as self); a folder row offers
*Rename…*, Move up / down, *Remove folder*. A folder's open list takes drops of any row -- a hoist,
a top-level slot, a deeper vanilla row -- and gives them back to the top list. Naming is typed into the
popup row itself (`ponytail:` ASCII off the SDL keycodes the cascade already swallows; Enter
commits, Esc cancels -- `SDL_TEXTINPUT` is the upgrade when a non-ASCII name matters).

The packed spell param carries the class *slot*, so on a character where the path no longer
yields a leaf with the same `(fn, param)` (or a step's creator declines) the row shows
greyed -- its leaf label, the parents' breadcrumb in the count column -- and is never invoked.
Positions: a row claims `at` (first free index at or after it), the unclaimed vanilla rows
flow in slot order; no rows is vanilla order, *Reset layout* drops the root kind's rows.

**Game windows** — groups `dialog` (240, *Dialog*), `inventory` (242, *Inventory*).

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| dialog.enabled | Enable the CX dialog window | T | on | U | L | — | Conversations in CX's own window: resizable, scrollable, portrait, animated. Off -- or while the HUD is hidden, as in a cutscene -- the stock dialog panel runs. |
| dialog.lock | Lock position and size | T | off | U | L | — | The window can't be dragged or resized. |
| dialog.maxheight | Height before scrolling | S | 120–800 px step 10 · 320 | U | L | — | The window grows with the text up to this height, then scrolls. The resize grip sets it too. |
| dialog.justify | Justify text | T | off | U | L | — | Stretch lines to the window width. Off is ragged right. |
| dialog.anim | Animations | T | on | U | L | — | Text fades in and out between nodes. Off, it snaps. |
| dialog.fadein / fadeout | Fade in / Fade out | S | 0–500 ms step 10 · 180 / 140 | A | L | — | |
| dialog.font / typeface / outline | Text size / Typeface / Text outline | D | as UI · default · off | A | L | Text | the dialog window's element text rows |
| dialog.x / y / w / placed | — | S, T | window state | H | | — | |
| inventory.enabled | Enable the CX inventory window | T | **off** | U | L | — | The inventory in CX's own window: the vanilla layout, sharp at any resolution, tabs or one view scrolling through every page, search. Off, the stock panel runs. |
| inventory.size | Size | S | 0.75–3 × step 0.05 · 1 | U | L | — | Scale over the UI font scale. The window shrinks itself to fit the screen. |
| inventory.mode | Pages | D | Tabs / Scroll · Scroll | U | L | — | Tabs is the vanilla layout (a tab per page); Scroll replaces the tabs with a scrollbar that glides through every page, dividers marking where each begins. |
| inventory.search | Search box | T | on | U | L | — | A box in the title bar that dims every item whose name doesn't match. |
| inventory.kilograms | Kilograms tooltip | T | off | U | L | — | Hovering the weight shows it in kilograms (1 lb = 0.4536 kg). The window itself keeps the game's pounds. |
| inventory.dropground | Drag out to drop | T | on | U | L | — | Releasing a dragged item over the world acts as the stock panel does: on the ground it drops there, on another creature it offers a trade, on you or your associate a potion is drunk. Off: only quickbar drops leave the window. |
| inventory.typeface / outline | — | D | default · off | C | L | Text | the inventory's element text rows; its text size follows its own *Size* row |
| inventory.x / y / placed | — | S, T | window state | H | | — | |

**Units:** "pt" = 1 design px before GUI scale, no DPI maths; the ladder is 6–32 pt everywhere
(`TextRig.MinFontPx` 6 … `MaxFontPx` 32; the atlas ceiling is 96 px effective). Box sizes and
distances stay design px.

**Element text rows** (`TextStyle`): every CX element that draws text declares the same three
rows in its own group, under a section the group names: `<prefix>font` (*Text size*, as UI /
6..32), `<prefix>typeface` (*Typeface*, the `ui.typeface` choices; `default` follows the UI) and
`<prefix>outline` (*Text outline*, off / thin / thick). Overriding the UI-wide rows for one
element is an enthusiast's dial (ADR 0030), so the tier is **Advanced** for an element drawn
over the world (`nameplate` names and `bubble*`, `combattext`, `dialog`, `fps`) and
**ChatOnly** for a CX window content with the UI rows (`radial`, `inventory` -- typeface and
outline only, its text follows its own *Size* row). As UI + default draws with the UI face;
anything else is a face of its own on the shared sheet: a draw-path miss queues its bake on the
thread pool and the UI face stands in until it lands (a cold caller -- the radial menu opening, a
row set -- bakes in place); a refused bake logs once; every re-bake raises `TextRig.Rebaked`, which
the dialog rebuilds on. The outline is a black halo of one px per level per 18 px of
face, never under a px per level, laid under all of a run's glyphs before their bodies. Each
chat window keeps its own (`w<N>.font` / `typeface` / `outline` on its gear; the outline reaches
the messages, not the tab bar or the input line). A toggle's stored `1`/`0` reads as thin/off, so
the old `nameplate.outline`, `combattext.outline` and `w<N>.outline` keep their meaning. The
settings panel (and the chat gear, which is it), its tooltips and dropdowns, and the Alloy notice
lines draw with the UI face itself: `ui.font` / `ui.typeface` are their rows. The quickbar and the
NUI windows draw no CX text.

### 4.3 Graphics

Nothing in Graphics is parented on `alloy.enabled` (ADR 0030 §6): the renderer's master is a
ChatOnly escape hatch, so its off state lays the clickable strip over the group's own rows
rather than greying every submenu.

**Quality** — groups `quality` (300, *Preset*), `alloy` (302, *Display*), `lod` (304, *Model
detail*). `quality`'s header: "Pick a preset, then adjust any area. A changed setting elsewhere
in Graphics keeps a blue dot until you pick a preset again."; its `PanelStatus` is "High, as
set." / "High, 3 settings changed from it." (the ladder members, recursively, that left their
rung). `alloy`'s `PanelStatus` is the renderer's own line ("Running: Alloy / Vanilla —
`AlloyGate.Reason`"); bare `/alloy` prints the diagnostic `Status`.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| quality.preset | Graphics quality | D | Low / Medium / High / Ultra · High | U | L | — | Sets every area below at once. Low is for integrated graphics; Ultra raises shadow and reflection detail past the defaults. Some changes take effect at the next launch. |
| quality.shadows | Shadows | D | Low / Medium / High / Ultra · High (ladders on `quality.preset`) | U | L | Areas | Shadow resolution, distance, softness and how many lights cast them. |
| quality.lighting | Lighting | D | (the preset ladder) | U | L | Areas | How many lights reach each pixel and how far their light carries. |
| quality.reflections | Reflections | D | (the preset ladder) | U | L | Areas | Reflections of the scene on glossy and wet surfaces. |
| quality.ao | Ambient occlusion | D | (the preset ladder) | U | L | Areas | Contact darkening in corners and under objects (the game's own Ambient Occlusion option still turns it on or off). |
| quality.detail | Model detail | D | (the preset ladder) | U | L | Areas | How far models keep full detail, and how small a model may get before it is skipped. |
| quality.grass | Grass | D | (the preset ladder) | U | L | Areas | How dense grass is and how far it draws. |
| quality.weather | Weather | D | (the preset ladder) | U | L | Areas | Rain and snow density, splashes, and the wet and snowed ground. |
| quality.effects | Effects | D | (the preset ladder) | U | L | Areas | Particles, light halos and other glows. |
| alloy.enabled | Enable the Alloy renderer | T | on | **C** | R | — | cfg-only (ADR 0030 §6): the way to run without CX's renderer short of uninstalling, and the key `AlloyGate`'s failure notice names. Off, the game's own renderer draws the world and nothing under Graphics applies. |
| alloy.scale | Render scale | S | 0.25–2.00 step 0.05 · 1.00 | U | L | — | Size of the 3D frame relative to your screen. Below 1 renders fewer pixels and upscales (faster); above 1 supersamples (sharper, slower). The UI is never scaled. |
| alloy.highlight | Selection highlight | D | glow / outline · **outline** | U | L | — | How a selected or hovered object is marked: an outline with a light fill, or a soft glow around the object. |
| alloy.lod | Skip models smaller than | S | 0–8 px step 0.5 · 1.0 | A | L | — | Distant models whose projected size is under this many pixels are not drawn at all. Ladders on `quality.detail`. |
| alloy.cutmip | Distant cut-out fill | S | 0–1 step 0.05 · 0.25 | A | L | — | Leaves, fences and other cut-out textures thin out and shimmer with distance as their alpha averages away. This keeps them as full as they are up close. 0 is the stock test. |
| alloy.ghost | See-through creature thickness | S | 1–3 × step 0.1 · 1.2 | A | L | — | A creature an effect such as Invisibility makes see-through draws as this many layers of itself, so its shape still shows. 1 is the effect's own transparency. A fully invisible creature stays invisible. |
| alloy.texpreview | Low-resolution texture previews | T | on | A | L | — | While an area streams in, every texture first shows a small blurred version of itself, then sharpens when the full one lands. Off: a texture shows white until it has fully loaded. |
| alloy.a2c | Alpha-to-coverage samples | D | off / 2x / 4x / 8x · 4x | A | L | — | Materials that ask for alpha-to-coverage (`NWN_ALPHA_MODE 2`, often hair) get soft, layered edges from this many samples per pixel. Off draws them with the hard cutout edge every other leaf has. |
| alloy.gui / weld / border / oit / oitlayers / texcache / bindless / bcn / hdr / gl46 / drawhooks / ownedtex / pltcpu / pltverify / bcndrip / texprefetch / texsingle / ddsread / rasterread / texdelete / handledrop / ownedgeom / maxelements / spirv / vanillabridge / hostfs / ownskin / clustercap / dumptex / dumpshaders / gldebug / renderthread / swapprobe / framediff / paritydump / staleaudit / glsync / skipbuckets / enginezones | — | T, S, D, X | as declared | C | mostly R | — | capability gates, budgets and bisects |
| alloy.movie / a2cmask / light / prepass / viz / threads / aotest / cull / retest / hldiag / hlchan / poll / prepasskey / prepassfeat / depthequal / idtarget / blenddepth / gpumeter / vislist / washtest / oitviz / cullwatch / lodhyst / hitcheck | — | T, S | as declared | C | L | — | live path switches and visualisers |
| alloy.authoredlod | authoredlod | T | on | H | L | — | cfg-only: authored `.lod` alternates are loaded and switched for every object, non-static placeables included, whatever the game's own LOD option says. Off is for debugging; the switch distances still follow the game's LOD scale setting. |
| alloy.consts / framedump / bench / shaderdump / snap / hitbench | — | B | — | C | — | — | dumps and measurements (`bench` steps the GPU-cost settings and logs a `[bench]` table) |
| lod.enabled | Automatic mesh detail | T | on | **C** | L | — | cfg-only: distant walls and floors are drawn from a simplified copy of their own mesh. Off draws every model at full detail at every distance, which is what the game does. |
| lod.px1 | Full detail above | S | 50–2000 px step 10 · 500 | A | L | — | A mesh wider than this on screen is drawn exactly as authored. Ladders on `quality.detail`. |
| lod.px2 | Lowest detail below | S | 10–1000 px step 10 · 150 | A | L | — | Below this a mesh is drawn from its smallest copy. Between the two it takes the middle one. Ladders on `quality.detail`. |
| lod.bias | — | S | 0.25–4 · 1 | C | L | — | scales both thresholds |

**Lighting** — groups `light` (310, *Lights*), `shadow` (312, *Shadows*), `ao` (314, *Ambient
occlusion*), `lightmap` (316, *Baked lighting*), `effects` (318, *Effects*). `light`,
`ao` and `effects` have no master; `ao`'s header: "The game's own Ambient Occlusion video
option turns this on and off." `lighttrace` (320, *Lighting trace*) is a ChatOnly master alone,
so it adds no submenu content.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| light.gain | Point-light brightness | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales every placed and carried light. 1 is the stock brightness. |
| light.flicker | Fire flicker | S | 0–2 step 0.05 · 1.00 | U | L | Lights | Animated lights (torches, braziers) keep a steady reach and flicker in brightness instead of pulsing in size. 1 flickers as deep as the pulse was made; 0 is the stock pulse. A light flickers when its model's animation keys its radius or multiplier as a pulse (3+ keys, rising and falling, never 0); day/night ramps and blinks are left alone. |
| light.flickerspeed | Flicker speed | S | 0.1–4 step 0.05 · 1.00 | A | L | Lights | Rate of the flicker; 1 is a fireplace. |
| light.ambient | Ambient light | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales the area's ambient fill. 1 is the area author's value. |
| light.sun | Sun and moon | S | 0–4 step 0.05 · 1.00 | U | L | Lights | Scales the area's directional light. 1 is the area author's value. |
| light.sky | Shade under cover | S | 0–1 step 0.05 · 0.25 | U | L | Lights | Outdoors, how much the ambient fill drops where little open sky reaches -- an alley, an arcade, the space under a roof. A vanilla area has one ambient for the whole of it, so 0 is that flat fill back and needs no content change either way. Interiors are unaffected, and this needs Shadows switched on: it reads the same view of the area the shadows are cast from. Water and effects keep the flat fill. |
| light.probe | Reflections from the scene | T | on | U | L | Reflections | Materials with an environment map reflect a capture of the surroundings instead of the stock sphere map. Ladders on `quality.reflections`. |
| light.ssr | Screen-space reflections | T | on | U | L | Reflections | Glossy and rain-wet surfaces also reflect what is on screen -- grass, creatures, anything drawn -- and keep the capture where a reflection leaves the screen. Ladders on `quality.reflections`. |
| light.ssrplanar | Mirror reflections on flat ground | T | off | U | L | Reflections | Wet ground at the player's height reflects a second, mirrored draw of the scene: no holes behind figures, and undersides the camera never sees. Costs a low-resolution redraw of the opaque set, drawn without moving lights, so a street lit by them mirrors darker than it is. |
| light.overbright | Carried-light boost | S | 0–1 step 0.05 · 0 | A | L | Lights | How much of a torch's extra intensity shows at its centre instead of only as reach. 0 is the stock game; 0.5 makes carried lights stand out. |
| light.daylight | Daylight outshines lamps | S | 1–10 step 0.25 · 3 | A | L | Lights | How much brighter full daylight is than a lamp. Lamps and their glow fade as the day brightens and come back at dusk. 1 is the stock game. |
| light.sss | Soft skin lighting | S | 0–1 step 0.05 · 0.25 | A | L | Lights | Light wrapping past the shadow line on characters. 0 is flat; the stock game uses 0.38 at shader quality above medium. |
| light.probesize | Reflection capture size | D | 64 / 128 / 256 / 512 px · 64 | A | R | Reflections | Resolution the surroundings are captured at. Ladders on `quality.reflections`. |
| light.ssrdetail | Screen-space reflection detail | D | full / half / quarter · half | A | L | Reflections | The resolution a reflection finds what it hits at. Lower is cheaper and blockier. Ladders on `quality.reflections`. |
| light.ambientdir | Directional ambient | S | 0–1 step 0.05 · 0.5 | A | L | Materials | Ambient light takes its direction from the surroundings (sky above, ground below) at the area's own brightness. 0 is the stock sun-facing shading. |
| light.daylow / dayhigh / wrap / wrapndl / hot / hotsize / radius / falloff / fadeout / deferred / deferredtiles / lightrec / probegain / ssrrough / ssrplanarres / ssrplanarcull / hidden / brdf / specaa / binreach / bincap / relevance / floor / fullmodel / reachfade | — | T, S, D | as declared | C | L | — | the lit stage's tuning: the day curve, the sphere-light shape, the deferred path, reflection cost, the modern BRDF (ADR 0026) and the cluster/relevance budgets |
| shadow.enabled | Enable Alloy shadows | T | on | **C** | R | — | cfg-only: real-time sun cascades and point-light shadow cubes. Off, no surface takes a shadow from CX at all. |
| shadow.dist | Sun shadow distance | S | 20–400 m step 10 · 120 | A | L | Sun | How far from you the sun casts shadows. Cost does not depend on area size. Ladders on `quality.shadows`. |
| shadow.size | Sun shadow resolution | D | 512 / 1024 / 2048 / 4096 · 2048 | A | R | Sun | Per-cascade map size. Larger is sharper and slower. Ladders on `quality.shadows`. |
| shadow.sunangle | Sun angular size | S | 0–5 deg step 0.05 · 0.53 | A | L | Sun | How wide the sun looks in the sky, which is what sets how fast its shadows soften away from whatever casts them: 0.53 is the real sun, larger is softer everywhere. 0 turns the blocker search off and every sun shadow takes the fixed, cheapest edge. Ladders on `quality.shadows`. |
| shadow.cubes | Shadow-casting lights | S | 0–64 step 4 · 32 | A | R | Point lights | How many nearby lights cast shadows at once. Lower on a small card. Ladders on `quality.shadows`. |
| shadow.cubesize | Point-light shadow resolution | D | 128 / 256 / 512 / 1024 / 2048 · 512 | A | R | Point lights | Per-face size of each light's shadow cube. Ladders on `quality.shadows`. |
| shadow.cubedist | Point-light shadow distance | S | 0–200 m step 5 · 30 | A | L | Point lights | A light farther than this casts no shadow, and no fragment farther than this reads one; shadows fade over the last quarter. 0 = every light in the budget. Ladders on `quality.shadows`. |
| shadow.soft | Shadow softness | S | 0–16 step 0.25 · 4 | A | L | Point lights | Widest blur of point-light and sun shadows, in shadow-map texels; the blur narrows toward the caster's contact. 0 is hard-edged and cheapest. Ladders on `quality.shadows`. |
| shadow.search | Blocker search taps | S | 1–16 step 1 · 16 | C | L | Point lights | How many taps look for what is casting the shadow, which is what lets the blur narrow toward the caster. Fewer is cheaper and guesses the width off less of the map. Ladders on `quality.shadows`. |
| shadow.cascades / floor / dynsize / dyncount / dynmin / sundyn / discpx / taps / cap / perpixel / deferredres / near / csmbias / fade / fadeslots / mask / maskholes / maskgraded / maskdither / focusdist / staticcap / dynhold / dynfull / sunfull / sunlatch / dynranks / casters / stage / gather / receiver / rebake / split / layered / refreshall / dynsrc / dynevents / itemdirty / flipcollect / maskh / trace / detail | — | T, S, D | as declared | C | casters / stage / dynsize / dyncount / sundyn R, rest L | — | cascade count, the per-pixel and mover budgets, the cut-out masks, the bake cadence, the bisects |
| shadow.movers | — | B | — | C | — | — | dump the mover list next frame |
| ao.radius | Radius | S | 0.1–2 m step 0.05 · 0.6 | U | L | — | How far occlusion reaches: 0.6 darkens corners and feet; larger is a room-wide gloom. Ladders on `quality.ao`. |
| ao.strength | Strength | S | 0.5–4 step 0.1 · 2.2 | U | L | — | Multiplied by the game's AO intensity slider. |
| ao.dirs | Slices | S | 1–8 · 1 | A | L | — | Directions each pixel searches for nearby surfaces. More is smoother and slower. Ladders on `quality.ao`. |
| ao.steps | Steps | S | 1–8 · 3 | A | L | — | Samples along each direction. More reaches further out accurately, and costs that much more. Ladders on `quality.ao`. |
| ao.direct / cutout / temporal / maxpx / blur / on | — | T, S | as declared | C | L | — | the direct term, cutout coverage, temporal accumulation, the screen-radius cap (ladders on `quality.ao`), the blur and the bypass |
| lightmap.enabled | Baked static lighting | T | on | **C** | L | — | cfg-only: static geometry gets its lights baked into a lightmap as the area loads, and the frame stops re-shading it. Off is the live light loop. |
| lightmap.density | Lightmap density | S | 2–32 px/m · 8 | A | L | — | Lightmap texels per metre of world. Each instance's rectangle is capped at a quarter page; a change re-bakes the area. |
| lightmap.movers | Creature shadows from baked lights | T | on | A | L | — | A creature standing under a baked light casts its shadow on the baked surface. Off is cheaper. |
| lightmap.vram / window / near / tiers / hold / movertaps / moverlights / pairsize / paircount / disk | — | T, S | as declared | C | pairsize / paircount R, rest L | — | the page pool's memory budget, the baked radius round the player, the density tiers, the loading hold, the creature-and-light shadow atlas, the chart cache size |
| effects.halos | Light halos | S | 0–4 step 0.1 · 1 | U | L | — | Brightness of the glow around the lights that flare in vanilla. 1 is vanilla's; 0 draws no halos. Ladders on `quality.effects`. |
| effects.sky / beams / halosize / haloviz | — | T, S | as declared | C | L | — | the sky dome, the light shafts, the halo's size as a multiple of vanilla's flare, the halo visualiser |
| lighttrace.enabled | Trace every area-light change to cx.log | T | off | **C** | L | — | cfg-only diagnostic for area builders: every light, direction and fog packet the server sends, every day/night state change and transition, the lightning flash, and once a second the colours the engine applied to the area's sun next to the ones Alloy rendered -- all under the `[lighttrace]` prefix. Off costs nothing per frame. |

**Image** — group `post` (330), no master (the resolve is the frame).

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| post.exposure | Exposure | S | 0.25–4 step 0.05 · 1 | U | L | Exposure | Overall brightness before tone mapping. 1 is neutral. |
| post.bloom | Bloom | S | 0–0.5 step 0.01 · 0.06 | U | L | Bloom | Glow around bright lights. 0 is off. |
| post.tonemap | Tone mapping | D | shoulder / aces / neutral · aces | U | L | Tone | How bright values above white are compressed. |
| post.taa | Temporal anti-aliasing | T | on | U | L | Anti-aliasing | Smooths edges over several frames. |
| post.threshold | Bloom threshold | S | 0–4 step 0.05 · 1 | A | L | Bloom | |
| post.knee | Bloom knee | S | 0–1 step 0.05 · 0.5 | A | L | Bloom | |
| post.taasharp | TAA sharpness | S | 0–1 step 0.05 · 0.3 | A | L | Anti-aliasing | Undoes the softness the history blend leaves. 0 = the raw resolve. |
| post.acesmix / tmknee / dither / taablend / taau / halfres / stack / fbpost / toonbands / toonink / toonedge / motion / viz / clearviz / skyraw | — | T, S | as declared | C | L | — | tone-curve details, the TAA history weight and upsample, half-resolution AO, the stock framebuffer effects (gamma, sharpen, vibrance, SMAA, toon) and the visualisers |

**World** — groups `grass` (340), `weather` (342), `fog` (344), `vfog` (345, *Volumetric fog*). `water` (346) declares a
ChatOnly master and two diagnostics, so it has no submenu.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| grass.enabled | Grass | T | on | U | L | — | Grass on the ground: denser than the stock game's, wind-swayed, lit and shadowed. Off, no grass is drawn at all. The game's own Grass option still has to be on either way. |
| grass.style | Grass style | D | Procedural blades / Game textures · Procedural blades | U | L | Field | Procedural blades: dense, individually shaded 3D blades coloured from the area's grass. Game textures: the area's own grass pictures, as the stock game draws them. Grass textures that look like flowers stay as pictures either way. |
| grass.density | Density | S | 0–4 step 0.25 · 2 | U | L | Field | Blades per patch relative to the stock game. Ladders on `quality.grass`. |
| grass.far | Distance | S | 0–1000 m step 10 · 0 | U | L | Field | How far grass draws. 0 follows the game's Grass Distance option (which stops at 173). |
| grass.push | Parts around characters | S | 0–1 step 0.05 · 0.15 | U | L | Field | Blades bend away from creatures walking through them. 0 is off. |
| grass.wind | Wind strength | S | 0–4 step 0.1 · 1 | U | L | Wind | How far the blades sway. 0 stands them still. |
| grass.bladedensity | Blade density | S | 1–32 · 8 | A | L | Detail | Procedural blades per stock grass tuft, on top of Density. Ladders on `quality.grass`. |
| grass.lod | Full-density radius | S | 5–200 m · 40 | A | L | Detail | Every blade draws inside this. Ladders on `quality.grass`. |
| grass.bladelod | Full-detail blade radius | S | 5–200 m · 30 | A | L | Detail | Procedural blades inside this are smooth curves; past it each is a single triangle. Ladders on `quality.grass`. |
| grass.shade | Full-shading radius | S | 5–200 m · 40 | A | L | Detail | Past this blades skip light and shadow taps. Ladders on `quality.grass`. |
| grass.shadow | Grass casts shadows | D | none / 1 / 2 / 3 / 4 · 1 | A | L | Detail | How many sun cascades the blades are drawn into. More is sharper far away and costlier. Ladders on `quality.grass`. |
| grass.aniso | Anisotropic filtering | D | 1 / 2 / 4 / 8 / 16 · 8 | A | L | Detail | Keeps grass textures sharp when seen at a low angle. Lower is a little faster and blurrier. |
| grass.snowthin / snowfall / snowlook / windspeed / lodmin / lodwidth / lodheight / castwidth / pushradius / pushextent / cull / cullshrink / cullmax / tuft / soften / edge / softedge / rootao / gtint / width / clump / backlight / sheen / vlight / prepass / gviz | — | T, S | as declared | C | L | — | the winter look, the far-field ramp, the push field, the object cull, the blade shape and shading (`vlight` ladders on `quality.grass`) |
| grass.dumpmips / dumpshadow | — | B | — | C | — | — | |
| weather.enabled | Weather | T | on | U | L | — | Rain and snow drawn by CX: wind-leaning streaks and flakes that stop under cover, and ground that wets, puddles and snows over. Off, the game's own rain and snow sheets fall instead. |
| weather.density | Density | S | 0–3 step 0.1 · 1 | U | L | Precipitation | How much rain or snow falls, on top of the weather the area asks for. Ladders on `quality.weather`. |
| weather.wet | Wet surfaces | S | 0–1 step 0.05 · 1 | U | L | Surfaces | Exposed ground and roofs darken and turn glossy in rain, reflecting the sky, and puddles pool in low ground. 0 = off. |
| weather.snow | Snow cover | S | 0–1 step 0.05 · 1 | U | L | Surfaces | Exposed up-facing surfaces whiten in snow, with drifts banked against walls. 0 = off. |
| weather.drips | Roof drips | S | 0–1 step 0.05 · 0.25 | A | L | Precipitation | Drops fall from roof edges and overhangs while the roofs are wet, and keep falling for the dry-out time after the rain stops. 0 = off. |
| weather.cover | Shelter under roofs | T | on | A | L | Cover | Nothing falls under a roof, an awning or a doorway, and what they shelter stays dry. Off, it rains indoors. |
| weather.accum | Soak / settle time | S | 5–600 s step 5 · 60 | A | L | Surfaces | Seconds of full-density weather to fully wet or snow the ground. |
| weather.dry | Dry-out time | S | 10–1800 s step 10 · 180 | A | L | Surfaces | Seconds for wet ground to dry after the rain stops. |
| weather.melt | Melt time | S | 30–7200 s step 30 · 900 | A | L | Surfaces | Seconds for snow cover to melt after the snowfall ends. |
| weather.puddles | Puddles | S | 0–1 step 0.05 · 0.5 | A | L | Surfaces | How much of level ground pools in the rain: low is a few small pools in the dips, high is broad sheets. A server may set it for its own areas. |
| weather.impacts | Splash impacts | S | 0–1 step 0.05 · 0.6 | A | L | Impacts | Water leaps where each drop lands -- on the ground, on roofs, on ledges, wherever the rain reaches. 0 = off. Ladders on `quality.weather`. |
| weather.streak / flake / bright / opacity / range / rainspeed / snowspeed / windlean / snowangle / backscatter / soft / count / viz / waterroof / coversoft / coverrange / shelcache / arrive / splash / edgebreak / edgetaps / runoff / shorerim / snowalbedo / snowslope / snowpile / snowpilewidth / snowvary / snowdepth / snowsteps / snowbump / snowsparkle / snowwrap / snowsky / puddle / puddleedge / impactrange / map / mapres / mapcadence / basin / puddledepth / filmrough / filmlights / wetdark / rainrows / snowrows / snowskip | — | T, S, X | as declared | C | L | — | the fall's look and speed, the cover map, the shelter edge, the snow relief, the wetness map and its puddles, and the `weathertypes.2da` row lists (`range`, `count`, `splash`, `edgetaps`, `snowsteps`, `mapres`, `mapcadence`, `filmlights` ladder on `quality.weather`) |
| fog.enabled | Fog | T | on | U | L | — | Fog that takes the sky's colour, glows toward the sun and thins with height. Off, every dial below reads 0 and the area author's own flat fog colour draws, as the stock game shows it. |
| fog.sky | Fog takes the sky colour | S | 0–1 step 0.05 · 0.75 | U | L | Colour | How far the fog colour moves from the area's flat colour toward the sky. 0 is the stock look. |
| fog.sun | Sun glow in fog | S | 0–2 step 0.05 · 0.35 | U | L | Colour | Fog brightens toward the sun. |
| fog.sunexp | Sun glow tightness | S | 1–64 · 8 | A | L | Colour | Big = tight glow, small = half-sky wash. |
| fog.skyband | Horizon band | S | 0–1 step 0.05 · 0.15 | A | L | Colour | How far above the horizon the sky dome is fogged. |
| fog.height | Height falloff | S | 0–1 step 0.005 · 0 | A | L | Density | Fog thins with height above the floor; 0 = off. |
| fog.floor | Layer floor | S | 0–50 m step 1 · 0 | A | L | Density | Offset above your feet where thinning starts. |
| fog.origin / cull / cullz | — | T, S | as declared | C | L | — | player-origin fog, the fully-fogged geometry cull and its depth |
| vfog.enabled | Volumetric fog | T | off | U | L | — | Fog as a lit volume: sunlight falls through it in shafts, lamps glow in it and it drifts on the wind, built from the area's own fog colour and distance. Replaces the fog above; costs GPU time. |
| vfog.quality | Fog detail | D | Low / Medium / High / Ultra · Medium | A | L | — | How finely the fog volume is divided. Higher shows sharper light shafts and costs more. Ladders on `quality.effects`, a step below it. |
| vfog.haze | Haze in the air | S | 0–0.9 step 0.01 · 0.1 | A | L | Look | How much of the area's fog hangs in the air close by. More shows stronger shafts and glows; 0 keeps the near air clear. |
| vfog.sun | Sun shafts | S | 0–1 step 0.05 · 0.5 | A | L | Look | How much of the fog's light falls as sunbeams: more is brighter shafts and darker shade between them. 0 is even fog. |
| vfog.lights | Lamp glow | S | 0–4 step 0.05 · 1.5 | A | L | Look | How brightly torches and lamps glow in the fog. |
| vfog.sky | Fog takes the sky colour | S | 0–1 step 0.05 · 0.5 | A | L | Look | How far the fog's own light moves from the area's fog colour toward the sky's. 0 is the area's colour. |
| vfog.aniso | Forward scattering | S | 0–0.9 step 0.05 · 0.4 | A | L | Shape | How much brighter the fog looks toward a light than away from it. |
| vfog.height | Haze height falloff | S | 0–0.2 /m step 0.005 · 0.08 | A | L | Shape | How fast the haze thins above the ground you stand on; 0 = the same at every height. |
| vfog.floor | Haze floor | S | −20–50 m step 1 · 0 | A | L | Shape | Height above your feet where the haze starts to thin. |
| vfog.noise | Patchiness | S | 0–1 step 0.05 · 0.6 | A | L | Shape | How uneven the haze is; 0 is even haze. |
| vfog.noisesize | Patch size | S | 2–64 m step 1 · 16 | A | L | Shape | Size of the haze's patches. |
| vfog.wind | Drift speed | S | 0–8 m/s step 0.1 · 1.5 | A | L | Shape | How fast the patches drift, along the area's wind. |
| vfog.band | Horizon band | S | 0.01–1 step 0.01 · 0.15 | A | L | Shape | How far above the horizon the far fog covers the sky. |
| vfog.temporal / lightshadows | — | S, T | as declared | C | L | — | the share of past frames the volume keeps, and lamps' shadows in the fog |
| water.enabled | Water | T | on | **C** | L | — | cfg-only: draw water (the stock water shader, reflecting the finished frame). Off, no water is drawn at all -- there is no stock water pass left behind it. |
| water.rect / diag | — | T, S | as declared | C | L | — | |

**Motion** — groups `anim` (350, *Animation*), `dangly` (352, *Cloaks and hair*), `particles`
(354, *Particles*). `emitsim` (356, *Particle simulation*, parent `particles.enabled`) is a
ChatOnly master alone, so it has no submenu.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| anim.enabled | Procedural animations | T | on | U | L | — | Corrections on top of the game's own animation: feet that stand on the floor that is drawn, strides that match the pace, leaning, body weight and hit reactions. Off, every correction below reads off and characters animate exactly as the stock game plays them. |
| anim.distance / footik / footik_max / footik_tilt / footik_lift / stride / footlock / settle / footsteps / syncattach / lookbody / diagonal / pivot / blend / crossfade / curves / lean / mass / drive / follow / aim / hitreact / staticlight | — | T, S | each defaults on / at its tuned value | C | L | Feet, Body | the individual corrections; one reads on only while both it and the master are |
| dangly.enabled | Swaying cloaks and hair | T | on | **C** | L | — | cfg-only: cloaks, hair, banners and other swaying parts move with wind and their own weight. Off, they go rigid -- nothing else sways them. |
| dangly.motion | Motion | D | automatic / stock · automatic | U | L | — | Automatic moves each part the way its own model asks to be moved -- leaves and cloth sway and ring, cloaks swing, hair jiggles. Stock moves every part the way the unmodified game does. |
| dangly.windgain | Wind response | S | 0–3 step 0.05 · 1 | U | L | Forces | How hard moving air pushes a part -- the wind, and the air a moving character runs through. 0 = none. |
| dangly.tilt | Gravity | S | 0–1 step 0.05 · 1 | U | L | Forces | How fully a part on a tipped-over object falls toward the ground. 0 = it keeps hanging the way its model authored it. |
| dangly.breeze | Base breeze | S | 0–2 step 0.05 · 0.5 | U | L | Forces | The least wind an outdoor area blows, so a still one is not frozen: 1 = an area set to weak wind, 2 = strong. 0 = only what the area sets. Automatic motion only. |
| dangly.cage | Freedom of movement | S | 0.5–4 step 0.1 · 1.5 | A | L | Movement | How far hair, body parts and short plants may move from where the model puts them: ×the authored limit. Cloth and tall plants swing freely. Automatic motion only. |
| dangly.sway / swing / jiggle / still | — | T, S | each on / as declared | C | L | — | one kind stood down to stock motion, and the displacement a part has to cross to count as a shadow mover |
| particles.enabled | Particles | T | on | **C** | L | — | cfg-only: draw spell effects, smoke and fire. Off, no particles are drawn at all -- nothing else draws them any more. |
| particles.soft | Soft edges | T | on | U | L | — | Particles fade where they meet geometry instead of cutting through it. Ladders on `quality.effects`. |
| particles.lit | Lit particles | T | on | U | L | — | Smoke and dust take the scene's light. Ladders on `quality.effects`. |
| particles.shadows / sort / grid / guard / batch / sampler / diag / viz | — | T, S | as declared | C | batch / sampler R, rest L | — | shadowed particles (ladders on `quality.effects`), the GPU sort, the batch and sampler paths, the visualiser |
| emitsim.enabled | Simulate particles in CX | T | on | **C** | L | — | cfg-only: CX moves, spawns and retires every particle. Off, nothing steps them -- they freeze where they are. |

**Not panel content.** `view` (CX computes the view and projection; CX runs the wind), `models`
(the model cache's budget, the parse-ahead and the animation-set check), `mdl` (the parse
comparison) and `dbtiles` (the CX tile build's report) declare ChatOnly knobs and a diagnostic
`Status` only: `/view`, `/models`, `/mdl`, `/dbtiles` and `/knobs` are their whole surface.

### 4.4 Audio

**Sound** — group `sound` (400). Master `sound.enabled`, live.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| sound.enabled | Enable CX sound (Steam Audio) | T | on | U | L | — | Positional sounds are rendered by Steam Audio: heard from their real direction, muffled by walls, echoing off the area's own geometry. Off, every sound plays as in the stock game. |
| sound.output | Output | D | Automatic / Headphones (HRTF) / Speakers · Automatic | U | **R** | — | Headphones place every sound around your head (HRTF). Speakers pan across your speaker layout, stereo to 7.1. Automatic asks the system whether headphones are connected. |
| sound.layout | Speaker layout | D | Automatic / Stereo / Quad / 5.1 / 7.1 · Automatic | U | **R** | — | The speakers to pan across when Output is Speakers. Automatic takes what the system reports. Play the speaker test after changing it. |
| sound.test | Play speaker test | B | — | U | — | — | A burst of noise from ahead, then right, behind and left of your character's view, one second each. If one comes from the wrong place, your output or speaker layout is set wrong. |
| sound.occlusion | Walls | D | No effect / Block sound / Muffle sound · Muffle | U | L | Space | What a wall between you and a sound does. Muffle lets some of it through, dull, as a real wall does. |
| sound.reverb | Reverb | D | Off / Room / Every sound / Area preset · Room | U | L | Space | Echo from the area's walls, floors and ceilings. Room simulates it once around you; Every sound also gives the four nearest sounds their own echoes, which costs noticeably more CPU. Area preset is the area's authored reverb type instead. |
| sound.ambient / footsteps / combat / spells / voices / other | Ambience / Footsteps / Combat / Spells / Creature voices / Everything else | S | −24…+12 dB step 1 · 0 | U | L | Mix | per-category loudness of positional sound |
| sound.master | Master volume | S | 0–1.5 step 0.05 · 1 | U | L | Mix | Everything the game plays, music and interface included. The stock game has no master volume. |
| sound.reverblevel | Reverb level | S | −24…+6 dB step 1 · 0 | A | L | Space | How loud the echoes are against the sound itself. |
| sound.pathing | Sound around corners | T | off | A | L | Space | A sound behind a wall still reaches you through the nearest opening, from that direction. Each area is analysed in the background when you enter it. |
| sound.copies | Same sound, at most | S | 0–8 · 2 | A | L | Repetition | How many copies of one sound play at once, nearest first. 0 plays every copy. |
| sound.normalize | Even out loudness | S | 0–1 step 0.05 · 0.5 | A | L | Mix | Moves each sound part of the way to a sensible level for its kind. 0 leaves every sound as recorded. |
| sound.crossfade | Crossfade music and ambience | T | on | A | L | Mix | A new music track or area ambience fades in while the old one fades out. The stock game fades the old one out completely first. |
| sound.rainreverb / curve / air / placeloops / level3d / pitchvariance / randomstart / detune / drift / ambienttreble / duck / compressor / limiter / resampler / rays / bounces / duration / order / rolloff / fade / voices3d / voices2d | — | T, S, D | as declared | C | order / voices3d / voices2d R, rest L | — | the distance model, the repetition fixes, the mix shaping and the simulation's budgets |

### 4.5 Controls

**Chase / Top-down / Driving** — groups `chase` (500) / `topdown` (501) / `driving` (502), one
submenu each, identical rows with per-mode values and each its own master. Header: "The game
has three camera modes, picked under its own Options » Controls » Camera Mode (Chase Cam,
Driving, Top Down; Top Down is the game default). The camera-mode key (`*` by default) flips
between Driving and the other one you picked. Each mode keeps its own settings here."
`PanelStatus` names the live mode.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| `<mode>.enabled` | Enable the CX camera for this mode | T | on | U | L | — | CX shapes this camera mode: smoothing, field of view, offset, camera-relative movement, auto-recenter. Off, the stock camera runs untouched in this mode. |
| `<mode>.fov` | Field of view | S | 10–120 ° step 1 · 45 | U | L | View | Vertical field of view. The game's own is 45. |
| `<mode>.lagpos` | Smoothing | S | 0–2 s step 0.05 · 0 | U | L | Feel | How long the camera takes to catch up with your character and to settle after a turn. 0 is welded, as in the stock game. A set carries `lagrot` and `lagzoom` (floor 0.02) along. |
| `<mode>.offsetx` | Sideways offset | S | −10–10 m step 0.25 · 0 | U | L | Offset | Moves your character off centre; positive puts them on the left. 0 is the stock orbit. |
| `<mode>.offsety` | Height offset | S | −10–10 m step 0.25 · 0 | U | L | Offset | Raises the camera's focus above your character. |
| `<mode>.drive` | Move relative to the camera | T | chase on · topdown off · driving on | U | L | Movement | Forward is where the camera looks, not where your character faces. |
| `<mode>.face` | Face where the camera looks | T | chase on · topdown off · driving off | U | L | Movement | Your character always faces the camera's direction. Forward runs that way, back walks backwards, the strafe keys sidestep, and the turn keys turn you and the camera together. Off: your character turns to face whichever way it moves. |
| `<mode>.recenter` | Swing behind you as you move | T | chase on · topdown off · driving on | U | L | Movement | The camera turns to follow your movement direction instead of the stock chase. |
| `<mode>.mouselook` | Enable CX mouse-look | T | on | U | L | Mouse-look | CX handles the mouse-drag camera: the cursor stays put and the view never stalls at the screen edge. Off, the stock middle-drag orbit runs. |
| `<mode>.right` | Right-drag looks around | T | off | U | L | Mouse-look | Hold the right button and drag to orbit; a still right-click still opens the radial menu. |
| `<mode>.sens` | Sensitivity | S | 0.05–20 × step 0.05 · 1 | U | L | Mouse-look | 1 matches the stock middle-drag. |
| `<mode>.invert` | Invert vertical | T | off | U | L | Mouse-look | Dragging up tilts the view down. |
| `<mode>.action` | Action camera | T | off | U | L | Action camera | Hides the cursor and pins it to a crosshair in the centre of the screen. Moving the mouse turns the camera; whatever the crosshair points at is what you hover, tooltip and click. Left-click acts on it, a right-click opens its menu. Alt frees the cursor; the toggle key switches the camera on and off. |
| `<mode>.actionkey` | Action camera toggle key | X | `]` | U | L | Action camera | An SDL key name (F8, Tab, Backspace, Z...) or Mouse4 / Mouse5 for the thumb buttons. Blank binds nothing. |
| `<mode>.pitchcurve` | Tilt down when zoomed out | S | −10–10 °/m step 0.5 · 0 | A | L | View | Degrees of extra tilt per metre of zoom past the reference distance. Negative lowers the angle instead. 0 leaves the tilt where you put it. |
| `<mode>.siderun` | Turn keys run sideways | T | off | A | L | Movement | With *Face where the camera looks* on: the turn keys run you sideways instead of turning you and the camera. Locked on a target this is always so. |
| `<mode>.rate` | Recenter speed | S | 1–720 °/s step 5 · 360 | A | L | Movement | Fastest swing behind your movement; it eases in near the target. |
| `<mode>.assist` | Aim assist | T | on | A | L | Action camera | The crosshair snaps its aim to the nearest creature, placeable or door within a few pixels of the centre. The camera never moves; only what you point at changes. |
| `<mode>.autotarget` | Cast at the crosshair | T | on | A | L | Action camera | A spell, feat or item that asks you to pick a target fires straight away on whatever the crosshair is pointing at. A ground-targeted spell still asks you to aim it. |
| `<mode>.headtrack` | Head follows the crosshair | T | on | A | L | Action camera | Your character turns their head toward whatever you are aiming at. Only you see it. |
| `<mode>.pitchref / lagrot / lagzoom / deadzone / idle / grace / idlerate / keyturn / hz / turn / threshold / alttoggle / assistpx / feedback / headyaw / headpitch` | — | T, S | as declared | C | L | — | the two lag keys *Smoothing* carries, the recenter and heading-send tuning, and the action camera's fine dials |

Build notes: the master is a ninth `Gate` value consulted at the pre-Control recenter and the
post-Control Adjust/SettleCollision; FOV `Camera+0x4c4` has no engine restorer, so off restores
`_fovFirst`, then `_welded = true` + `CameraCut()`. The layer writes the slider whenever the
mode's master is on. Mouse-look is a per-mode toggle, not a group of its own.

**Camera** — groups `walls` (510, *Walls*), `free` (512, *Free camera*). Neither has a master;
`free`'s header: "No master: the free camera is entered from the button and left with Escape."

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| walls.fade | Fade objects that hide you | T | on | U | L | Fade | Placeables between the camera and your character turn see-through. |
| walls.floor | Faded opacity | S | 0–1 step 0.05 · 0.5 | U | L | Fade | How solid a faded object stays. 0 hides it entirely. |
| walls.radius / margin / armin / armout / armradius | — | S | as declared | C | L | Fade, Spring arm | the ray fan's half-width and the spring arm (which only acts while a sideways or height offset is set) |
| free.enter | Enter the free camera | B | — | U | — | — | Fly the camera freely: move keys fly, Space/C up and down, Z/X roll, Shift is fast, the wheel sets speed, right-drag looks. Escape returns. Closes this panel. |
| free.speed | Fly speed | S | 0.1–200 m/s step 0.5 · 8 | U | L | Feel | How fast the move keys fly the camera; Shift is four times this. |
| free.leash | Range from your character | S | 1–25 m step 1 · 25 | U | L | Feel | The camera stops this far from your character. A server that sets its own camera limit overrides it. |
| free.fov | Field of view | S | 0–120 ° step 1 · 0 | A | L | Feel | 0 keeps the field of view of the mode you came from. |
| free.sens | Look sensitivity | S | 0.05–20 × step 0.05 · 1 | A | L | Feel | On top of Mouse-look's sensitivity. |
| free.turn | Keyboard turn speed | S | 10–720 °/s step 10 · 90 | A | L | Feel | |
| free.roll | Roll on entry | S | −180–180 ° step 5 · 0 | A | L | Feel | |

`_occRate` (8) stays a const; `Leave()` restores FOV when `free.fov` was set.

**Targeting** — group `tabtarget` (520). Master = the whole mode; off = stock Tab and stock
clicks, the lock and any pending preview cast dropped on the off edge. With it on, its cast
mode supersedes the mode's `autotarget`. Header: "Clicking a creature only picks it as your
target; the walk, attack or talk a click used to start is not sent. …"

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| tabtarget.enabled | Enable tab-target mode | T | **off** | U | L | — | MMO-style targeting: pick one target with Tab or a left-click, then quickbar spells, feats, items and the attack button fire at it with no second click. Off, Tab and clicks are the stock ones. |
| tabtarget.cast | Cast mode | D | Normal / Quick / Quick with preview · Quick | U | L | — | Normal: press the button, then click a target, as the game always did. Quick: the button fires at once -- at your target, or with none at what the cursor points at; a ground spell lands at the cursor. Quick with preview: hold the button to see the game's own targeting preview, release to fire; right-click or Escape before release cancels. |
| tabtarget.cyclekey | Cycle key | X | Tab | U | L | — | Locks the candidate nearest the centre of the screen, then steps through the others. Shift with it steps back. An SDL key name. |
| tabtarget.clearkey | Clear key | X | Backspace | U | L | — | Drops the lock. Blank binds nothing. |
| tabtarget.camera | After picking a target | D | Nothing / Turn the camera to it / Lock the camera on it · Turn | U | L | — | Turn: the camera swings once to look at the new target. Lock on: it keeps looking at the target as you both move, until you press the lock key; the turn and strafe keys then run you sideways around the target. |
| tabtarget.lockkey | Lock-on key | X | F | U | L | — | Locks the camera on the creature nearest the middle of the screen (it becomes your target); pressed while locked on, lets go. |
| tabtarget.filter | Tab cycles | D | Hostile creatures / All creatures · Hostile creatures | U | L | — | Hostile only keeps allies and townsfolk out of a fight; all creatures lets you lock an ally for a heal. |
| tabtarget.objects | Include placeables and doors | T | off | U | L | — | Tab can also lock a lever, chest or door. |
| tabtarget.range | Maximum range | S | 5–80 m step 1 · 30 | U | L | — | Tab ignores anything farther from you than this. |
| tabtarget.tilt | Target within | S | 0–25 ° of the centre step 1 · 12 | A | L | — | The camera keeps your own tilt while the target sits within this many degrees of the middle of the view; past it it tilts just enough to bring it back. 0 always puts the target dead centre. |

**Movement** — group `walk` (530). Master = the client step guard. Header: "The stock client
tests only where a walk starts, not each step along the server's path, which is how a character
ends up past a closed door." The refusal ledger is the diagnostic `Status`, not a panel line.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|

**Hotkeys** — group `hotkeys` (540). `PanelStatus` = `Status` = "Approved on this server: F5,
Ctrl+Q …" / none.

| key | label | kind | range · default | tier | eff | section | help |
|---|---|---|---|---|---|---|---|
| hotkeys.enabled | Enable server hotkeys | T | on | U | L | — | Lets a server bind keys to its own actions, after you approve each one. Off, no key is sent and no approval is asked; keys reach the game only. |
| hotkeys.bind | Bind keys on this server… | B | — | U | — | — | Opens the server's own key-binding window. Greyed outside a server session (`KnobButton.Enabled`). |
| hotkeys.revoke | Revoke every approved key | B (confirm) | — | U | — | — | Forgets every key you approved on this server and tells the server to unbind them. Click again to confirm. |
| hotkeys.forget | Forget every server | B (confirm) | — | U | — | — | Wipes the approvals stored for every server you ever played on; the one you are on now is told to unbind too. Click again to confirm. |
| hotkeys.keydiag | — | T | off | C | L | — | key dispatch trace |
| hotkeys.consent | — | B | — | C | — | — | native consent-modal allocator probe |

Master off gates `Fire/Emit`, `Prompt` (an open modal is `Reset`) and capture; never the hooks
(`LiveMessage` is shared plumbing) and never the `CX_ENABLED` advertise. Inbound
`PresentToken`/`ArmSet` keep being consumed so re-enable is instant. *Forget every server* =
wipe every `[token=…]` section (no packet; #418's fail-closed rule) plus, in session, today's
Revoke-all. Both revoke rows confirm on a second click within 3 s. `MaxArmed` stays a const.

### 4.6 Quality presets

`quality.preset` ladders the eight area dropdowns, and every real knob ladders on its area
(§1 `Ladder`). High is the shipped default, so `cx.cfg` holds only what the player moved off
the preset. Rungs, low / medium / high / ultra:

**quality.shadows** → `shadow.dist` 60 / 90 / 120 / 200 m · `shadow.size` 512 / 1024 / 2048 /
4096 · `shadow.cascades` 2 / 3 / 4 / 4 · `shadow.cubes` 8 / 16 / 32 / 48 · `shadow.cubesize`
128 / 256 / 512 / 1024 · `shadow.cubedist` 15 / 20 / 30 / 50 m · `shadow.soft` 0 / 2 / 4 / 6 ·
`shadow.sunangle` 0 / 0 / 0.53 / 0.53 deg · `shadow.search` 4 / 8 / 16 / 16.

**quality.lighting** → `light.binreach` 0.5 / 0.65 / 0.8 / 1 · `light.bincap` 16 / 24 / 0 / 0
(0 = no cap) · `light.relevance` 0.03 / 0.02 / 0.01 / 0.005 · `light.floor` 0.001 / 0.0005 /
0.0003 / 0.0003.

**quality.reflections** → `light.probe` off / on / on / on · `light.probesize` 64 / 64 / 64 /
256 · `light.ssr` off / on / on / on · `light.ssrdetail` quarter / quarter / half / half ·
`light.ssrplanarres` eighth / eighth / eighth /
quarter.

**quality.ao** → `ao.radius` 0.5 / 0.6 / 0.6 / 0.8 m · `ao.dirs` 1 / 1 / 1 / 2 · `ao.steps`
2 / 2 / 3 / 4 · `ao.maxpx` 48 / 64 / 96 / 128 px.

**quality.detail** → `lod.px1` 800 / 650 / 500 / 300 px · `lod.px2` 300 / 220 / 150 / 80 px ·
`alloy.lod` 2 / 1.5 / 1 / 0.5 px.

**quality.grass** → `grass.density` 0.75 / 1.25 / 2 / 3 · `grass.bladedensity` 3 / 5 / 8 / 12 ·
`grass.lod` 15 / 25 / 40 / 60 m · `grass.bladelod` 10 / 20 / 30 / 45 m · `grass.shade` 15 / 25 /
40 / 60 m · `grass.shadow` none / 1 / 1 / 2 cascades · `grass.vlight` on / on / on / off.

**quality.weather** → `weather.density` 0.5 / 0.8 / 1 / 1.5 · `weather.range` 12 / 16 / 20 /
28 m · `weather.count` 4000 / 8000 / 16000 / 24000 · `weather.impacts` 0 / 0.35 / 0.6 / 1 ·
`weather.splash` 0 / 1 / 1 / 1 · `weather.edgetaps` 3 / 5 / 8 / 12 · `weather.snowsteps` 4 / 6 /
10 / 16 · `weather.mapres` 1.5 / 2 / 3 / 4 texels/m · `weather.mapcadence` 8 / 6 / 4 / 2 frames
· `weather.filmlights` 0 / 1 / 2 / 4.

**quality.effects** → `particles.soft` off / on / on / on · `particles.lit` off / on / on / on ·
`particles.shadows` off / off / on / on · `effects.halos` 0 / 1 / 1 / 1.

The Low/Medium/Ultra rungs are reasoned from the perf notes and owe an `/alloy bench` pass on
real hardware (ADR 0030).

### 4.7 Commands without a row

Every group keeps its generated `/<group>` command whether or not it has a submenu, so a
ChatOnly-only group is reached exactly as a panel group is: `/water`, `/emitsim`, `/view`,
`/models`, `/mdl`, `/dbtiles`, `/knobs`. `/settings` is exempt from `general.commands`;
everything else is gated by it.

## 5. Feature disable verdicts

A master is a row only where a player has a reason to turn the feature off (ADR 0030 §1). After
CX-111 there is no engine renderer behind Alloy, so the renderer-side masters are ChatOnly: off
is not "the stock one draws", it is "nothing draws". The caveat is what the build must do for
"off" to be what the help says.

| Feature | Master | Tier · effect | What off must do |
|---|---|---|---|
| Alloy | alloy.enabled | C · restart | `AlloyGate` is frozen at boot; off = never install, the game's own renderer draws the world. The cfg-only escape hatch, and the key the failure notice names. No group is parented on it. |
| Shadows | shadow.enabled | C · restart | caster pool + cascades are boot allocations; off = no surface takes a CX shadow. |
| Water | water.enabled | C · live | off draws no water at all: there is no stock water pass behind it. |
| Particles | particles.enabled | C · live | off draws no particles at all. |
| Particle simulation | emitsim.enabled | C · live | off steps nothing: every particle freezes. Parented on `particles.enabled`. |
| Cloaks and hair | dangly.enabled | C · live | off leaves the parts rigid; nothing else sways them. |
| Baked lighting | lightmap.enabled | C · live | off is the live light loop (the four DB-mode edges, ADR 0028, still own the tables). |
| Model detail | lod.enabled | C · live | off draws every model at full detail at every distance. |
| Fog | fog.enabled | U · live | every dial reads 0 and the area author's flat fog colour draws. |
| Volumetric fog | vfog.enabled | U · live | off (the default): no volume is built or bound and the `fog` group draws the fog; its textures are freed. Server-holdable. |
| Grass | grass.enabled | U · live | off draws no grass; the game's own Grass option gates both ways. |
| Weather | weather.enabled | U · live | `MasterChanged` hands precipitation back: the game's own rain and snow sheets fall. |
| Procedural animations | anim.enabled | U · live | every `anim.*` correction reads off; the engine's animation plays untouched. |
| Chat | chat.enabled | U · **restart** | `ChatService.Initialize` returns before any install; the hotkey observer's vanilla-focus path keys on `!chat.enabled`. Off = the stock chat window untouched. |
| Chat shortcuts | snippets.enabled | U · live | parented on `chat.enabled`. |
| Dialog | dialog.enabled | U · live | ownership flips per node at `ShowDialogEntry` (engine no-ops replies on null `+0x70`); the other hooks follow; forward while the engine panel is open; 4 trampolines kept. |
| Inventory | inventory.enabled | U · live | off (the default) leaves the stock panel; `MasterChanged` closes the CX window. |
| Nameplates | nameplate.enabled | U · live | restore the engine bubble mode via the `_pendingMode` shape, forward the real mode while off, stop the `+0x14c` clear, `Suppressing` is live (combat text reads it). |
| Combat text | combattext.enabled | U · live | `!V` forwards to the stock floating text; needs nameplates, which anchor it. |
| Quickbar | quickbar.enabled | U · live | keep replacing `Reposition` with the stock centre formula. |
| Compass, FPS overlay | quickbar.compass, fps.enabled | U · live | already. `ServerPing` thread never stops. |
| Radial menu | radial.enabled | U · live | off = the stock radial menu; the `p<N>` layout bag is untouched. |
| NUI fixes | nui.enabled | U · live | off = the stock client's own widget, solve and scrollbar behaviour. |
| Sound | sound.enabled | U · live | `SoundService.Master` hands every voice back to the engine's OpenAL path. |
| Camera modes | `<mode>.enabled` | U · live | ninth `Gate` value; restore `_fovFirst`. |
| Mouse-look | `<mode>.mouselook` | U · live | `Release()` on the off edge; per mode, not a group. |
| Tab-target | tabtarget.enabled | U · live | off (the default) = stock Tab and clicks; the lock and a pending preview cast drop on the off edge. |
| Hotkeys | hotkeys.enabled | U · live | gate the actions, never the hooks or the advertise. |
| General, About, Preset, Lights, Ambient occlusion, Effects, Image, Walls, Free camera | none | — | the header says why. |

Framework facts: the window registry is immutable after frame 1, so every live hide is a
content early-return, never an Unregister.

## 6. Primitives to build

| Primitive | Where | Notes |
|---|---|---|
| Scrollable column | `CX.Ui` | three per panel; also the dropdown list and #572's tab editor |
| Dropdown | `CX.Ui` | replaces `SetKind.Cycle`; list scrolls; `layout:` knobs use it by construction |
| Tooltip | `CX.Ui` | Advanced rows with help (the chat window's panel wraps help under the label since CX-683) |
| Section header, greyed row, off-default dot, restart badge, warning strip, Advanced disclosure | panel | per the prototype |
| Boxed master row | panel | |
| Status line, link row | panel | knob-less elements |
| Two-click confirm | panel | revoke rows, 3 s |
| Main-menu gear | `UiHostService` | glyph-atlas sprite (#425); yield exemption; `SyncUiScale` guard lifted |
| `HandleEnter` chain | `CommandRegistry` | Linux name symbol; Windows `At` row (`tools/abi/check-win-symbols.py`) |
| `KnobGroup` / `Knob<T>` / registry walk / debounced writer | `CX.Framework` | |

## 7. Build order

Each step ships on its own and leaves the client working.

1. **Registry + file.** `Knob<T>`, `KnobGroup`, binding from `Settings.Load`, the debounced
   writer, the generated `/<group>` handlers, `/knobs` as a walk, the command gate. Migrate one
   group end to end (Grass) to prove the shape; SelfCheck for the constraints.
2. **Migrate every group.** Graphics (`*Keys` → knob tables; the three `PassLifecycle` slots
   go), Camera (`CameraConfig` → six groups; `cx-camera.cfg` writer dies), UI (per-feature cfgs
   die; `ui.font` replaces chat's `font`), General + Hotkeys. Delete as you go (§8).
   `lockchat` dies here; `chat.enabled` arrives with the `HandleEnter` chain.
3. **Primitives:** scrollable column, dropdown, tooltip. Dropdown replaces the cycle button in
   chat's per-window panel too.
4. **The panel:** window, three columns, row kinds, master/parent/Advanced/reset/markers,
   `/settings` door, panel face. Delete the three per-feature panels and gears.
5. **The gear:** the three live checks, the yield exemption, the sprite, the anchor.
6. **Docs:** CLAUDE.md "Tuning knobs" (already points here), `docs/agents`, `/knobs` mention in
   README if any.

## 8. Deleted

- Names: `/vanilla`, `/draw`, `/probe`, `/halos`, `/sky`, `/beams`, `/framedump`, `/dumpshaders`,
  `/camera` and its subcommands and multi-arg forms, `/post scale`, `/post aotemporal`, `/qbar`,
  `/np`, `/ct`, `/nw`, `/cw`, Quickbar's `/settings`, Nameplate's `settings` subcommand,
  `/keydiag` as a separate name, `lockchat`.
- Code: `PassLifecycle.Tune/Describe/Reset` + the `*Keys` constants, `RenderCommands.Tune`,
  `CameraConfig.ToText/Parse` + `Set` switches, `SetKind.Cycle`, the global half of `SetAct`,
  `DialogSettings`, `NameplateSettings`, `QuickbarSettings` and their gears, the nuklear hotkey
  manager + the Options-panel "Hotkeys" button + their `SymbolMap` rows, `GameGate.Locked` /
  `OwnsFramePresent` and every `lockchat` branch (UiHostService, UiInputService, ChatService,
  GlContextService, AlloyGate, CasterPass, AlloyRenderer, Quickbar, Notice, HotkeyInputService),
  `ConfigFile.Load/Save/Baseline`, every `cx-<feature>.cfg` writer, nameplate `mode`, the FOV
  magic zero.
- Files: `cx-camera.cfg`, `cx-chat.cfg`, `cx-combattext.cfg`, `cx-dialog.cfg`,
  `cx-nameplate.cfg`, `cx-quickbar.cfg`, `cx-ui.cfg`. `cx-hotkeys.cfg` shrinks to tokens.
- Boot/live twins collapsed into one knob: `halos`/`halosize` (`halogain` is gone -- `effects.halos`
  IS the brightness), `probe`,
  `renderscale`, `shadows` (+ the feed's `_enabled`), `partbatch`, `partsamp`, `keydiag`,
  the four `mouselook*` boot keys, `camera.occlude`.

## 9. Not in this spec

Search across categories (header slot reserved), the
auto-update consent row (#31), chat's tab editor (#572), Path A-lite (#571), the hotkey
binding UI (#422, server-side), colour picker redesign, resizable panel / remembered geometry /
per-row reset, migration of `cx-*.cfg`.
