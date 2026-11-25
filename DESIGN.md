# Design

The decisions behind each rule are the records in `docs/adr/`.

## Optional, client-only

**NWNCX:EE** (**CX** for short): optional, drop-in **client** extender for NWN:EE. Feature logic in C# (NativeAOT);
a thin native bootstrap gets it loaded. Optional by design: run the stock client
and you play vanilla. New client->server packets the server doesn't recognize are
ignored, so an un-extended client loses nothing.

This is the **client** (`nwmain`), not the server. The server end of any packet lives in the
server's own extender (NWNX/Anvil); do not conflate the two.

## Tuning knobs (ADR 0025, amending 0013)

There are no `CX_*` environment variables. A knob is **one declaration** (`Knob<T>` in a
`KnobGroup`: key, label, kind, range, default, tier, effect, help); the settings panel row,
the `cx.cfg` key and the `/<group> <knob> <value>` chat command are three views of it.
Nothing else creates a knob. The panel (`/settings`, or the gear at the main menu) is the
primary surface; chat commands are gated by `general.commands` (off by default, `/settings`
exempt). `cx.cfg` is the one file, in the client's user directory beside `nwn.ini`; `/knobs`
prints every declaration, and General → About's *Copy my settings for a bug report* puts the
same text on the clipboard. Spec: `docs/spec/unified-settings.md`.

The one exemption is `CX_MANAGED`, which the native bootstrap reads before any
managed code exists.
