# The live harness

`tools/live/run-live.sh` boots the real client with this worktree's CX, lands it in a playable area
with a character, soaks for N seconds, captures the logs and screenshots, shuts it down and prints
PASS or FAIL. It is for verifying that a change *works in the game* -- the thing `run-demo.sh`, which
runs the framework in a stub host with no GL and no engine, cannot tell you.

    tools/live/run-live.sh                             # defaults: 20 s in The Dark Ranger's Treasure
    tools/live/run-live.sh --seconds 60 --out /tmp/x
    tools/live/run-live.sh --module Kingmaker --cfg 'alloy.enabled=0'
    tools/live/run-live.sh --keys 'key:Return;type:/alloy snap;key:Return' --cfg 'general.commands=1'
    tools/live/run-live.sh --ladder                    # the scripted ladder below (70 s)

Options: `--module NAME` `--seconds N` `--cfg 'key=value'` (repeatable) `--out DIR` `--keys SCRIPT`
`--char NAME` `--width W` `--height H` `--boot-timeout N` `--min-lit N` `--keep` `--ladder`.
Exit code 0 is PASS, 1 is FAIL, 2 is a setup problem (no build staged, no client, a client already
running). It works from any worktree: it runs the `cx-linux-x64/` staged next to it, so build first.

## How it works

**Nothing reaches the desktop.** The client runs inside `gamescope --backend headless`, which starts
its own nested Xwayland on a free display number and composites to nowhere. The GPU is the real one:
`glxinfo` inside it reports `OpenGL core profile version string: 4.6.0 NVIDIA`, which is what Alloy
needs. gamescope chooses the display number itself, so the harness reads it back out of gamescope's
log (`Starting Xwayland on :N`) -- there is no flag to pin it.

**Nothing of the user's is touched.** `$OUT/home` is a throwaway HOME and
`$OUT/home/.local/share/Neverwinter Nights` a throwaway user directory, passed to the client as
`-userdirectory`. `HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME` and `XDG_CONFIG_HOME` all point inside it,
which matters because CX resolves `cx.cfg` two different ways: boot settings through
`Native.DefaultUserDirectory` (the XDG path, before the engine exists) and everything after through
the engine's own resolved user directory. Pointing both at the same throwaway directory is what makes
the two agree -- see the `run-demo-reads-real-cfg` lesson, where only one of them was isolated and the
headless run silently bound the user's real knob values. `cx.log` follows `$HOME`. The copied files are
`cdkey.ini`, `nwnplayer.ini`, `settings.tml` and `nwn.ini`; `nwn.ini` is nothing but absolute `Alias`
paths, so every one of them is rewritten to the throwaway directory, which is what keeps the engine's
`saves/`, `cache/`, `logs/` and `crashreport/` inside `$OUT`.

**It gets into the game with no clicking.** `+TestNewModule "<module>"` makes the client host the
module in-process (it logs `Server mode: Module Loaded`), join itself as Player 1, and take the
character sitting in `LOCALVAULT` -- so the harness drops exactly one stock pregenerated `.bic`
(`data/lcv/fighter.bic` by default, `--char` picks another) into the throwaway local vault and the
character select has nothing to ask about. A second character, or a broken one, puts a panel in front
of the harness: a `.bic` the engine rejects shows up as
`QuarantineInvalidCharacter: *** FAULT ***: Broken GFF` and the run then sits at the main menu
forever. The working directory must be `bin/linux-x86` or the client prints "Could not divine nwn base
data directory" and exits; `cx-launch` is used in its Steam shape (the command is its argument)
because its manual mode only forwards leading-dash options and `+TestNewModule` is not one.

**Shutdown.** In game the engine ignores `SDL_QUIT`, both from the window-manager close box and from
SDL's own `SIGTERM` handler, so the clean exit goes through the UI with synthetic XTEST input:
Escape, the entry below the Options panel's centre, the "Quit Neverwinter Nights?" prompt's Yes, then
the main menu's Exit and its prompt. Which of those steps does the work varies between runs, so the
sequence walks the whole path and the clicks that land on nothing cost a few idle seconds. If the UI
path fails the harness escalates `WM_DELETE_WINDOW` -> `SIGTERM` -> `SIGKILL`; the summary names
whichever ended the process. The engine only catches SIGSEGV, SIGABRT and SIGFPE
(`CrashReporter_Linux`), so a SIGKILL cannot be confused with a crash -- and crashes are counted
*before* shutdown starts, for the same reason.

## What a PASS means

Every clause is graded and named in the FAIL list, so a failure says which one:

