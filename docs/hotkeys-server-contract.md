# Hotkeys: client <-> server contract

The "Hotkeys" feature is split between the client and a server that opts in. This client extender does
the parts only a client can; **the arming UI is a server-authored NUI** (the server owns the toolbar
layout). Shadows over Westgate's server is the reference implementation. This file is the contract
the server side must implement to match the client.

## Split of responsibilities

Client extender (this repo, done):
- The settings panel's Hotkeys rows (`/settings` → Hotkeys): "Bind keys on this server…" sends OpenUi.
- Runtime capture: a pressed **armed** key -> a packet to the server, gated on
  window focus + "not typing in chat", debounced 250 ms.
- Handshake: advertises extender **presence only** (`cx_enabled`) on login. It does
  **not** advertise the armed set -- consent is per-server (see below), so telling every
  server the keys to harvest was a leak. The server re-pushes its set via ArmSet.
- Persists consent **per server**, scoped by a client-minted token
  (`cx-hotkeys.cfg` in the client's NWN user directory, one `[token=<hex>]` section per server).

Server (reference implementation done):
- Builds the arming **NUI** (it knows the toolbar/ability layout to show).
- Owns the key -> toolbar-action **mapping** (the client never sees it).
- Persists the armed set **and the consent token** per character (server vault).
- Presents the token on login (PresentToken) and pushes the armed set to the client.

## Per-server consent (the token model)

Consent is a **capability**, not a global switch. It is scoped to the server that
holds a secret the client gave it, so one server can never inherit another's consent.

- On the **first** consent for a server (the player OKs the client-owned modal), the
  client mints a random 128-bit token, records `{token -> keys}` locally, and hands the
  token + consented keys to the server (**GrantToken**). The server stores the token per
  character, alongside the bindings.
- On **every later login**, the server presents that token back (**PresentToken**)
  **before** ArmSet. The client looks it up: recognised -> restore that server's
  consented keys silently; unrecognised/absent -> the armed set stays **empty**.
- A malicious server that was never granted a token can present none, so the client's
  armed set stays empty and every key its ArmSet asks for becomes a fresh consent
  prompt. Replaying another server's key list gains it nothing -- it lacks that
  server's token, so nothing is silently restored. This is why a per-session ArmSet
  alone was insufficient and the token is required.

**Ordering matters:** send PresentToken *before* ArmSet on login. Otherwise the client
plans the ArmSet against an empty armed set and re-prompts the player every login.

## The messages

Opcodes are **final** in `managed/Services/HotkeyProtocol.cs` (device major `0x36`,
minors `0x40..0x46`). All custom payloads are **ASCII text** (no endianness).

### 1. Handshake (client -> server) -- device advertisement

On login accepted, the client advertises via the stock device channel
(`SendPlayerToServerDeviceAdvertiseProperty`, major `0x36` / minor `1`). The server
stores it as `player.propertyMap[lowercased name] = int` (on `CNWSPlayer`, offset `0xe0`):

- `cx_enabled = 1`      -- the extender is present; only now offer the feature.

- `cx_version = <n>`     -- the release as `major * 1000000 + minor * 1000 + patch` (`0.7.0` is `7000`; `0` when the build
  carries no version).
- `cx_cap_<name> = <v>`  -- one property per capability the client can draw right now, valued by that capability's
  version (`>= 1`). The vocabulary is in `docs/ui-policy-contract.md`.

Presence, the version and the capabilities are all the client advertises -- never the armed key list (that was the old
global-consent leak). The server owns the bindings and re-pushes them via ArmSet.

### 2. OpenUi (client -> server) -- "player clicked Hotkeys"

- major/minor: `OpenUiMajor/Minor`, **no payload**.
- Sent when the player presses "Bind keys on this server…" in the settings panel (or `/hotkeys bind`).
- Server response: `NuiCreate` the arming window for that player.

### 3. KeyPress (client -> server) -- an armed key was pressed

- major/minor: `KeyPressMajor/Minor`, payload = **ASCII decimal SDLK**, e.g. `"102"` (f).
- The client only sends keys in the armed set, at most once per 250 ms per key,
  never while unfocused or typing. Server maps the keycode to the toolbar action.

### 4. ArmSet (server -> client) -- here is the armed set

- major/minor: `ArmSetMajor/Minor`, payload = **ASCII CSV of SDLK codes**, e.g.
  `"102,1073741882"` (f, F1) (empty payload = arm nothing).
- Sent whenever the player changes their selection in the NUI (and optionally on
  enter, to restore the vault-persisted set). The client computes the delta:
  **removals** (armed now, not in the new set) apply silently; **additions** raise
  the client-owned consent modal and only arm on the player's OK.
- Only send this to clients that advertised `cx_enabled = 1`.

### 5. Revoke (client -> server) -- forget these keys

- major/minor: `RevokeMajor/Minor` (`0x36 / 0x43`), payload = **ASCII CSV of SDLK
  codes**, e.g. `"102,1073741882"`.
- Sent when the player **denies** the consent modal (so the server does not keep the
  binding and re-push/re-prompt it every login) or unbinds from a client-side review.
- Server response: drop those keys from the character's map (unbinding whatever
  toolbar/metamagic slot they pointed at) and re-push the shrunk ArmSet. A revoke for
  an unbound key is a no-op.

