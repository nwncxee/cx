# Area settings: an area holds CX settings while the player is in it

**Status:** built on the client (`managed/Framework/AreaSettings.cs`, `managed/Services/AreaSettings/`), with a Moonglow
Toolset plugin in `tools/moonglow/nwncx/`. Volumetric fog (`vfog`) is the first group that takes it; any group a
server may hold (`docs/ui-policy-contract.md`) takes it the same way.

## The variables

An area's settings are its own **local variables**, named `cx.` and a knob key:

| variable | type | value |
| --- | --- | --- |
| `cx.vfog.enabled` | int | `1` volumetric fog in this area, `0` the classic fog |
| `cx.vfog.haze` | float | `0.3` |
| `cx.vfog.top` | float | `40` |

A switch is an int (0 or 1), a dial is a float (an int works too), a choice is a string. `/knobs` in the client lists
every key with its range. The prefix is read in any case, so `CX.vfog.haze` works too.

The game keeps an area's variables in its `.git`, in the root `VarTable` list, so any toolset can write them:
Aurora's and Moonglow's Area Properties both have Advanced > Variables. The Moonglow plugin below gives them a form.

## What the player sees

When the area loads, CX holds every key the area names at its value. The rule for the rest of the group:

- Name any key of a group and the area holds the whole group. The master switch is held on unless the area names it,
  and every other dial is held at its default. The player cannot change any of them, or switch back to the classic
  fog, while they are in the area. The panel shows the rows greyed, and `/policy` lists them.
- The group's quality preset (`vfog.quality`) is not held unless the area names it, because it tracks the player's
  hardware rather than the area's look.
- The player's own values stay underneath and come back at the next area that names nothing, or at the next login.
  Leaving to the main menu keeps the last area's holds until then. `cx.cfg` never sees the area's values.
- CX reads the variables as the `.git` holds them, when the area loads. A script that changes them later
  (`SetLocalFloat` on the area) does not reach the client; a server relays such a change as `PolicySet` lines.
- A variable whose key is not a holdable knob, or whose value the knob does not take, is refused and listed under
  `refused:` in `/policy`. The area's other keys still hold.

`cx.log` writes one line per area: `AreaSettings: '<resref>' holds n/m knob(s): ...`.

## Local games and servers

CX reads the area's `.git` through the game's resources, so it finds one only when the game holds the module itself,
in a local game (single player, or a game this client hosts). A multiplayer client never has the module.

On a server, the server sends the same holds. When a player enters an area, the server reads the area's `cx.*`
variables (`GetLocalFloat`, `GetLocalInt` and `GetLocalString`, or the VarTable from NWNX or Anvil) and sends each one as a
`PolicySet` line with the prefix dropped (`vfog.haze=0.3`). It should first release the previous area's group with `vfog.*=`. The wire
and limits are in `docs/ui-policy-contract.md`. A server sending lines itself should follow the whole-group rule above: send
every key of the group.

## The Moonglow plugin

`tools/moonglow/nwncx/` is a Moonglow Toolset plugin (plugin API 0.3, Luau). To install it, copy the folder into
Moonglow's plugins folder (Linux `~/.local/share/moonglow/plugins`, Windows `%APPDATA%\Moonglow\plugins`), or zip it
with `mg plugin pack` and use Plugins > Install Plugin from File. Then enable it.

- **Plugins > NWNCX:EE Area Settings...** asks for an area and a feature, then shows the feature's settings with the
  area's current values. Ticking *This area sets ...* writes every key of the feature into the area's VarTable.
  Unticking it removes them. Either way the edit is one step of Undo.
- **Build > Verify Module** gets a check, *NWNCX:EE area variables name real settings*, which flags a `cx.*` variable
  naming no setting, or with a value of the wrong type or out of range.

`settings.luau` lists what the plugin offers. To add a feature, add an entry that mirrors its knob group's
declaration: key, kind, range and default.