| clause | evidence |
| --- | --- |
| frames presented | `UiHostService: first frame presented -- hook is LIVE` in `cx.log` |
| module loaded | `LoadModule): Loading Module:` in the engine's stdout |
| reached an area | the client's `SendPlayerToServerArea_AreaLoaded` actually leaving: the shadow bake's `hold released` / `passed through`, or the area edge when nothing holds the send (`holding the AreaLoaded send` is the START of the loading wait, not its end) |
| no hang | `cx.log` kept growing -- 60 s of silence from a live process is the freeze signature |
| no crash | no `*.nwcrash.txt` in the throwaway `crashreport/` before shutdown |
| screenshots | at least one PNG captured off the client's own window |
| self-checks | no `SelfCheck ... FAIL` and no `init threw` in `cx.log` |

`$OUT` keeps `summary.txt`, `cx.log`, `engine-stdout.log`, `nwengineLog.txt`, `nwclientLog1.txt`,
`gamescope.log`, `xtool.log`, any crash report, and `shots/`. The summary also carries boot-to-area
time, the last reported Alloy part count, the client's peak RSS (`VmHWM`) and how it was shut down.

Two screenshots are taken, halfway through the soak and at its end. Judge the frame on
`02-settled.png`: `AreaLoaded` fires before the module's loading-screen overlay has faded and before
texture streaming has caught up, so `01-early.png` often still has the overlay on it. A longer
`--seconds` buys a cleaner pair.

## The ladder (`--ladder`)

Standing in one area proves the client boots. It does not prove the game *runs*: CX-120's higher
rungs need the PC to move, a conversation to open, combat to resolve and an area transition to load.
None of that is clickable from a headless harness, so the ladder makes the game do it to itself.

    tools/live/run-live.sh --ladder                     # == --module cxladder, 70 s soak

`--ladder` builds a module whose own OnClientEnter script schedules the whole session off
`DelayCommand` and marks each rung in the engine's log. The rungs, with their timings (the named
constants at the top of `tools/live/ladder/cxladder.nss` -- change them there, nowhere else):

| t | rung | marker |
| --- | --- | --- |
| 0 s | the PC joins | `CXLADDER: enter <name> in <area>` |
| 4 s | `ActionMoveToLocation` ~5.6 m across the inn floor | `CXLADDER: walk`, then `walked N.Nm` |
| 9 s | a `nw_innkeeper` is created in front of the PC and opens the module's own 2-node `.dlg` | `CXLADDER: talk` |
| 16 s | the talker is destroyed (which drops the dialogue UI), three hostile `nw_goblina` spawn, `ActionAttack` | `CXLADDER: combat`, then `combat-result N/3 down` |
| 30 s | `JumpToLocation` to the second area's stock respawn waypoint -- a real transition with a loading screen | `CXLADDER: area2 <area>` |
| 43 s | a plain NUI window opens at (400, 260), 300x200, for the window-layer shots | `CXLADDER: nui` |
| 45 s | `JumpToLocation` back to where the PC joined -- the second transition | `CXLADDER: area1` |
| 48 s | the ladder is finished | `CXLADDER: done in <area>` |

Each of `enter walk talk combat area2 area1 done` is a graded clause: a missing one is a FAIL line
naming the rung, and a rung that detects its own failure (a blueprint or waypoint that is not there)
writes `CXLADDER: <rung>-FAILED: ...` and fails the run with its reason. The two audit lines are
graded too -- a `walked` under 1 m and a `combat-result 0/3` both fail, because a rung that ran is not
a rung that did anything. `$OUT/ladder-markers.txt` holds every marker the run saw and the summary
repeats them.

**Where the markers land.** `WriteTimestampedLogEntry` is a server-side call, but `+TestNewModule`
hosts the module inside the client's own process -- so there is one log and it is the client's:
`<userdir>/logs/nwclientLog1.txt`, timestamped by the engine, copied into `$OUT`. (The client formats
`LOGS:%sLog%01d` with both `nwclient` and `nwserver`, so the harness greps every log in the throwaway
`logs/` plus the engine's stdout; a build that routed the server half to `nwserverLog1.txt` would
still grade.)

**The PC is made immortal, and the character is not the one in the vault.** The first passing run
ended on the "You Are Dead" panel: the goblins got through the PC while it was back in the inn, so the
settled screenshot was a death prompt instead of a frame. The in-game character is *not* the
`fighter.bic` the harness drops into the local vault -- `+TestNewModule` joins with one of its own
("Aluvian Darkstar"), and it is nowhere near the vault character's 15 levels. `cxladder.nss` calls
`SetImmortal` on it at arm time: the fight still happens and the hits still land, the test character
just cannot lose. If it dies anyway the ladder says so (`done-FAILED: the PC died`) and the run fails.

