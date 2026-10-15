# Nameplate + overhead chat bubble — implementation contract

Two world-anchored overhead elements sharing one anchor per object, mutually exclusive:

- **plate** — portrait + name + health band, while the object is hovered or Tab-highlighted
- **bubble** — what the object just said, when no plate is up

Vanilla is suppressed; we draw both ourselves in the CX UI. Full RE: CX-179.
Managed seam guide: `docs/agents/nameplate-seam.md`. Both are authoritative — read them.

## Namespace / layout

All new code under `managed/Services/Nameplate/`, namespace `CX.Services.Nameplate`.

| file | owner | contents |
| --- | --- | --- |
| `NameplateNative.cs` | A | symbols, offsets, every native read; no UI types |
| `PlateTable.cs` | A | id-keyed entry table, cold string resolve, per-frame refresh/evict |
| `NameplateService.cs` | A | `IExtenderService`, hooks, `Window`, per-frame draw, `/nameplate` |
| `NameplateConfig.cs` | B | config record + `Parse`/`ToText`/`SelfCheck` |
| `PlateLayout.cs` | B | pure layout + fade math + `SelfCheck` |
| `NameplateSettings.cs` | B | `SettingsPanel` rows |

## Frozen contract between A and B

```csharp
// --- B owns this type; A reads it. Fields only, no behaviour. ---
public sealed class NameplateConfig
{
    public bool  On            = true;   // master
    public int   Mode          = 2;      // 0 off, 1 text only, 2 portrait + background (vanilla parity)
    public bool  Outline       = true;
    public bool  Background    = true;   // independent of Mode; Mode 2 forces it on at defaults
    public bool  Portraits     = true;
    public bool  HealthBand    = true;
    public bool  HealthBar     = false;  // thin bar under the name (ours, not vanilla)
    public float MaxDistance   = 45f;    // metres; 0 = unlimited
    public float FadeDistance  = 10f;    // fade band before MaxDistance
    public int   BubbleLines   = 3;      // max wrapped lines kept per bubble
    public int   BubbleQueue   = 2;      // how many recent lines stack (vanilla = 1); max = PlateTable.MaxLines
    public float BubbleWidth   = 300f;   // design px before Metrics.Scale; cfg key `bubblewidth`.
                                         // Max width of BOTH a bubble box and a plate NAME column: a long
                                         // name wraps to it on top of BreakName's author breaks.
    public bool  ChannelStyle  = true;   // per-channel colour/emphasis
    public bool  ClickToTell   = false;  // click a plate's NAME -> prefill /tell <name>; OFF by default
    public static NameplateConfig Parse(string text);
    public string ToText();
    public static void SelfCheck(ExtenderLog log);
}

// --- B owns; A calls per frame, per entry. Pure, no allocation, no GL. ---
public readonly struct PlateMetrics   // design-px, pre-scale
{
    public readonly float W, H, PortraitX, PortraitY, PortraitSize, NameX, NameY, BandX, BandY, BarX, BarY, BarW, BarH;
    public readonly float TextW;          // added: the column a centred line is centred in
}
public static class PlateLayout
{
    /// Anchor is the engine's projected point (screen px). Returns the top-left the
    /// caller should draw at, given a plate of size (w,h) drawn ABOVE the anchor.
    public static void Place(float anchorX, float anchorY, float w, float h, out float x, out float y);
    public static PlateMetrics Measure(float nameW, float bandW, bool portrait, bool band, bool bar, float lineH, float scale);
    /// added: an N-line name (PlateLayout.BreakName / SplitLines); the one-line form delegates here.
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band, bool bar, float lineH, float scale);
    /// added: left edge of one line in the text column. EVERY line of EVERY plate type centres there --
    /// name lines, the health band and every override line -- as vanilla does. The column, never the whole
    /// box: centring in the box would slide the text under the portrait. The health bar spans the column
    /// outright, so it is centred by construction.
    public static float LineX(float colX, float textW, float lineW);
    /// added: top edge of the portrait -- vertically centred against the content column, clamped to the pad
    /// so a portrait at least as tall as its content never gains empty space above it. The SIZE rule is
    /// unchanged: min(nameLines, 2) * lineH + band/bar.
    public static float PortraitTop(float pad, float contentH, float portraitSize);
    /// added, COLD: normalise LF / CR / CRLF / literal backslash-n / 3+ spaces down to single LF breaks.
    public static string BreakName(string s);
    public static int SplitLines(string s, int[] start, int[] len, int max);
    /// 1.0 inside MaxDistance-FadeDistance, ramps to 0 at MaxDistance. 0 => cull.
    public static float DistanceFade(float dist, float maxDist, float fadeDist);
    /// Hold at 1 for holdSec then linear to 0 over fadeSec (ChatAnim.FadeAlpha shape).
    public static float BubbleAlpha(long nowMs, long postedMs, float holdSec, float fadeSec);
    public static void SelfCheck(ExtenderLog log);
}

// --- B owns; A wires it. Mirrors QuickbarSettings. ---
public sealed class NameplateSettings
{
    public NameplateSettings(Func<NameplateConfig> get, Action onChanged);
    public SettingsPanel Panel { get; }
    public void Open(); public void Close(); public bool IsOpen { get; }
}
```

