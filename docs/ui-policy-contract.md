# UI policy: what a CX-aware server may set, and how

**Status:** client side built (`managed/Framework/ServerPolicy.cs`); no server sends it yet. The first consumer is
the inventory window (`managed/Services/Inventory/`).

The player configures CX with knobs (ADR 0025). A server may lay a **policy** over that configuration for the
length of a session, the way a directory policy sits over a workstation's own settings: it can **hold** any knob
at a value, and it can set **component properties** no knob exposes (hide the gold view, relabel a slot, move a
button, route an action to the server). The player's own values are never overwritten: they stay underneath,
`cx.cfg` keeps only them, and they come back the moment the policy lets go.

## Wire

One message family, the existing CX device major (CLAUDE.md: reuse an opcode; a stock client discards an unknown
minor with one trace line, the research on CX-152 §6.6).

| direction | major | minor | payload (ASCII, at offset 3) |
|---|---|---|---|
| server → client | `0x36` | `0x47` `PolicySet` | lines of `key=value`, `\n`-separated |
| client → server | `0x36` | `0x48` `UiEvent` | `action;arg;arg…` |

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
| `cx_cap_card` | `2`: the client draws the cards declared in `card.<key>` lines (see [Cards](#cards)). `1` had no `card.<prefix>*` keys, no `loading` block and no `[]` |
| `cx_cap_nuirender` | `1`: the `nui.<id>.*` render keys below apply. Live only while Alloy draws NUI, so a client with Alloy off never advertises it |
| `cx_cap_heldimage` | `1`: the `held.image` policy key shows an image under the pointer (`nui.enabled`, `nui.heldimage`) |
| `cx_cap_heldpick` | `1`: the `held.pick.<id>` and `held.confirm` keys show a pick at once (needs the NUI element hit test as well) |

A property is sent only while the capability is live: its hooks resolved and its knob (`nui.enabled`,
`nui.itemicons`, `nui.cards`) is on. **Absent or `0` means the client does not draw it**; send the vanilla version (a plain
image, or none). A new capability adds a `cx_cap_<name>` row here when it ships, and its value rises when its
wire form changes. A knob toggled mid-session advertises the whole set again, a withdrawn capability as `0`; whether the
server keeps the latest value for a repeated name is unconfirmed, so re-read the property when it matters.

### `PolicySet` lines

| line | meaning |
|---|---|
| `inventory.mode=tabbed` | a key naming a declared knob **holds** it at that value (validated as the knob would validate chat input; a bad value is refused and logged). Only knobs of a feature that opted in (`ServerPolicy.AllowKnobs`; today `inventory.*`) can be held -- the renderer, audio and the hotkeys consent stay the player's |
| `inventory.gold.visible=0` | any other key is a component property |
| `inventory.gold.visible=` | an empty value releases the key |
| `inventory.*=` | releases every key under the prefix |
| `*=` | releases everything |

At most 512 lines per message; a larger one is refused whole. Knob side effects a hold triggers run on the next
frame, never inside the inbound handler. Every hold and property is **session state**: the
client drops them all at the next login (`HotkeyHandshakeService`), and nothing reaches `cx.cfg`. A module that
wants a policy for the whole session sends it on every login.

The player sees held knobs greyed in `/settings`; `/policy` lists every key the server holds, with the player's own
value beside each held knob; `/knobs` marks them `[server; yours …]`.

### `held.image`: an image under the pointer

`held.image=<image>` shows that image centred on the pointer, above every NUI window, and swaps the engine cursor
to the `Use` hand (`cursors.2da`, `CursorID` 25) while it is up. Send it when the player picks something up in a NUI
window, and `held.image=` when they place it. The image never takes a click: the press that places it reaches the
NUI window underneath.

`<image>` is a texture resref, `@item:<hex>` or `@look:<hex>`, spelled as in a NUI image. Any other name (a model
view, a malformed resref) holds the cursor and draws nothing. The image is 32 px a cell at the GUI scale.

The hold ends on an empty value, on `held.image=` released by `*=`, and at the next login. It also ends by itself
when no NUI widget has drawn for half a second, so a lost empty line cannot leave a ghost once the last NUI window
is closed. While any other NUI window stays open CX cannot tell that the one that held the image closed: send the empty
value when your window closes. A client with `cx_cap_heldimage` absent or `0` ignores the key, so keep the plain NUI
drag for it. The cursor swap is Linux-only for now.

#### Optimistic pick

A server a long way off answers a pick late. Declare what a pick shows and CX shows it on the press, before the answer:

| line | meaning |
|---|---|
| `held.pick.<id>=<image>` | pressing the element whose JSON `id` is `<id>` shows `<image>` at once |
| `held.pick.<prefix>*=<image>` | the same for every element whose `id` starts with `<prefix>`; the longest prefix wins, and an exact `<id>` beats a prefix |
| `held.confirm=<ms>` | how long to wait for the answer, 50-10000, default 1000 |

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

Send a card only to a client that advertises `cx_cap_card`, and send the plain NUI tooltip only to a client that does
not. CX does not hide a NUI tooltip when it draws a card, so a server that sends both gets both.

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
- Image keys work on Linux only, because only the Linux client reads the image name an element draws. On Windows
  key every card by `id`.
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
a value over a limit refuses the whole line.

A colour is `"#RRGGBB"` or `"#RRGGBBAA"`.

| card field | meaning |
|---|---|
| `width` | design px, 160-480, default 300 |
| `bg` | a colour, or `{"from": colour, "to": colour, "dir": "v" or "h"}` for a gradient (`v`: top to bottom, the default) |
| `border` | the 1 px frame, a colour |
| `tab` | a label on a tab above the card's top left corner |
| `tabcolor` | the tab's background colour |
| `item` | the item's hex id, for `{item.name}` and `{item.stack}` in this card; without it they use the item the element draws as an `@item:` image |
| `blocks` | the blocks, drawn top to bottom |
| `images` | up to 4 images placed against a corner: `src`, `w`, `h` (px, up to 256), `at` (`tl`, `tr`, `bl`, `br`, default `tr`), `dx`, `dy` (px right and down from the corner; a push out of the card makes the image overflow it) |

Every block has a `type`. Text blocks read `text` (markup, below), `size`, `color`, `dim` and `italic`; the fields a
type reads are listed here.

| type | draws | fields |
|---|---|---|
| `header` | a title line, size 1.3 | `text`, `color` |
| `subtitle` | a dim small line, size 0.85 | `text` |
| `status` | an icon beside wrapped text | `icon`, `text`, `px` (icon size, default 22), `frame` (a frame round the icon) |
| `headline` | a large line, size 1.6 | `text` |
| `text` | wrapped text | `text` |
| `flavour` | dim italic wrapped text, size 0.85 | `text` |
| `divider` | a thin rule across the full card | `color` |
| `icons` | a row of icons, wrapping, each in a frame | `icons` (up to 16 names), `px` (default 28), `frame` (default true) |
| `section` | blocks on a tinted band across the full card | `blocks`, `tint` |
| `loading` | text that pulses between 35% and full opacity every 1.2 s | `text`, `color`, `size` |
| `footer` | a band at the foot: `left` text and `right` text on one line | `left`, `right`, `tint`, `size` (default 0.85) |

- `size` is a multiple of the player's text size, snapped to 0.7, 0.85, 1, 1.15, 1.3, 1.6 or 2.
- `dim` lowers the colour's opacity to 60%. `subtitle` and `flavour` are dim by default, and `flavour` italic.
- A `section` holds blocks, and sections nest two deep.
- A block with an unknown `type` draws as `text`, so a client without `loading` still shows its text. With no `text` it draws nothing.

### Data that arrives late

A server a long way off sends its card lines after the window is open, so the pointer can rest on an element before the
element's card exists. Send one small stand-in per id family when the window opens, and the real cards when they are ready:

- `card.<prefix>*` shows for every element of the family until a better key matches. Give empty cells and slots `card.<id>=[]`, so
  the stand-in does not show for them.
- The card on screen is swapped in place when its key gains a better match, or the key's own line changes. CX redraws it with no hide, no
  new wait and no fade, and places it again if its size changed.
- `{item.name}` and `{item.stack}` fill a stand-in with real data and need no round trip, because the client already knows the item.
- A `loading` block marks what is still on its way.

### Text markup

| markup | effect |
|---|---|
| `<c=#RRGGBB>...</c>` | colour (`#RRGGBBAA` for opacity) |
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

An icon or image `src` is a texture resref (up to 16 characters) or `@item:<hex>`, which draws that item's inventory
icon in its own colours. A name that does not resolve draws nothing, never an error sprite. `@look:` and `@model:`
sources are reserved and draw nothing yet.

### Limits

| limit | value |
|---|---|
| cards per key | 4 |
| keys held | 256 |
| bytes per line | 12,000 |
| blocks per card (sections' blocks included) | 48 |
| section depth | 2 |
| characters per text | 400 |
| runs (styled pieces) per text | 48 |
| icons per `icons` block | 16 |
| images per card | 4 |
| icon name or `src` | 120 characters |

### Where and when it shows

CX puts the cards to the right of the element, 8 px off it, with their tops level. When they do not fit there it puts
them to the left, and it moves them as far as it takes to stay on screen. A card is drawn above NUI and takes no
input: the pointer and keys go to the window underneath.

A card shows once the pointer has rested on the element for `ui.tipdelay`, and fades in and out with the other CX
tooltips. Moving to another element that has a card skips the wait. It hides when the pointer leaves the element,
the window closes, or the list scrolls the element out of view.

`nui.cards` turns cards off. The capability is withdrawn with it. For people building cards, `nui.cardfile` reads a
file of policy lines when the game starts, as if a server had sent them, and `nui.cardforce` shows one key's cards
without a pointer.

## NUI window render keys

A server sets render properties per NUI window, keyed by the window id it gave `NuiCreate`. Send them only to a client that
advertised `cx_cap_nuirender`. A stock client has no such keys and draws the window unchanged, and so does a CX client with
Alloy off.

| key | meaning |
|---|---|
| `nui.<id>.opacity=0.6` | the whole window, 0-1 |
| `nui.<id>.background=0` | the title bar, frame and body fill, 0-1. At 0 only the widgets draw |
| `nui.<id>.fadein=150` | ms from invisible to the window's opacity, from the frame it opens |
| `nui.<id>.fadeout=150` | ms the window takes to fade away after it closes |

The keys are session state like every other `PolicySet` line. `nui.<id>.*=` releases a window's keys.

A fade out draws the window's last frame again, without the images in it: their textures are freed with the window. A
window's own popups (an open combo list) are not in that frame.

## The inventory window's keys

Knobs (all holdable): `inventory.enabled`, `inventory.look` (`native`/`cx`), `inventory.size`, `inventory.mode`, `inventory.kilograms` (a kilograms tooltip on the weight)
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

## Server side (sketch, not built)

The server end is one more `case` beside the server's hotkeys handler (in the reference server, its
`HotkeyService`): write `['P'][0x36][0x47]` + the ASCII lines with
`CreateWriteMessage` / `WriteCHAR`s / `SendServerToPlayerMessage`, as `LoginLoadBarService` builds its vanilla
packet, and read `0x36/0x48` in the existing `PlayerToServerMessage` subscriber.
