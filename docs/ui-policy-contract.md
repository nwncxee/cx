# UI policy: what a CX-aware server may set, and how

**Status:** built on the client (`managed/Framework/ServerPolicy.cs`), and the reference server implements the server end.
The inventory window (`managed/Services/Inventory/`) reads it too.

The player configures CX with knobs (ADR 0025). A server may lay a **policy** over that configuration for the
length of a session, the way a directory policy sits over a workstation's own settings: it can **hold** any knob
at a value, and it can set **component properties** no knob exposes (hide the gold view, relabel a slot, move a
button, route an action to the server). The player's own values are never overwritten: they stay underneath,
`cx.cfg` keeps only them, and they come back the moment the policy lets go.

## Wire

One message family, the existing CX device major (CLAUDE.md: reuse an opcode; a stock client discards an unknown
minor with one trace line, the research on CX-152 §6.6).

| direction | major | minor | payload (at offset 3) |
|---|---|---|---|
| server → client | `0x36` | `0x47` `PolicySet` | UTF-8 lines of `key=value`, `\n`-separated |
| client → server | `0x36` | `0x48` `UiEvent` | ASCII `action;arg;arg…` |

Send `PolicySet` only to a client that advertised `cx_enabled = 1` (`docs/hotkeys-server-contract.md`). Messages
accumulate; send as many as you like.

### Capabilities: what this client can draw

At login, beside `cx_enabled`, the client advertises its release and the NUI capabilities it can draw at that moment,
as device properties (`GetPlayerDeviceProperty`, names lowercase):