`SetAct` additions, if any, go in `managed/Ui/SetAct.cs`; prefer `SetAct.ToggleBit` +
a `Bit` discriminator so no enum member is needed.

## Verified engine facts (do NOT re-derive; do NOT guess beyond these)

Symbols (add to `managed/Framework/SymbolMap.cs`, mangled):

| purpose | mangled |
| --- | --- |
| suppression / user mode | `_ZN21CGuiInGamePopUpBubble17SetChatBubbleModeEi` |
| plate text (creature) | `_ZN12CNWCCreature22ShowFeedbackBubbleTextEv` |
| plate text (placeable) | `_ZN13CNWCPlaceable22ShowFeedbackBubbleTextEv` |
| bubble/plate text sink | `_ZN10CNWCObject18SetPopUpBubbleTextE10CExoStringiff` |
| chat channel latch | `_ZN11CNWCMessage26HandleServerToPlayerChatEh` |
| object by id | `_ZN13CClientExoApp13GetGameObjectEj` (already `SymbolMap.GetGameObject`) |

Verify each with `./sym.sh -m <pattern>` before use; correct any that don't match exactly.

Offsets (all verified in CX-179):

- `CNWCObject + 0x08`  = **uint object id** (verified by disassembly at `0x6a0712`)
- `CNWCObject + 0x0c`  = object type byte (5 creature, 6 item, 9 placeable, 0xa door)
- `CNWCObject + 0x38/0x3c/0x40` = world x/y/z
- `CNWCObject + 0xb8`  = `CGuiInGamePopUpBubble*` (nullable)
- `CNWCObject + 0x104` = 1 ⇒ this object gets **no** plate and **no** bubble (honour it)
- `CNWCObject + 0x120` = flag mask; bit 4 = plate on hover, bit 8 = plate under Tab
- `CNWCObject + 0x124 / +0x128 / +0x130` = **`ObjectTextBubbleOverride`**: mode int (1 replace,
  2 prepend, 3 append, else none), `char*`, dword length. Same offsets for creature, placeable
  and item (the item's `+0x16c/0x170/0x178` is this triple through its base at `+0x48`). Append
  is `text + override` and the separators are the SERVER's — never trim them. §8.5.
- `CNWCCreature + 0x3a8` = reaction byte: 1 friend (green), 2 enemy (red), else neutral (blue)
- `CNWCCreature + 0x3d0` = `CNWCCreatureStats*`
- `CNWCCreature + 0x468` = **plate currently shown for me** (set in `ShowFeedbackBubbleText`,
  cleared in `MouseOff`). `CNWCPlaceable + 0x218` is the same flag.
- `CNWCCreature + 0x478` = area id; hide when != player's area (`module+0xf8`, `+0x118`)
- `CNWCCreatureStats + 0x20/0x28` first name char*/len, `+0x30/0x38` last, `+0x40/0x48` override
  (override wins outright; else "first last"; else whichever is non-empty; else "")
- `CNWCCreatureStats + 0x8e` = **damage-level byte**, 0..5
- bubble `+0x154 / +0x158` = **projected screen anchor x/y** (floats, whole-pixel snapped)
- bubble `+0x14c` = 1 while live chat text owns the bubble (see the reset below)
- bubble `+0x148` = default creature id
- `*g_pAppManager` → `+8` = `CClientExoAppInternal`; `+0x180` word: **bit 0** = the
  "show mouse-over feedback" option, **bit 1** = Tab currently held. Two derefs.

Health bands, index = `stats[0x8e]`. The plate shows what the client's `damagelevels.2da` `STRING_REF` cell
resolves to through the TLK. The text column is the stock TLK's answer, for reference only: no word is typed into
the code. A blank result, or a cell `C2DA::GetINTEntry` refuses, draws no caption. The engine's own bubble ignores
that refusal and resolves the 0 written for an empty cell, which is strref 0, "Bad Strref".

| idx | `STRING_REF` | text | colour |
| --- | --- | --- | --- |
| 0 | 6409 | Uninjured | 128,255,128 |
| 1 | 6410 | Barely Injured | 200,255,1 |
| 2 | 6411 | Injured | 255,200,1 |
| 3 | 6412 | Badly Wounded | 255,100,1 |
| 4 | 6413 | Near Death | 255,80,64 |
| 5 | 6414 | Dead | 128,128,128 |

Row 3's `LABEL` is `HEAVILY_WOUNDED`, but a label is never shown: the word is strref 6412, "Badly Wounded".

`SetPopUpBubbleText` ABI — **verified by disassembly at `0x7e8050`**:
`(IntPtr this /*rdi*/, IntPtr pExoString /*rsi, hidden pointer*/, int forceShow /*edx*/,
float perChar /*xmm0*/, float fixedSec /*xmm1*/)`. `CExoString` = `char*` at +0, length at +8.
Decay = `fixedSec > 0.15f ? fixedSec : clamp(len * perChar * 0.15f, 4f, 12f)`. The threshold is **0.15, not
0** -- `CGuiInGamePopUpBubble::SetText` compares `ucomiss 0x1313280,%xmm3; jbe <length-formula>` at `0x69fdf3`,
and `.rodata 0x1313280` is `9a99193e` = 0.15f.

Chat channels that get **no** bubble: `5`, `0x0c`, `0x0f`. Object types 7/9/10 get a
`"[name]: "` prefix in the bubble text; creatures do not.

## Hook plan (A)

1. `SetChatBubbleMode(int)` — record the requested value as the user's display mode
   (this is the `ui.text-bubble-mode` parity knob), then **call the original with 0**.
   Also call it once at first frame so the initial state is suppressed. This is why no
   draw hook is needed: bubble mode 0 guards every activation path, yet `UpdatePopUpBubble`
   keeps refreshing `bubble+0x154` for every bubble-owning object, every tick.
2. `CNWCCreature::ShowFeedbackBubbleText` and `CNWCPlaceable::ShowFeedbackBubbleText` —
   set a static `_inPlate = true`, call the original (it sets `+0x468`/`+0x218` and drives
   the vanilla state machine we are polling), clear the flag. Record `this`'s object id
   as a plate candidate.
3. `CNWCObject::SetPopUpBubbleText` — call the original, then: if `_inPlate` do nothing
   (that was the plate's own text, we build our own); else it is **chat** — read the
   `CExoString`, `ColorRunParser.Parse` it, record it against `this`'s id with the decay
   above, and then **reset `*(int*)(bubble + 0x14c) = 0`**.
   *That reset is load-bearing*: under bubble mode 0 nothing else ever clears it, and
   `MouseOver` refuses to build a plate while it is 1 — without the reset a creature's
   plate dies permanently the first time it speaks. We enforce chat-wins ourselves.
4. `CNWCMessage::HandleServerToPlayerChat(uchar)` — latch the channel byte in a static for
   the `SetPopUpBubbleText` that follows on the same stack; clear on return. Drives
   `ChannelStyle`. If the symbol or arg shape doesn't match, drop this hook and ship
   `ChannelStyle = false` — it is the one optional hook.

All four are cold or warm (input/message paths), never per-frame render hooks.

## Per-frame model (A)

One `Window`, `Focusable = false`, **sent to the back** so plates never paint over the
chat box or dialog (`UiHost` assigns Z in name-sorted registration order; add a
`UiHost.SendToBack(Window)` if that is the smallest fix).

Entries are keyed on **object id**, sourced only from the hooks. Each frame:

1. `GameGate.InGame` false ⇒ draw nothing. On the `InModule` true→false edge, clear the table.
2. For each entry: `GetGameObject(id)`; **null ⇒ evict** (that is the despawn signal).
3. `obj+0x104 == 1` ⇒ skip. `bubble = *(obj+0xb8)`; null ⇒ skip.
4. Anchor = `bubble+0x154 / +0x158`. Off-viewport ⇒ skip.
5. Distance = |camera − obj position|; camera is `module+0xe8` → `Camera::GetPosition()`
   (vtable slot `+0x48`). **Never** the Alloy `CameraPos` global — at present time that is
   the UI camera `(0,0,742)`. `PlateLayout.DistanceFade` ⇒ 0 ⇒ skip.
6. Plated? (`+0x468` / `+0x218` non-zero) ⇒ draw the plate. Else a live bubble ⇒ draw it.
   Never both — plate wins while it is up, and a bubble that is still within its decay
   resumes when the plate drops.

**Zero allocation in the draw.** Names, portrait resrefs, bubble text, colour runs and
text wrapping are all resolved **cold** (on the hook, or on first sight of an id) into a
preallocated entry record. The frame walks cached data and emits into a reused `float[]`.
Cache the wrap keyed on width + `TextRig.AtlasEpoch` + `Metrics.Scale`.

## Vanilla parity — the acceptance list

Every one of these must hold, and each gets a line in the commit message:

1. `internal+0x180` bit 0 off ⇒ no plate on plain hover; **Tab still plates** (the engine
   tests the whole word, so bit 1 overrides). Reproduce that exactly.
2. Bubble mode 0 = nothing; 1 = text only, no portrait, no background; 2 = portrait + background.
3. Font follows the game's large-font/UI scale via `Metrics.Scale`. There is **no plate-specific size knob**
   -- vanilla has none either, and the shared glyph atlas is baked once at the effective px, so a scale factor
   would only inflate chrome around unchanged glyphs.
4. Chat wins over the plate while a line is live; un-hovering does not cut a line short.
5. Object types 7/9/10 get the `"[name]: "` prefix; creatures do not. (A speech card's name header already
   names the speaker, so under one the prefix is wrapped past -- `nameplate.speaker`.)
6. Channels 5, 0x0c, 0x0f never bubble.
7. Decay: server-supplied `fixedSec` if **> 0.15**, else `clamp(len*perChar*0.15, 4, 12)`.
8. Tab plates **creatures only**; placeables and items plate on hover only; doors and
   triggers never plate but do bubble.
9. `obj+0x104 == 1` ⇒ neither element.
10. A creature in a different area than the player is hidden.
11. GUI hidden (hide-UI key, cutscenes) ⇒ nothing draws.
12. **No idle floating names** — a plate needs hover or Tab. (`ShowNames` is opt-in, ours.)

Deliberate divergences from vanilla, to be stated in the commit message:
- bubbles **queue** (`BubbleQueue`, default 2) instead of the last line overwriting;
- distance fade / max distance (vanilla has neither);
- our own overlap separation replaces `BumpAllBubbles`, which bubble mode 0 disables. Two rules of ours:
  a box in its FADE-OUT takes no part (it neither pushes nor is pushed, and fades where it stands, so
  sweeping the cursor over a crowd leaves no trail of dying plates shoving the live one), and the boxes
  that do take part stack by SEND TIME -- newest nearest its speaker, older riding above, so a
  back-and-forth reads chronologically. A plate has no send time and sorts as "now";
- plate **names wrap** at the max text width, and author breaks are honoured (a real
  LF/CR/CRLF, the literal `\n`, or a run of 3+ spaces). Vanilla draws a name on one line
  at any length, which is why builders pad names with spaces to fake a wrap.

## Extras (requirement 3) — build exactly these, no more

- **Bubble queue** — up to `BubbleQueue` recent lines stack upward, oldest fading first.
- **Channel styling** — per-channel colour and emphasis (whisper dim + italic, shout
  larger, party/tell tinted), driven by the latched channel byte. `ChannelStyle` off ⇒
  one neutral style.
- **Distance fade** — `MaxDistance` / `FadeDistance`.
- **Click-to-tell** — clicking a plate's **name text** prefills the chat entry with `/tell <name> `.
  Reuse the chat's existing entry/tell path; do not invent a second one. **Off by default**, and the plate
  window publishes **no `HitTestOverride`**: a plate must never take the cursor from the engine's contextual
  pointer, nor take a click the game world would have had.
- **Health bar** — optional thin bar under the name, coloured by band. Off by default. The fill is the
  creature's EXACT HP (`(current + temporary) / max`, `CNWCCreatureStats +0x88/+0x86/+0x8a`) where the
  server sent it -- the player's own creature and party members only (update flag `0x400`, RE §4.5) -- and
  only while it lands in the band the server last sent, which is how HP left stale by a creature leaving
  the party is caught. Everyone else fills to the band's midpoint.
- **Speech card** — `nameplate.speaker`: name and portrait (default), name only, portrait only, or neither. With either,
  a bubble is one card: the speaker's portrait top-left (`nameplate.portraitsize` lines tall, 2–6, default 3), their name as a header
  in the reaction colour (wrapped as the plate wraps it, so "name + title" keeps both lines), then every queued line oldest first, left-aligned. The engine's
  `"[speaker]: "` prefix on a placeable's/door's line (types 7/9/10) is wrapped past while the header
  names the speaker. Neither = the plain per-line boxes, as on the stock game.
- **Show while talking** — `nameplate.whiletalking`, off by default: hover or Tab plates a talking object
  too, the plate on the anchor and the bubble stacked above it as one separation box. The bubble leaves to
  the plate what it shows: the name always, the portrait when the plate draws one. Diverges from
  acceptance §4 on purpose, and only when asked.
- **`ObjectTextBubbleOverride`** — the module's own plate text (§8.5). The engine composes one
  string, `name\nband`, and glues the override onto its head (mode 2) or tail (mode 3), or
  replaces it outright (mode 1); we keep name and band as separate boxes, so the segment at the
  joint continues the band line (append) or prefixes the first name line (prepend) and the rest
  become lines of their own. Colour tags in it are `ColorRunParser.Parse`d cold, like a chat
  line's. There is no hook — the field is written inside two big object-update handlers — so it
  is polled per frame on plated entries with an allocation-free compare.

Nothing else. No threat meters, no target frames, no combat text.

## Build / verify

- `./build.sh` must pass. `./run-demo.sh` must pass — it fails the build if any
  `SelfCheck` line reports FAIL, and that is the **only** automated test that exists.
- Every pure piece gets a `SelfCheck(ExtenderLog)` called from `Initialize` before any
  stand-down return: config round-trip + defaults, layout, distance fade, bubble alpha,
  the decay formula, the band table.
- Comment rule: XML `<summary>` on declarations that need it; no inline prose except a
  single terse line for a magic offset or an ordering hazard. `ponytail:` markers name a
  known ceiling and its upgrade path.