### 6. PresentToken (server -> client) -- proof of prior consent

- major/minor: `0x36 / 0x44`, payload = **the token hex** the client granted last time
  (e.g. `"a1b2...d6"`). Empty payload = "I hold no token".
- Sent on login **before** ArmSet, only to clients that advertised `cx_enabled = 1`.
- Client response: recognised token -> restore that server's consented keys (no prompt);
  unrecognised/empty -> armed set stays empty, awaiting fresh consent.

### 7. GrantToken (client -> server) -- consent receipt

- major/minor: `0x36 / 0x45`, payload = **`"<tokenhex>;<CSV of consented keys>"`**.
- Sent right after the player OKs the consent modal. The server reads the leading hex
  run (up to the `;`) and stores it per character as that character's consent token, to
  present back on the next login. Storing it is idempotent (re-sending the same token is
  a no-op).

### 8. AbuseContact (client -> server) -- who to email about extender abuse

- major/minor: `0x36 / 0x46`, payload = **the contact email as ASCII** at offset 3
  (`mpiasecki720@protonmail.com`).
- Sent once on login, right after `cx_enabled`. Servers that do not know the minor
  ignore it; it is informational and never affects consent or arming.

## Keycodes (SDLK, not Windows VK)

The codes are **SDL keycodes (SDLK)** -- what `HandleWMKeyDownMessage` actually
delivers. They are NOT Windows VK codes (an earlier draft said "VK"; that was
wrong). Reference values:
`a..z = 97..122` (e.g. `f = 102`), `0..9 = 48..57`,
`F1 = 1073741882 .. F12 = 1073741893` (F1 + n-1), `Space = 32`, `Return = 13`.
Digits coincide with VK; letters (lowercase) and F-keys (SDL's `0x40000000`
range) do not -- so a "VK" value like `70` for F will silently never match.
The number is opaque to the server: display key names, but store/echo whatever
SDLK the client sent. Full list: SDL's `SDL_keycode.h` (`SDLK_*`).

### Modifiers (Ctrl/Shift/Alt)

A combo is the base SDLK with a modifier high bit OR'd in: `ModCtrl = 1 << 25`,
`ModShift = 1 << 26`, `ModAlt = 1 << 27` (bits 25-27 are free of every base -- ASCII
keys use bits 0-6, scancode keys add bit 30, mouse uses bit 28). So `Ctrl+F` is
`ModCtrl | 102`. Matching is **exact** -- `Ctrl+F` and `F` are different codes and never
fire each other. The code is opaque on the wire as always: the client reads live modifier
state (`SDL_GetModState`) at capture and encodes the same bits, then matches against the
armed set; the server offers three picker checkboxes and OR's the chosen bits into the
keycode at bind time. Alt uses **left-Alt only** (`KMOD_LALT`); right-Alt is AltGr, which
is needed for accented typing and which Windows synthesizes as Ctrl+Alt. Both repos'
`HotkeyProtocol` must agree on the three constants.

### Confirmed failure mode (runtime-verified)

If the server's key table uses VK codes, the client receives them, arms them
verbatim, and **nothing matches except digits**. Observed live: server pushed
`{F=70, G=71, F1=112, F2=113, 2=50}`; the client delivered `{102,103,
1073741882,1073741883,50}` on those presses, so only `50` (`2`) fired. Fix the
server's key list to SDLK.

Conversions for the common keys a toolbar UI offers:

| Keys        | VK (wrong)   | SDLK (correct)                | Formula                 |
|-------------|--------------|-------------------------------|-------------------------|
| `A..Z`      | `65..90`     | `97..122`  (lowercase ASCII)  | `sdlk = vk + 32`        |
| `0..9`      | `48..57`     | `48..57`   (identical)        | `sdlk = vk`             |
| `F1..F12`   | `112..123`   | `1073741882..1073741893`      | `sdlk = 1073741882+(vk-112)` |
| Space       | `32`         | `32`                          | same                    |
| Everything else: look it up in `SDL_keycode.h` -- do not assume it maps.     |||

Better than converting: define the server's key list in SDLK from the start
(the value is opaque anyway) and drop VK entirely.

## Flow

```
login   -> client advertises cx_enabled (presence only)      (msg 1)
        -> client AbuseContact "<email>"                         (msg 8)
        <- server PresentToken (its stored token, or empty)     (msg 6)  [before ArmSet]
        <- server ArmSet (the character's bound keys)           (msg 4)
           known token -> keys restored, no prompt
           new/unknown -> additions raise the consent modal
OK      -> client mints token, records {token->keys},
           GrantToken "<hex>;<csv>" -------------------------->  server stores token   (msg 7)
click   -> client OpenUi ------------------------------------->  server NuiCreate
NUI     <- player arms keys -> server persists (vault) ------->  server ArmSet          (msg 4)
press   -> client KeyPress (armed, gated, debounced) -------->  server maps -> action   (msg 3)
deny    -> client Revoke ------------------------------------->  server forgets binding (msg 5)
```

A different or malicious server holds no token for this player: it presents none, the
client's armed set stays empty, and its ArmSet is all-additions -> a consent prompt. It
cannot inherit the consent the player gave another server.
