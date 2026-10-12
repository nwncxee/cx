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
| `cx_cap_heldimage` | `1`: the `held.image` policy key shows an image under the pointer (`nui.enabled`, `nui.heldimage`) |

A property is sent only while the capability is live: its hooks resolved and its knob (`nui.enabled`,
`nui.itemicons`) is on. **Absent or `0` means the client does not draw it**; send the vanilla version (a plain
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