**The ladder arms on the area, not on the login.** `OnClientEnter` fires while the PC is still between
areas: `GetArea(oPC)` is `OBJECT_INVALID` there, so a location built from it is nowhere, and the
engine runs no delayed command during the load and then fires every expired one at once. The first
version of this ladder scheduled straight off `OnClientEnter` and logged a `walked 0.0m`, a talker
that would not spawn, and three rungs in the same second. `cxladder.nss` now polls every half second
until the PC has an area and starts its clock there. That is also why `walked N.Nm` is graded and not
just `walk`: a rung that ran is not a rung that did anything.

The ladder also drives the pointer: `xtool.py keys` grew a `move:x,y` step (motion with no button, so
the engine runs its cursor hit-check -- the hover half of a pick) and a `shot:<path>` step, which is
what times a capture against a rung instead of against the soak's halves. The run keeps
`03-walk` `04-talk` `05-talk2` `06-combat` `07-area2` `08-area1` alongside the usual
`01-early`/`02-settled`.

**Beamdog's content is not in this repo and must not be.** The module is *derived at run time* from
the installed game: `tools/live/ladder/build-ladder.sh` unpacks the stock
`data/mod/The Dark Ranger's Treasure.mod` with `nwn_erf`, compiles `cxladder.nss` against the game's
own `nwscript.nss`, converts `cxladder.dlg.json` with `nwn_gff`, repoints `module.ifo`'s
`Mod_OnClientEntr`, and repacks the lot as `cxladder.mod` into the *throwaway* user directory's
`modules/`. Committed are only the `.nss`, the `.dlg.json` and the script. The stock module supplies
the two areas (the Inn of the Lance interior the harness already uses, and The Dark Ranger's Ruins
for the transition) and the walkable ground the coordinates are taken from; the creatures
(`nw_innkeeper`, `nw_goblina`) are standard blueprints out of the base game data. It needs
`nwn_erf`, `nwn_gff` and `nwn_script_comp` (neverwinter.nim) on `PATH`.

`--seconds` below 60 is raised to 60, because the ladder's last rung is at t+48 and a shorter soak
would grade rungs the run never reached.

## Window layers

`--cfg 'ui.layerprobe=1'` with `--ladder` draws a red rectangle on the below-NUI layer and a cyan one on the above-NUI
layer, placed over the rung's NUI window. `07n-nui*.png` shows the order: the red one hides under the NUI window and the
cyan one sits over it. Run it with and without `--cfg 'alloy.enabled=0'`; both must look the same.

## Limits

- **One client at a time.** The harness refuses to start if `nwmain-linux` is already running. Two
  clients on one GPU make every timing number meaningless, and the box is shared with build agents.
- **Everything it grades on is a CX log line**, so the harness writes `general.logs=1` into the
  throwaway `cx.cfg`. A change that breaks CX logging reads as a FAIL regardless of what it drew.
- **It cannot tell you the picture is right.** It proves the client reached an area, kept presenting
  and did not fall over. Whether the frame looks correct is a human (or an image-diff) judgement on
  the screenshots; `/alloy snap` via `--keys` gets you the matched HDR/present set that `tools/snap/`
  reads if you want to bisect by pass.
- **The screenshots are the client's X window, not Alloy's own readback.** They are what the
  compositor would show, which is what you want for "does it look right", but they carry the UI too.
- **No frame-rate number.** `presents logged` is whatever CX's own reporting period last printed, not
  a measured FPS. Use `/alloy bench` for that.
- **The UI quit path is pixel coordinates** at the harness's fixed window size, against a UI whose
  scale comes from the copied `settings.tml`. A user whose UI scale is unusual gets the SIGKILL path
  instead; nothing else changes.
- **Module choice is not free.** A module that needs haks, NWSync or a server (a persistent world's own
  module) will not come up this way. Stock modules under `data/mod/` do.
- **It is a single-player local-server session.** Anything that only happens against a real server --
  packet handling, server-driven NUI, area transitions on a hop -- is not covered. The ladder's two
  transitions are local `JumpToLocation`s between areas of one hosted module, not a server hop.
- **The ladder's shot times are open-loop.** They are sleeps measured from "reached an area", which is
  a second or two after the PC's login that the module's own clock starts from, so a capture lands
  inside its rung's window rather than on a known frame. The markers are the evidence; the pictures
  are the sanity check.

## Held image

`--cfg 'nui.heldprobe=iit_belt'` with `--ladder` sends the client a `held.image` line at the first NUI draw 50 s after
boot, which is the rung's NUI window. The ladder moves the pointer over that window first, so `07n-nui*.png` shows the
image under it. The hold ends when the window closes. Run it with and without `--cfg 'alloy.enabled=0'`.