| property | value |
|---|---|
| `cx_version` | `major * 1000000 + minor * 1000 + patch`; `0.7.0` is `7000`, `0` for an unversioned build |
| `cx_cap_itemicon` | `1`: `@item:<hex>` images draw the item's own icon |
| `cx_cap_lookicon` | `1`: `@look:<hex>` images draw a described item (base item, parts, colours) |
| `cx_cap_card` | `8`: the client draws the cards declared in `card.<key>` lines (see [Cards](#cards)) and reads the `cardstyle` line and the style fields of [Style](#style), including a role identifier (`@accent`) in place of a colour and `look` for the game's own GUI art. Older values are listed below |
| `cx_cap_nuirender` | `1`: the `nui.<id>.*` render keys below apply. Live only while Alloy draws NUI, so a client with Alloy off never advertises it |
| `cx_cap_modelview` | `9`: `@model:<key>` NUI images, cards and the held image draw a live model (see [Model views](#model-views)), live only while the knobs `nui.enabled` and `nui.modelview` are on. Older values are listed below |
| `cx_cap_heldimage` | `3`: the `held.image` policy key shows an image under the pointer (`nui.enabled`, `nui.heldimage`), with `held.opacity`, `held.dx` and `held.dy`, and takes `@model:<key>`. `2` held a `@model:` name and drew nothing. `1` drew it at a fixed 0.9 opacity, centred on the pointer |
| `cx_cap_heldpick` | `2`: the `held.pick.<id>` and `held.confirm` keys show a pick at once (needs the NUI element hit test as well), with `held.pickopacity`. `1` showed a pick at a fixed 0.55 opacity |

A property is sent only while the capability is live: its hooks resolved and its knob (`nui.enabled`,
`nui.itemicons`, `nui.cards`) is on. **Absent or `0` means the client does not draw it**; send the vanilla version (a plain
image, or none). A new capability adds a `cx_cap_<name>` row here when it ships, and its value rises when its
wire form changes. A knob toggled mid-session advertises the whole set again, a withdrawn capability as `0`; whether the
server keeps the latest value for a repeated name is unconfirmed, so re-read the property when it matters.

#### Older values

A client that sends an older value reads less. Each line says what that value lacks next to the one above it.

`cx_cap_card`:

- `7` had no `look` field: a card was always drawn in flat colour, never in the game's own GUI art, whatever the player's `ui.look`.
- `6` took no role identifier (`@accent`, `@panelbg/f2`) where a colour goes, and the colours it left alone were fixed: a card drew
  the same greys whatever look and palette the player had chosen.
- `5` always drew the nearest ring of a `glow` at half the colour's opacity: no `glowalpha`.
- `4` took no `@model:<key>` image `src`.
- `3` had no style fields and no `cardstyle`; its spacing, colours, pulse, width range, size ladder, placement and delay were fixed.
- `2` gave `header`, `subtitle`, `headline`, `flavour` and `footer` their own sizes and dimming, and framed `icons` by default.
- `1` had no `card.<prefix>*` keys, no `loading` block and no `[]`.

`cx_cap_modelview`:

- `8` had no `vfx=all`, so it had to be sent the effect rows again each time the target's effects changed.
- `7` took `bg`, `lightcolor` and `lightambient` as numbers only, not as `#RRGGBB` or `#RRGGBBAA`.
- `6` had no `rot` and drew an item in the pose its file was authored in (a sword end-on). It framed a body built from parts on
  bounds the engine does not keep, so `fit` and `fitzone` did nothing with Alloy on and framed a metre-high box with it off. It
  framed everything else on the engine's bounds, which also cover dummies and emitters.
- `5` took only a hand item in `wear`, one at a time.
- `4` drew only a creature (the player or an object id) and played no named animation. It drew a creature whose model is not made
  of body parts as nothing, had no `fit`, and stood its camera at fixed defaults, so a large model overflowed its image.
- `3` showed one view at a time: with two windows open only the last loaded had its doll, and a card or the held image drew
  nothing for `@model:`.
- `2` had no `zoom`, `clip`, `pitch`, `light*`, `dragrange`, `vfxmute`, or body part in a `vfx` row.
- `1` had no `drag` and no `vfx`.

### `PolicySet` lines

| line | meaning |
|---|---|
| `inventory.mode=tabbed` | a key naming a declared knob **holds** it at that value (validated as the knob would validate chat input; a bad value is refused and logged). Only knobs of a feature that opted in (`ServerPolicy.AllowKnobs`; today `inventory.*`, `ui.look` and `ui.palette`) can be held -- the renderer, audio and the hotkeys consent stay the player's |
| `inventory.gold.visible=0` | any other key is a component property |
| `inventory.gold.visible=` | an empty value releases the key |
| `inventory.*=` | releases every key under the prefix |
| `*=` | releases everything |

At most 512 lines per message; a larger one is refused whole. The client holds at most 4096 properties, and a new key past that is refused. Knob side effects a hold triggers run on the next
frame, never inside the inbound handler. Every hold and property is **session state**: the
client drops them all at the next login (`HotkeyHandshakeService`), and nothing reaches `cx.cfg`. A module that
wants a policy for the whole session sends it on every login.

The player sees held knobs greyed in `/settings`; `/policy` lists every key the server holds, with the player's own
value beside each held knob; `/knobs` marks them `[server; yours …]`.

### Refused lines: `/policy` says which and why

A line the client does not take is not silent. `/policy` ends with a `refused:` block, one row per key:
`[kind] key=value -- reason`. The kinds are `line` (no `=`, or a message over 512 lines), `knob` (not a knob a server may
hold, or a value it does not take), `card`, `style` (`cardstyle`), `held`, `window` (`nui.<id>.*`), `model`
(`model.<key>.<setting>`) and `property` (any other key a window reads and cannot use). A setting that is out of range is
clamped, not refused; a refusal is a value the client could not read at all, so the default stands.

- One row per key, with the latest reason. A key sent again with a good value, or released, loses its row; `*=` and a
  new login clear them all.
- Bounded: 64 rows and 8192 bytes of text. Key, value and reason are cut at 96, 64 and 160 characters. When a ceiling is
  hit the oldest row goes and the block says how many were dropped.
- Properties are checked when a window reads them, so a bad `model.<key>.zoom` shows up once the image loads, not when
  the line arrives.
- Each row has an event form, `refused;<kind>;<key>;<reason>`, for a later change that sends it to the server as a
  `UiEvent`. Nothing sends it yet.

### `held.image`: an image under the pointer

`held.image=<image>` shows that image centred on the pointer, above every NUI window, and swaps the engine cursor
to `held.cursor` (the `Use` hand unless the server names another) while it is up. Send it when the player picks something up in a NUI
window, and `held.image=` when they place it. The image never takes a click: the press that places it reaches the
NUI window underneath.

`<image>` is a texture resref, `@item:<hex>`, `@look:<hex>` or `@model:<key>`, spelled as in a NUI image. Any other name (a malformed
resref, a `@model:` with no key) holds the cursor and draws nothing. The image is `held.size` px (32 unless set) for each inventory cell its art fills, at the GUI scale.
A `@model:<key>` is the model view of that key (see [Model views](#model-views)), `held.size` px on its longer side and in the view's own proportions.
It counts toward the 8 live views, and its doll is built when the hold starts and taken down 1.5 s after it ends.

The hold ends on an empty value, on `held.image=` released by `*=`, and at the next login. It also ends by itself, so a
lost empty line cannot leave a ghost:

- when the NUI window the pointer was over as the hold began is closed, even while other NUI windows stay open;
- when no NUI widget has drawn for `held.grace` ms (half a second unless set), which covers a hold that began with the pointer over no NUI window.

A client with `cx_cap_heldimage` absent or `0` ignores the key, so keep the plain NUI drag for it.

#### Optimistic pick

A server a long way off answers a pick late. Declare what a pick shows and CX shows it on the press, before the answer:

| line | meaning |
|---|---|
| `held.pick.<id>=<image>` | pressing the element whose JSON `id` is `<id>` shows `<image>` at once |
| `held.pick.<prefix>*=<image>` | the same for every element whose `id` starts with `<prefix>`; the longest prefix wins, and an exact `<id>` beats a prefix |
| `held.confirm=<ms>` | how long to wait for the answer, 50-10000, default 1000 |
| `held.size=<px>` | the held image's size for each inventory cell its art fills, 8-128 at UI scale 1, default 32 |
| `held.cursor=<id>` | the engine cursor (`cursors.2da` `CursorID`) shown while something is held, or `none` to leave the cursor alone, default 25 (`Use`) |
| `held.grace=<ms>` | how long a hold outlives a NUI that draws nothing, 0-60000, `0` never drops it, default 500 |
| `held.opacity=<0-1>` | how opaque a confirmed hold is, clamped to 0-1, default 0.9 |
| `held.pickopacity=<0-1>` | how opaque a pick still waiting for the server is, clamped to 0-1, default 0.55 |
| `held.dx=<px>`, `held.dy=<px>` | where the image sits against the pointer: px right and down of dead centre at UI scale 1, clamped to -256..256, default 0 |

Declare them when the window opens, like cards. `<image>` has the forms `held.image` takes. `held.pick.*=` releases every pick, and
an empty value releases one. Only a left press counts, and only on an element with an `id`.

The pick shows as a pending hold: dimmer than a confirmed one, with the same cursor swap. It ends in one of three ways:

- `held.image=<image>` confirms it and replaces the pending image, which may be a different one.
- `held.image=` clears it. Send that when the server refuses the pick.
- No `held.image` line within `held.confirm` ms drops it. Only a line for `held.image` itself answers; a change to any other key does not.

A client without `cx_cap_heldpick` ignores the `held.pick` keys, so keep sending `held.image` as before.

### Value forms

Booleans `1/0/true/false/on/off/yes/no`; numbers invariant-culture floats; strings verbatim to the end of the line.

### Routes and `UiEvent`

A component action is routed by `<action>.route`:

| value | effect |
|---|---|
| `engine` (default) | the engine's own message, exactly as the stock panel sends it |
| `deny` | nothing is sent; the player is told "The server does not allow that here." |
| `server` | CX sends `UiEvent` `<action>;<oid hex>;<mask hex>;<x>;<y>;<other oid hex>` and does nothing else -- the server decides |

Every `<oid hex>` in a `UiEvent` is the CLIENT's id: the server's own id with `0x80000000` ORed in
(`CNWSMessage::WriteOBJECTIDServer` sets `CGameObjectArray::LISTTYPE_MASK` on every valid id it sends; the engine's
own reader, `ReadOBJECTIDServer`, masks it back off with `& 0x7fffffff`). A server handler does the same before
`StringToObject`. `7f000000` (OBJECT_INVALID) is sent as is.

`server` is the hook point: the server sees exactly what the player tried and can run its own logic (confirm a
drop, move an item by script, refuse with its own message).

## Cards

A card is a panel CX draws beside a NUI element while the pointer rests on it: an item's details, a comparison, any
text and icons the server wants shown. The server declares it once, in a `card.<key>` line of `PolicySet`, when it
opens the window. Never send one on hover. CX lays out what the line says and adds no design of its own.

A client that does not advertise `cx_cap_card` draws no cards. CX does not hide a NUI tooltip when it draws a card, so a
server that sends both gets both.

### Keys

A card is found by the element the pointer is over. CX tries these keys in order and draws the first card it finds:

| line | matches |
|---|---|
| `card.<id>#<row>` | the element whose JSON `id` is `<id>`, in row `<row>` (counted from 0) of the list that holds it |
| `card.<id>` | the element whose JSON `id` is `<id>`, in any row |
| `card.@item:<hex>` | an element that draws that item's `@item:` image |
| `card.@look:<hex>` | an element that draws that `@look:` image |
| `card.<prefix>*` | an element whose JSON `id` starts with `<prefix>`: the longest declared prefix wins. Tried last |

- The element's `id` is the token. A stock client ignores it. Give each element that has a card its own `id`: the
  last element to declare a duplicate wins.
- A list builds its row template once, so every row shares one `id`. Use `<id>#<row>` for a card per row and `<id>`
  for the rest.
- `<hex>` of an `@item:` key is the item's `ObjectToString`, lowercase, with no leading zeros.
- A `@look:` key matches on its first 96 bytes, which is as much of the image name as CX keeps.
- Image keys work on Linux only, because only the Linux client reads the image name an element draws. The Windows client
  logs this once at start. On Windows key every card by `id`.
- Keys compare without regard to case, so ids that differ only by case share a card. A key is at most 96 bytes, and
  a client holds at most 256.

`card.<key>=[]` (an array of no cards) declares that the key has no card. It is a held key like any other, and it ends the
search: a matching `card.<prefix>*` does not show for that element. An empty value is not that. It releases the key.

A new `card.<key>` line replaces the cards of that key. An empty value, `card.*=` and a new login clear them. A
declaration CX refuses is logged, and its key keeps no card.

### The value

The value is JSON, on one line (a line break inside a text is the JSON escape `\n`), in UTF-8, at most 12,000 bytes.
It is one card object or an array of one to four. Several cards draw side by side, left to right, in the order
declared. A field CX does not know is ignored. A field it knows with the wrong type, a colour that does not parse or
a count or length over a limit (see [Limits](#limits)) is **refused**, and the whole line with it. A size that has a range is
**clamped** into it instead.

A colour is `"#RRGGBB"` or `"#RRGGBBAA"`, or in a card, a style and a `<c=>` markup tag a **role identifier**: `"@accent"`, or `"@accent/80"` for
a hex opacity (see [Style](#style)). Anything else is not a colour: not `#RGB`, not `0x` and not a number. Every colour a server sends reads the
same way, in a card, a style, a `<c=>` markup tag and a model view's `bg`, `lightcolor` and `lightambient`; the model view settings also keep their
number forms and take no role identifier. No other key a server sends is a colour: the `nui.<id>.*` render keys, the inventory and held-image keys take numbers and text, and the swatch
knobs (a colour written without the `#`) are the player's alone.

| card field | meaning |
|---|---|
| `width` | design px, clamped to the style's range (160-480 unless it says otherwise), default 300 |
| `bg` | a colour, or `{"from": colour, "to": colour, "dir": "v" or "h"}` for a gradient (`v`: top to bottom, the default) |
| `border` | the frame, a colour (see [Style](#style) for its thickness and edges) |
| `tab` | a label on a tab above the card's top left corner |
| `tabcolor` | the tab's background colour |
| `item` | the item's hex id, for `{item.name}` and `{item.stack}` in this card; without it they use the item the element draws as an `@item:` image |
| `blocks` | the blocks, drawn top to bottom |
| `images` | up to 4 images placed against a corner: `src`, `w`, `h` (px, clamped to 1-1024, default the style's `imagesize`, 64), `at` (`tl`, `tr`, `bl`, `br`, `t`, `b`, `l`, `r`, `c`, default `tr`; any other value is refused), `alpha` (0-1, default 1), `dx`, `dy` (px right and down from the corner, clamped to -1024..1024; a push out of the card makes the image overflow it) |

Every block has a `type`. Text blocks read `text` (markup, below), `size` (default 1), `color`, `dim`, `italic` and `pulse`,
all off unless declared; the fields a type reads are listed here.

| type | draws | fields |
|---|---|---|
| `text` | wrapped text | `text` |
| `status` | an icon beside wrapped text | `icon`, `text`, `px` (icon size, clamped to 4-256, default the style's `statuspx`, 22), `frame` (a frame round the icon) |
| `divider` | a thin rule across the full card | `color` |
| `icons` | a row of icons, wrapping | `icons` (up to 16 names, more is refused), `px` (clamped to 4-256, default the style's `iconspx`, 28), `frame` (a frame round each icon) |
| `section` | blocks on a tinted band across the full card | `blocks`, `tint` |
| `footer` | a band at the foot: `left` text and `right` text on one line | `left`, `right`, `tint`, `size` |

- `size` is a multiple of the player's text size, snapped to the style's `sizes` (0.7, 0.85, 1, 1.15, 1.3, 1.6 or 2 unless it says otherwise).
- `dim` lowers the colour's opacity to the style's `dimalpha` (60%).
- `pulse` fades the block's text between the style's `pulsemin` (35%) and full opacity every `pulseperiod` (1.2 s). `loading` is a `text` block with `pulse` on.
- A `section` holds blocks, and sections nest two deep.
- A block with an unknown `type` draws as `text`. With no `text` it draws nothing.

### Style

Every look value of a card is a field of the style. A server that sets none gets a card in the player's own look (see below). The
same fields can sit on a card (for that card), or in one `cardstyle=<JSON object>` line (for every card). A card's own field beats
the style's, and the style's beats the default. `cardstyle` can come before or after the card lines: cards already held are laid
out again against it. `cardstyle=` restores the defaults and so does `*=`; `card.*=` leaves the style alone. A style CX refuses is
logged and the defaults apply. Lengths are design px (they scale with the UI), colours are `#RRGGBB` or `#RRGGBBAA`, and a length
outside its range is clamped.

**Colours follow the player's look.** The player picks one look for every CX window with two knobs: `ui.look` (`native`, the game's own
GUI art, or `flat`) and `ui.palette` (`slate`, `ember`, `moss`, `wine`, `ash`). A colour field a server does not set follows that choice,
so a card sits in the same chrome as the rest of CX: `color` is the look's text colour, `bg` its panel background at the same opacity as
before, `border` its border, `frameline` its edge highlight and `tabcolor` its darkest ground. The washes are unchanged: `divider`,
`footerband`, `framefill` and `dimcolor` are the same translucent white and black whatever the look. A field the server **does** set is
drawn exactly as sent.

**A card can wear the game's own GUI art.** `look` decides: `auto` (the default) follows the player's `ui.look`, `native` asks for the art
whatever the player chose, `flat` asks for colour. It sits in `cardstyle` or on one card, not on a block -- the art is the card's ground,
not a line's. Drawn native, the card's background and border are the stock window art (`nui_windowh`, nine-sliced with the engine's
insets), its tab is the stock header art, and a framed icon's box is the window art again, so **`bg`, `border`, `border2`, `corners`,
`glow`, `framefill` and `frameline` are ignored**: whatever colours the server set for those are not drawn. Everything else still
applies -- text and run colours (roles included), `shadow` under the glyphs, every spacing, size and placement field, `divider`,
`footerband` and a `section` tint. If the art is missing (a client whose GUI haks do not carry it), the card falls back to flat colour
with the colours as sent, as every other CX window does. A client advertising `cx_cap_card` 7 or less ignores `look` and draws flat.

A colour may also name a role instead of a value, so server content moves with the look too: `"@accent"` is the role identifier, and
`"@accent/80"` the same role at a hex opacity (`00`-`ff`). It is resolved every time the card is drawn, so a player changing `ui.look` or
`ui.palette` re-colours the cards on screen. The roles are
`@panelbg`, `@border`, `@headerbg`, `@catbg`, `@subbg`, `@colborder`, `@navon`, `@accent`, `@accent2`, `@input`, `@selected`, `@tx`, `@tx2`,
`@tx3`, `@grip`, `@hi`, `@toggleoff`, `@frame`, `@frame2`, `@track`, `@listbg`, `@masterbg`, `@stripbg`, `@stripborder`, `@warn`, `@off`,
`@badgeframe` and `@ok` (case does not matter). A role no client knows refuses the line, like any bad colour, so send a role only to a
client advertising `cx_cap_card` 7 or better. Both knobs are server-holdable if you would rather decide the look yourself: `PolicySet`
lines `ui.look=native` and `ui.palette=ember`.

| field | meaning | range | default |
|---|---|---|---|
| `width` | the card's width | `minwidth`-`maxwidth` | 300 |
| `minwidth`, `maxwidth` | the range `width` clamps to | 32-2000 | 160, 480 |
| `sizes` | the text sizes `size` and `<s=>` snap to (each a baked face), up to 12 numbers | 0.25-6 | 0.7, 0.85, 1, 1.15, 1.3, 1.6, 2 |
| `pad` | the padding round the card | 0-256 | 10 |
| `gap` | the space after a `status` or `icons` block | 0-256 | 4 |
| `textgap` | the space after a `text` block | 0-256 | 2 |
| `statusgap` | between a `status` icon and its text | 0-256 | 6 |
| `icongap` | between the icons of an `icons` row | 0-256 | 4 |
| `frameinset` | between a frame and its icon | 0-256 | 2 |
| `dividermargin`, `dividerthick` | the space above and below a `divider`, and its thickness | 0-256 | 3, 1 |
| `sectionpad` | the padding inside a `section` band | 0-256 | 6 |
| `footerpad` | the padding above and below a `footer` | 0-256 | 5 |
| `footersplit` | the share of the footer's width the `left` text gets | 0.05-0.95 | 0.62 |
| `statuspx`, `iconspx` | the default icon size of a `status` and an `icons` block | 4-256 | 22, 28 |
| `imagesize` | the default `w` and `h` of an image | 1-1024 | 64 |
| `leading` | line height as a multiple of the text's | 0.5-4 | 1 |
| `slant` | how far italic text leans | -1 to 1 | 0.2 |
| `tabx`, `tabpad`, `tabextra`, `tabdrop` | the tab's left offset, its side padding, the height it adds to the line, and the text's drop from its top | 0-256 | 8, 6, 4, 2 |
| `look` | the chrome the card wears: `auto` (the player's `ui.look`), `native` (the game's GUI art), `flat` (colour) | word | `auto` |
| `color` | the default text colour | colour | `@tx` |
| `bg` | as in the card table above (`dir` also takes `d`, top left to bottom right). Without `to` the fill is flat, and `to` may be clear | colour or object | `@panelbg/f2` |
| `border` | the frame's colour | colour | `@border` |
| `borderw` | the frame's thickness | 0-256, at least 1 px drawn; an empty `sides` draws no frame | 1 |
| `sides` | which edges the frame draws: any of `t r b l` | text | `trbl` |
| `border2`, `border2w`, `border2inset` | a second frame inside the first (a double border): colour, thickness and inset | colour, 0-256, 0-256 | none, 1, 3 |
| `corners`, `cornerlen`, `cornerw` | bracket marks in the four corners: colour, arm length and thickness | colour, 0-256 | none, 12, 2 |
| `glow`, `glowspread`, `glowalpha` | a soft light outside the card: its colour, how many px it spreads, and the share of the colour's opacity the nearest ring has (each ring out is fainter) | colour, 0-64, 0-1 | none, 8, 0.5 |
| `shadow`, `shadowdx`, `shadowdy` | a drop shadow under every glyph | colour, -16..16 | none, 1, 1 |
| `divider` | the `divider`'s colour | colour | `#FFFFFF` at 19% |
| `footerband` | the `footer`'s band | colour | `#000000` at 40% |
| `framefill`, `frameline`, `framew` | the fill, line and thickness of an icon's frame | colour, colour, 0-256 | `#000000` at 40%, `@grip/c0`, 1 |
| `tabcolor`, `tabtext` | the tab's background, and its text (the default text colour when absent) | colour | `@masterbg/b0` |
| `dimcolor`, `dimalpha` | what a `dim` block with no colour of its own draws in, and its opacity | colour (RGB used), 0-1 | `#FFFFFF`, 0.6 |
| `pulsemin`, `pulseperiod` | a `pulse` block's faintest opacity, and ms for one full fade out and back | 0-1, 100-60000 | 0.35, 1200 |

The blocks take some of the same fields to override the card for one block: `after` (the space after it), `pad` (a `section` or
`footer`), `margin` and `thick` (a `divider`), `spacing` (an `icons` row's gap, or a `status` icon's), `inset`, `framefill` and
`frameline` (a framed icon), and `align` (`left`, `center`, `right`) on a text or `status`. Images take `alpha` (0-1) and more
anchors: `t`, `b`, `l`, `r` and `c` centre along the edge.

Placement and timing are read from the first card of a key, and from the style when it names none:

| field | meaning | range | default |
|---|---|---|---|
| `side` | the element's edge the cards sit against: `right`, `left`, `above`, `below` | word | `right` |
| `flip` | go to the opposite side when this one has no room | true or false | true |
| `offset` | the distance from the element | -256..256 | 8 |
| `cardgap` | the distance between cards | -256..256 | 8 |
| `align` | along that edge: `start` (tops level with the element, or lefts), `center`, `end` | word | `start` |
| `dx`, `dy` | a further shift right and down | -1000..1000 | 0 |
| `delay` | seconds before a card shows; `0` shows at once, below 0 or absent follows the player's own tooltip delay | -1..30 | the player's |
| `fadein`, `fadeout` | seconds for the fades; below 0 or absent follows the player's | -1..10 | the player's |

The group is kept on screen whatever the placement says.

### Data that arrives late

A server a long way off sends its card lines after the window is open, so the pointer can rest on an element before the
element's card exists. Send one small stand-in per id family when the window opens, and the real cards when they are ready:

- `card.<prefix>*` shows for every element of the family until a better key matches. Give empty cells and slots `card.<id>=[]`, so
  the stand-in does not show for them.
- The card on screen is swapped in place when its key gains a better match, or the key's own line changes. CX redraws it with no hide, no
  new wait and no fade, and places it again if its size changed.
- `{item.name}` and `{item.stack}` fill a stand-in with real data and need no round trip, because the client already knows the item.
- A `pulse` block can mark what is still on its way.

### Text markup

| markup | effect |
|---|---|
| `<c=#RRGGBB>...</c>` | colour (`#RRGGBBAA` for opacity, or a role: `<c=@accent>`, `<c=@accent/80>`) |
| `<s=1.6>...</s>` | size, snapped to the sizes above |
| `<i>...</i>` | italic |
| `<icon=NAME>` | an inline icon, as tall as its line |
| `<br>` or a newline | a line break |
| `<<` | a literal `<` |

A tag CX does not know, or one with a value that does not parse, stays in the text as typed. Text wraps at the card's
width. A single word wider than the card runs past its edge.

`{item.name}` and `{item.stack}` in any text, icon name or image `src` become the name and stack size the client
knows for the card's item: its own `item`, or else the one the element draws as an `@item:` image. Both are empty
when the client does not know the item.

### Images and icons

An icon or image `src` is a texture resref (up to 16 characters), `@item:<hex>`, `@look:<hex>` or `@model:<key>`. `@item:` draws that item's
inventory icon in its own colours. `@look:` draws the icon of an item described by its appearance, in the form a NUI
`@look:` image takes. CX makes one throwaway item for each distinct look the declared cards name, when the declaration
arrives, and ends it when no declared card names the look any more or the card is released. A name that does not resolve
draws nothing, never an error sprite. `@model:<key>` draws the model view of that key stretched to the image's `w` x `h` (see
[Model views](#model-views)); a card names it in an `images` entry or an `icons` row. The doll is built while the card is on screen and
taken down 1.5 s after it is not, so the first frame of a card shows no doll until it has been drawn once.
A key with no stand-in shows nothing meanwhile, and `standin` is not used in a card.

### Limits

These refuse the line (or, for keys held, the new key):

| limit | value |
|---|---|
| cards per key | 4 |
| keys held | 256 |
| bytes per key | 96 |
| bytes per line | 12,000 |
| blocks per card (sections' blocks included) | 48 |
| section depth | 2 |
| characters per text | 400 |
| runs (styled pieces) per text | 48 |
| icons per `icons` block | 16 |
| images per card | 4 |
| icon name or `src` | 120 characters |

These are clamped into their range and the card still draws:

| field | range |
|---|---|
| card `width` | the style's `minwidth`-`maxwidth`, 160-480 unless set |
| `px` of a `status` or `icons` block | 4-256 |
| image `w`, `h` | 1-1024 |
| image `dx`, `dy` | -1024 to 1024 |
| `size` of a text | snapped to the nearest of the style's `sizes` |

### Where and when it shows

CX puts the cards to the right of the element, 8 px off it, with their tops level. When they do not fit there it puts
them to the left, and it moves them as far as it takes to stay on screen. The style's `side`, `flip`, `offset`, `align` and
`dx`, `dy` change all of that. A card is drawn above NUI and takes no
input: the pointer and keys go to the window underneath.

A card shows once the pointer has rested on the element for `ui.tipdelay` (or the style's `delay`), and fades in and out with the other CX
tooltips (or the style's `fadein` and `fadeout`). Moving to another element that has a card skips the wait. It hides when the pointer leaves the element,
the window closes, or the list scrolls the element out of view.

`nui.cards` turns cards off. The capability is withdrawn with it. The debug knobs for people building cards and windows
are in `docs/live-harness.md`.

## NUI window render keys

A server sets render properties per NUI window, keyed by the window id it gave `NuiCreate`. Send them only to a client that
advertised `cx_cap_nuirender`. A stock client has no such keys and draws the window unchanged, and so does a CX client with
Alloy off.

| key | meaning |
|---|---|
| `nui.<id>.opacity=0.6` | the whole window, 0-1 |
| `nui.<id>.background=0` | the engine's own title bar, frame and body fill, 0-1. At 0 only the widgets and the server's draw list draw, and the window still drags by its hidden title bar. A `transparent` window keeps its title bar and frame, so they fade too. A window with no title, close or collapse button, `border=false` and `transparent` has nothing to fade, and a server rect the size of the window stays |
| `nui.<id>.fadein=150` | ms from invisible to the window's opacity, from the frame it opens |
| `nui.<id>.fadeout=150` | ms the window takes to fade away after it closes |

The keys are session state like every other `PolicySet` line. `nui.<id>.*=` releases a window's keys.

A fade out draws the window's last frame again, without the images in it: their textures are freed with the window. A
window's own popups (an open combo list) are not in that frame.

## Model views

`@model:<key>` as the name of a NUI image shows a copy of a creature as a live, animated, lit model on a transparent
background, redrawn every frame into the image's own texture. NUI draws it like any other image. Send it only to a client
that advertises `cx_cap_modelview`, and send the plain image (a portrait) to every other one.

The same name works as a card's image or icon and as the `held.image` value (see "Where it goes, and how many" below).

The key is any text. CX keeps its first 48 characters, so a longer key never matches its `model.<key>.<setting>` lines:
keep keys short. Its settings are policy lines, `model.<key>.<setting>`, and a client reads them when
the image loads and again whenever the policy changes. A window can open before its lines arrive, so the view shows its
stand-in until they do.

| setting | value | default |
|---|---|---|
| `target` | what the doll shows: `player`; the hex object id of a creature (NWScript `ObjectToString`); `app:<row>`, a row of `appearance.2da` in decimal (0-65535), built with every part, colour and head at 0 (a row the client's `appearance.2da` does not have shows the stand-in); `model:<resref>`, any model by name (1-16 letters, digits and underscores; a model the client does not have shows the stand-in); or `item:<look>`, an item shown on its own, where `<look>` is an `@look:` string or the hex object id of an item the client knows (a look whose base item the client lacks, or whose model files it lacks, shows the stand-in, and so does an id that is not a creature or an item). Empty releases it | no one: the view shows the stand-in |
| `standin` | a texture resref (a TGA, plain or RLE) | nothing: the image is transparent |
| `standinrect` | `x,y,w,h` in the stand-in's own pixels, counted from its top-left corner. Only that part is shown. A rectangle that does not fit inside the stand-in is ignored | the whole stand-in |
| `wear` | an `@look:` string, or the hex object id of an item the client knows, or several of them split on spaces, commas or semicolons. Each goes where its base item fits (a hand, the body, the head, the cloak), in place of what the target wears there. Up to 8 items; the rest are left out and listed by `/policy`. A look the client cannot build, by the checks `target` names, and text that is neither a look nor a hex id are left out and listed by `/policy`. An id the client does not know yet is looked up again every half second, and after 10 s it is listed by `/policy` and no longer looked for until the line is sent again. Empty releases them | nothing |
| `size` | `<w>x<h>` pixels, 1-1024 each. Read when the image loads | the stand-in's size, else `256x256` |
| `angle` | the camera's view angle in degrees, 1-170 | `30` |
| `dist` | camera distance from the creature, in metres, 0-1000. The starting distance when `zoom` is set | the distance that fits the whole model (see `fit`) |
| `height` | camera height above the creature's feet, in metres, -1000 to 1000 | the height of the middle of the model (see `fit`) |
| `fit` | `0` stops the framing: a `dist` or `height` that is not set is `4` and `0.9`. A positive number, 0.1-10, frames the model and scales the room left round it, so `1.5` stands the camera further back than `1` | `1` |
| `fitzone` | `lo,hi`: frame only this slice of a creature's height, as fractions with 0 the floor and 1 the top, so `0.75,1` is head and shoulders. The slice is no wider than it is tall, and no wider than the image's shape leaves room for, so a head slice fills a portrait image. A body made of parts is looked at along its head's own axis. `lo` must be below `hi`. Needs a creature target (`player`, an id or `app:`); anything else frames the whole model | the whole model |
| `rot` | `x,y,z`: the model's own turn in degrees, each -360 to 360, about the x axis, then the y, then the z of the view (x right, y toward the viewer, z up). It turns the model, not the camera: `yaw` and `pitch` still move the camera round the turned model, and the doll is framed as it is shown. An item is stood up first (see below); `rot` turns it from there. Anything that is not three numbers is no turn | `0,0,0` |
| `yaw` | degrees round the creature. `0` sees its right side, `90` its back, `180` its left, `270` its front. Past a full turn it wraps | `0` |
| `drag` | degrees the doll turns for each pixel a left drag that began on the image travels sideways, up to `10`. `0` turns drag off. The turn adds to `yaw` and lasts until the window closes | `0` |
| `bg` | `r,g,b,a`, each 0-1 (a missing one is 0), or a colour: `#RRGGBB` (opaque) or `#RRGGBBAA`. A malformed value is the default | `0,0,0,0` |
| `anim` | `0` leaves the doll still. An animation name (a letter, then letters, digits and underscores, up to 32) plays that animation, any the model has, for example `creadyr` (a combat stance), `ctaunt` or `cconjure1`; a name the model lacks leaves the doll in its rest pose and the client logs it once. Anything else is the creature's own idle. A creature's idle is part of the creature, so changing between the idle and another value rebuilds the doll | `1` |
| `animspeed` | the rate a named animation plays at, 0.05-8 times its own | `1` |
| `animloop` | `0` plays a named animation once and leaves the doll in its rest pose. Any other number loops it | `1` |
| `vfx` | `row[:part],row[:part],...`: the `visualeffects.2da` rows the doll wears, each on a body part (0-255, default 0), up to 16. `0` is the hand node, `1` the impact node, `2`-`11` the ground nodes `monster0`-`monster9`, any other number the root. A row on two parts is worn twice. A part that is not a row, and a row past the end of the client's `visualeffects.2da`, are skipped, logged once and listed by `/policy` (see [Refused lines](#refused-lines-policy-says-which-and-why)). A new value replaces the old set. Empty releases it. `all` on its own (any case) copies the target's effects instead of a list (see Visual effects) | none |
| `vfxmute` | `1` starts none of the sounds the doll's effects carry (their impact one-shot and their looping sound) | `0` |
| `pitch` | degrees the camera tips down in place, -89 to 89. Negative looks up. The camera does not move | `0` |
| `clip` | `near,far`: the camera's clip distances in metres, `near` 0.001 or more, `far` 100000 or less. `near` must be below `far`, or the default stands | `0.1,2000` |
| `zoom` | `min,max`: a scroll over the image moves the camera between these distances in metres (0.01-1000, `min` no greater than `max`). Scroll up comes nearer. The first scroll starts from `dist`. The view takes the scroll, so the window under it does not. No `zoom` means no scroll | none |
| `zoomstep` | metres one scroll notch moves the camera, 0.01-10 | `0.25` |
| `dragrange` | `min,max` degrees (-360 to 360) the drag turn stays inside, counted from `yaw`: `-45,45` turns a quarter either way and stops. `drag` still sets the speed and `0` still turns the drag off. No `dragrange` turns freely | none |
| `light` | `engine` shows the doll in the engine's character-preview light. `server` shows it in the light the three settings below describe. Anything else is the default | `engine` |
| `lightcolor` | `r,g,b` of a server light, each 0-4, or a colour: `#RRGGBB` or `#RRGGBBAA`, each channel 0-1 as written and the alpha ignored. A malformed colour is the default | `1,1,1` |
| `lightdir` | `x,y,z`: the direction the light comes from, in the view's own frame (x right, y up, z toward the viewer), each -1000 to 1000. It is made unit length; all zero is the default | `0.35,0.5,0.8` |
| `lightambient` | `r,g,b` added whatever the surface faces, each 0-4, or a colour in the same hex form as `lightcolor` | `0.25,0.25,0.25` |

**Framing.** Unless `fit` is `0`, the camera stands where the whole model is in view: the model's bounding box is covered by a
sphere, and the camera is as far from it as that sphere needs at the view `angle`, in the narrower of the image's two directions,
with a tenth to spare. It looks level at the middle of the box, so `yaw` and `pitch` turn the same frame. A `dist` or a `height` the server
sets replaces its half of the framing, a `zoom` replaces the distance once the player scrolls, and the box is read from the
model as built, so a pose that reaches beyond the rest pose, such as a hell hound rearing in `ctaunt`, leaves the frame: raise `fit` for such a doll.
The box is the geometry the doll draws, read from the model files (every mesh that renders, through its node's place in the model), not the engine's bounds, which also hold dummies and emitters. A creature built from body parts (a player race, `MODELTYPE` `P` in `appearance.2da`) has no bounds the engine keeps, so its box comes from its row: standing on the floor,
`HEIGHT` tall and twice `PERSPACE` wide (0.4 of its height when `PERSPACE` is empty). `fit` and `fitzone` work on that box, with Alloy on or off.

**Items stand up.** An item doll is shown the way a person holds it for a look: its longest side vertical, its thinnest side toward the viewer, the middle one across, so a
sword stands tip up with its blade face on. The pose comes from the size of the geometry the item draws, not from a table: a side within 15% of another counts as equal and keeps its
own axis, so a round or nearly cubic item is shown as authored. The engine has no such preview (its item pictures are icons; it lies a dropped item down by a per-base-item
turn about 90 degrees), so this rule is CX's. Use `rot` to change it.

**Stand-in.** The image holds the stand-in until the doll has been built and drawn once, and holds it again when the doll is
taken down. A stand-in of another size than `size` is stretched to it. With no `standin`, the image is transparent, and a
name that fails to load never draws the engine's error sprite. CX adds no stand-in of its own. Portraits (`po_*_h`) work: some
are RLE-compressed TGAs, and the engine's reader decodes them. Use `standinrect` to leave out a blank strip, for example
`0,0,256,400` for a 256x512 portrait whose art fills the top 400 rows.

**Where it goes, and how many.** The name works in any NUI image, a `NuiImage` or a `NuiDrawListImage` (use `NUI_ASPECT_STRETCH`
and a rectangle of the view's shape, so the doll is not distorted), in a card's image or icon, and as the held image. Each view is its own
doll: two windows with `@model:` images each show their own, and a window with several shows them all. Images that name the same key share
its settings, and each image NUI loads for it is a view with a doll. A card or the held image naming a key a window shows has a doll of its own, and may differ in size.

**Eight at once.** At most 8 model views are alive at the same time across NUI images, cards and the held image. A ninth stays
what it was: a NUI image keeps its stand-in and a card or the held image draws nothing, and the client logs the refusal once. A view
is alive while its NUI image is, and a card's or the held image's for 1.5 s after the last frame that drew it, so a card
that is off screen frees its doll. Each doll costs a creature, a scene and a texture of the view's `size`, so keep the views a window
needs small.

**What follows.** The doll follows the target's body, head, colours and armour as the server sends them, and what the
target holds in either hand: an equip or an unequip shows within a frame. Weapons and shields are copies. CX builds a
throwaway item from the real one's base item, model parts and colours, so the target's own items are never moved,
equipped or freed. The helmet is a copy too, so the doll shows it and its head the moment the target puts it on or takes it off. Closing the window takes the doll down with every copy and its cost with it.

**Targets that are not creatures.** `app:<row>` builds a throwaway creature from the row alone, so a creature type with a body made of
parts (a player race) shows its default parts. `model:<resref>` shows the model itself, with the animations it carries. `item:<look>` shows the item
as the engine draws it on the ground, a composite weapon whole. `wear` and `vfx` apply to the creature targets and `app:`; a `model:` or
`item:` doll has no hands to wear in. A creature whose model lacks a hand node draws that hand's item at its root. Each of these costs
what a creature doll costs.

**Previewing an item.** `model.<key>.wear` puts items the target does not wear on the doll: any number, split on spaces, commas or
semicolons, each an `@look:` string or the hex id of an item the client knows. An item goes where its base item fits: a weapon or
shield in the hand slot, armour in place of the body, a helmet on the head, a cloak on the back. The last item for a slot wins, and
releasing the key brings back what the target wears. Send an item's look as the `@look:` string the server already builds for
`@look:` icons; a hex id is looked up again twice a second until the client knows the item. Armour takes the item's model parts and
colours (its per-part colours too, from an `@look:`), a cloak its model and colours. A preview costs a throwaway item per hand and
helmet. An `@look:` the client cannot build is left out, never swapped for the engine's stand-in model, and logged once: a base item
the client's `baseitems.2da` lacks, a model file the base item's model type loads and the game does not have (`<class>_<nnn>`, or `<class>_b_<nnn>`, `_m_` and `_t_` for a
composite weapon), a cloak number past the end of `cloakmodel.2da`, or an item visual effect past the end of its table. Armour part numbers are not
checked by CX: the engine's body builder looks up each part's model file itself. A helmet number with no model leaves the doll as it was.

**Visual effects.** The doll copies the target's looks, not its effects, so it wears none unless `vfx` lists them.
With `vfx=all` the doll wears what its target shows, read from the target's effect list every frame, so the server need not send the line again when
an effect comes or goes. An effect is copied when the client has shown it (one that is hidden, for example while its creature is faded out, is
left out), when it lasts (a one-shot impact is over in a moment and would restart on every hit) and when it has no source object (a beam from another
creature has no end on the doll). An effect with no model of its own counts: the engine shows a colour change or a glow without one. Each effect
brings its row and body part; the same row on the same part counts once, and the first 16 are kept. An effect added to the target is added to the
doll. Any other change, such as one ending, puts the doll's effects on again, which restarts them. `vfxmute` applies as it does to a list. `all` needs a
creature target (`player` or an id); an `app:` doll has no effects to follow and wears none. The client logs `NuiModelVfx: mirroring N effects from the
target` when the doll changes. The list is read at the same offsets on Linux and on Windows, so nothing in this feature is Linux-only.
CX cannot tell a permanent effect from a temporary one, because both reach the client the same way. The server knows,
and lists the ones that belong to the character's look, usually the permanent ones. An effect that adds light, such as
a fire ring, writes its square into the image's alpha as well as its light. Alloy draws a doll that has such a draw twice,
over black and over white, and reads each pixel's coverage from the difference, so on a transparent or translucent `bg` the
fire covers what it lights and the window shows through the rest. With Alloy off the engine draws the doll in one pass: give a
doll with effects an opaque `bg` there. The doll's effects play their sounds where the doll stands, which is not where the player is.

**Lighting.** With Alloy on, a server light shades the doll as texture * (`lightambient` + `lightcolor` * the larger of 0 and the surface
normal dotted with `lightdir`) + what the model glows on its own, in linear light. Every view has its own light. With Alloy off the engine's
character-preview light shades the doll, whatever `light` says, because the engine cannot take another.

A shiny part (a helm, a blade) takes its reflectivity from the texture's alpha and reflects the environment map its creature names. A
creature that names none, or one that does not load, gets the grey the engine bakes in with environment maps off, so the part reads as
plain grey metal.

**Zoom and drag.** `zoom` and `drag` start only on the image's own rectangle, while the doll is built. A drag that began there keeps
turning the doll until the button comes up, wherever the pointer goes, even outside the game window. `dist` is the starting distance; the
player's scroll replaces it until the window closes, and a `zoom` that changes pulls it back inside the new range.

**Limits.** Eight views at a time. A card's or the held image's view takes its settings from the same `model.<key>.` lines, but
not `standin`, `standinrect`, `drag` and `zoom`: those need a NUI image to stand in or take the pointer. A creature target
must be a creature the client knows. A `wear` item that fits none of a hand, the body, the head and the cloak is ignored, logged and listed by `/policy`. The engine draws a cloak only
while the client's cloak option is on, so a doll shows none with it off.

## The inventory window's keys

Knobs (all holdable): `inventory.enabled`, `inventory.size`, `inventory.mode`, `inventory.kilograms` (a kilograms tooltip on the weight)
(`tabbed`/`scrolled`), `inventory.search`, `inventory.dropground`.

Components, each with `.visible` (bool), `.label` (string), `.dx` / `.dy` (design px, the vanilla panel's scale:
one backpack cell is 33):

| id | what |
|---|---|
| `title` | title bar and its text |
| `close` | the close button |
| `search` | the search box |
| `equip` | the whole equipment area (hiding it hides every slot) |
| `slot.<id>` | one equip slot: `head chest boots arms righthand lefthand cloak leftring rightring neck belt arrows bullets bolts` |
| `backpack` | the item grid |
| `scroll` | the scrollbar of the scrolled mode (in place of the tabs) |
| `tabs` | the page tabs |
| `ac` | the armour class, at the foot of the armour slot |
| `gold`, `weight` | the bottom-bar texts (`gold.label` / `weight.label` are the unit suffixes) |
| `goldbutton` | the drop-gold button right of the gold (hidden with `gold`; `.label` is its tooltip); it opens the CX amount prompt |

The loot pane (an open container, attached beside the window) takes `inventory.loot.label`: one title for every
container in place of the container's own name.

Window-wide properties:

| key | effect |
|---|---|
| `inventory.backpack.pages=N` | only pages 1..N are shown, scrolled to, or accept drops (1-6) |
| `inventory.readonly=1` | no drag, no quick-move, no take from a container; the item radial (examine included) still works |
| `inventory.skin.<part>=<resref>` | re-point one piece of the game-art skin: `frame header cell slot button buttonhot buttonon close edit trough thumb gold goldhot goldcursor lootcell sil.<slot id>` |
| `quickbar.skin.<part>=<resref>` | the same for the CX quickbar's skin: `frame slot grip` |

Actions for `.route`: `inventory.equip`, `inventory.unequip`, `inventory.move`, `inventory.merge`,
`inventory.split` (shift-click a stack: half), `inventory.drop`, `inventory.use`, `inventory.radial` (`x;y` are the
click's screen point for `use` and `radial`), `inventory.barter`, `inventory.gold`, `inventory.quickbar`, and for an open
container `inventory.take` (out of it: to a backpack cell, or `x;y` = `255;255` when the server picks, from a
double-click or ctrl-click) and `inventory.put` (into it, from the backpack or elsewhere in it); for `move`, `unequip`,
`take` and `put` they are the target cell in the wire's form, the game's own: column, and global row counted from each
page's bottom -- the backpack's 6-row pages, a container's 5-row ones -- and `take` / `put` name the container as the
other oid).

World actions -- an item dragged out of the window, or gold carried out of the gold prompt, released over the world --
are resolved by the stock drop rules first, then routed: `inventory.drop` (the ground, at the point), `inventory.use` (a
potion on the player or the player's associate), `inventory.barter` (a backpack item on another creature), `inventory.gold` (the ground, a
creature, a placeable or a usable ground item). `deny` on `inventory.gold` also refuses the gold button itself. Their
`server` event appends the world point:
`<action>;<oid hex>;<mask hex>;<amount>;0;<target oid hex>;<x>;<y>;<z>` (the item and its source slot mask; for gold
the oid is the target and `amount` the gold; `7f000000` = the ground). An item released over a quickbar button routes
`inventory.quickbar` with the button's slot (0-35, three rows of 12) in `amount`; `engine` assigns it as the stock
quickbar's own drop does. An item dragged out of a container drops or is used like a worn one (no barter, no quickbar,
as the stock drag).

A skin part the client does not have refuses the **whole** skin (logged with every missing name, a notice on
screen, the window draws its flat look): alias only art you ship (research: CX-152).

## Example

```
inventory.gold.visible=0
inventory.backpack.pages=4
inventory.drop.route=server
inventory.title.label=Satchel
inventory.mode=tabbed
```

Hides the gold view, limits the backpack to four pages, hands every ground-drop to the server as
`inventory.drop;<oid>;0;0;0;7f000000`, renames the window and holds it in tabbed mode (the player's own mode returns at the next
login).

## Server side

The reference server implements the server end: it writes `PolicySet` and reads `UiEvent` beside its hotkeys handler.
