# Architecture


Load path (the "hard work", you never touch it):

    LD_PRELOAD libcx.so  (native/bootstrap.c: constructor fires on load)
      -> dlopen cx_managed.so  (C# NativeAOT shared lib)
      -> CxInit()  ->  ServiceHost.Run()
                           - discover every IExtenderService (reflection, sorted by InitOrder then name; the seam is first)
                           - construct each + Initialize(log, hooks)
                           - HookService.Commit() installs all hooks at once

Read before working in the area: `docs/hook-pattern.md` (a real hook + trampoline),
`docs/owned-resource-lifetimes.md` (what creates and kills each owned table, per scene layer),
`docs/alloy-refusals.md` (why a frame is the engine's), `docs/audio.md` (Steam Audio behind the engine's OpenAL).
Every file under `managed/Alloy/Shaders/` is GLSL under a `cx_*` resref <= 16 chars -- the hak limit, so each
is overridable as `<resref>.shd` -- translated and compiled to SPIR-V at runtime (`VkGlsl`, shaderc). FSR 3.1's and FidelityFX DoF's shaders
are the exception: AMD's GLSL, compiled when `build.sh` runs (`tools/ffx/`) and bound by AMD's backend, so
`check-shaders.py`, the SSBO and UBO slot gates and the `cx_*.shd` contract never see them; they change through
`native/ffx/sdk-1.1.4.patch`.

## Adding a service


Drop a class implementing `IExtenderService` in `managed/Services/`; it is discovered and wired
automatically, no registration. Hook handlers are `[UnmanagedCallersOnly]` statics; `docs/hook-pattern.md`
has the hook + trampoline mechanics.

Trampolines must be static; mutable state must not be loose. New state goes in the one **lifetime owner**
for its lifetime -- a `static` field holding a `...State` class -- so a reset (context loss, stand-down,
area change) replaces the object instead of remembering forty fields. `static readonly`, `const` and the
owner field itself are free; one more loose `static` is refused by `tools/gates/statics.sh`.

## Engine memory reads


Read engine fields through the typed views in `CX.Engine`
(`new CNWCCreature(obj).Stats.FirstName`), not `*(T*)(ptr + EngineLayout.X)`.
A view member exists once its row is typed: `Field<T>` in `EngineLayout*.cs`, where `T` is
the primitive, the target's view for a pointer to a class, or a value type (`Quat`);
`count: n` makes it an embedded array (`Span<T>`), `embedded: true` an inline record (its address), and a
`stride` row gives the class `this[i]` over records laid end to end. A class the engine frees under us is
listed `TRANSIENT` in the generator and its view is a `ref struct` (#193). Concept members
go in a hand-written `partial` of the view.

Call engine functions the same way: the signature is declared once, `[Fn("R(T name, ...)")]` on
the `SymbolMap` symbol or `sig:` on a `vtbl.*` row, and the generator emits `EngineFn.X`, the
`XFn` pointer type, `hooks.InstallX(&OnX)` / `hooks.ChainX(...)`, and `view.X(...)` for a
`View this` first parameter; `sret(View this, ...)` (or `sig: "sret(...)"`) orders the hidden buffer per OS. No
hand-written `delegate* unmanaged` outside `Graphics/` unless its parameter list differs per OS, it is a
callback with no symbol, or it needs `[SuppressGCTransition]`. `build.sh` refuses a bare hex const anywhere
in `managed/` that does not say `not-an-offset:` on its line.

`build.sh` regenerates `Framework/Engine/Views.g.cs` (`tools/engine/views.py`); never edit it
by hand, and settle a merge conflict in it by rerunning the script. `tools/gates/engine-views.sh`
refuses `ptr +/- EngineLayout.X` outside `Framework/` once row X has a view member, so the
rule is checked rather than remembered. **Touch a file, port its reads** -- no backlog beyond that (CX-30). Views inline to the raw load, so hot loops use them
too; resolve an `EngineFn` pointer once into a static field there.

## The binary is your map


The client binary (`python3 tools/paths.py NWN_BIN`) is an
**unstripped** ELF (~24k dynamically-exported functions). You hook by name, no
offset hunting.

The engine renders via OpenGL/GLX/X11 (SDL is static -- symbols appear, no `libSDL` in ldd), and goes on
running that code with Alloy on: `NullGl` makes SDL open a Vulkan window and routes every GL entry nwmain
imports to a CX stub, so the session has no GL context and Vulkan presents (`VkBackend.Present`). Alloy is
Vulkan's alone -- a refused probe (`VkBackend.Refusal`) stands it down and the engine's own GL renderer draws
(ADR 0033).

## The server side


CX is client-only. A server that wants a CX packet implements its receive end in its own extender
(NWNX/Anvil); a server that does not simply ignores it. The engine classes (`CNWSMessage` and friends) are
the *same classes* on client and server, so server-side struct/field knowledge transfers; the hook mechanism
does not (the client uses funchook). Shadows over Westgate's server is the reference implementation of the
hotkey, UI-policy and nameplate packets (`docs/hotkeys-server-contract.md`, `docs/ui-policy-contract.md`).

## Status


A live hook proves out via `cx-launch`: watch for "first frame presented -- hook is LIVE". There is no
whole-extender opt-out (ADR 0025): every feature has a master toggle, and "no CX" is an uninstall.
macOS (`DYLD_INSERT_LIBRARIES`) is blocked by the notarized app's hardened runtime until re-signed (do last).
