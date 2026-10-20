# The Aurora seam (CX-120)

Under Alloy (`alloy.enabled 1`) the game layer's `NewCAurObject` / `NewCAurScene` / `NewCAurCamera` /
`NewCAurString` return **CX** objects and the engine's `Scene`/`Gob`/`Part` never exist (ADR 0028). This
directory is the boundary: the shells the game holds, the vtables it calls through, and the engine functions
the seam answers instead of the engine.

Four streams build it. `SeamService` owns the boot switch and installs everything; the other three register
through the API below. **Nothing else installs a seam hook or writes a vtable slot.**

The seam is **all or nothing**: `SeamService` stands DB mode down, loudly, unless all four object/scene
factories are answered (`NewCAurObject`, `NewCAurString`, `NewCAurScene`, `NewCAurCamera`). The game hands an
object it got from one factory straight to the scene it got from another, so a half-installed seam means the
engine dereferences a CX shell as a `Gob` -- measured: `CGuiMan::Initialize` faults at boot.

## How a stream registers (the one API)

Add a class implementing `ISeamProvider`. It is discovered by reflection at boot, like `IExtenderService`, so
no edit to `SeamService` is needed:

```csharp
public sealed class SceneSlots : ISeamProvider
{
    public unsafe void Register(SeamRegistry r)
    {
        r.Slot(SeamTable.Scene, 4, (void*)(delegate* unmanaged<nint, void>)&OnRender);
        r.ReplaceEngine(SymbolMap.NewCAurScene, (void*)(delegate* unmanaged<...>)&OnNewScene);
        r.AfterTablesBuilt(static () => { /* cache a table address, resolve an engine pointer */ });
    }
}
```

- `Slot(table, linuxSlot, impl)` — `impl` is the address of an `[UnmanagedCallersOnly]` static whose parameter
  list is the slot's ABI **on this OS**. The index is always the **Linux** index: the canonical identity in
  `tools/seam/aurora-seam-vtables.tsv`. The builder places it at the Windows index there when it builds the
  Windows table, and drops it from a Windows table whose Windows home is a different vftable of the same class.
- `ReplaceEngine(symbol, handler)` — the seam answers that engine symbol for the session, in DB mode only.
  Installed in one funchook batch after every provider registered; no trampoline is handed back, because in DB
  mode the original would walk a scene graph that does not exist. Two streams claiming one symbol is a logged
  refusal, not a silent last-wins.
- `AfterTablesBuilt(action)` — runs once, after every table is built and before the hooks commit.

Registration happens on the boot thread, in type-name order, **before** any vtable is built and before any hook
is installed, so a `Register` may only record — never call the engine. Refusals (a double claim, a null
implementation, a slot that is not a slot of that table) are collected and logged by `SeamService` as
`seam: <owner>: <reason>`; a refused slot stays poison, which is loud by construction.

A stream that would rather expose slot-index arrays than implement `ISeamProvider` can: `Seam/SceneProvider.cs`
is the adapter that plugs seam-scene's `SceneSlots.Table` / `SceneOnlySlots.Table` / `CameraSlots.Table` /
`CameraSlots.PrimaryExtra` and its `SceneFns.Install(log, hooks)` into the registry. A zero entry in such an
array means "no CX body for this slot" and the builder fills it with that slot's poison entry.

### Which table to register against

`SeamTable` names the **concrete** class, not the abstract interface, because the pointer the game holds is the
concrete object's and the slot count it calls through is the concrete one. Counts are measured
(CX-183 section 1), Linux / Windows:

| `SeamTable` | slots | offset-to-top | who registers |
|---|---|---|---|
| `Gob` | 94 / 93 | 0 | **seam-obj** (0-79 the `CAurObject` interface, 80-93 `Gob`-only) |
| `Scene` | 88 / 87 | 0 | seam-scene (0-50 the interface, 51-87 `Scene`-only) |
| `Camera` | 41 / 33 | 0 | seam-scene (0-32 the interface, 33-40 `Camera`-only) |
| `CameraGob` | 94 / 93 | -8 | seam-scene, for Camera's nine overrides only; the rest is `Gob`'s |
| `StringGob` | 143 / 97 | 0 | tar/text (94-142); 0-93 is `Gob`'s |
| `CAurString` | 52 / 51 | -0x4c0 | tar/text |

A logical slot is **one implementation**: two vtable slots that reach the same implementation share one logical
id, so registering `(StringGob, 94)` also fills `(CAurString, 3)`, and every `CameraGob` slot Camera does not
override is already filled by its `Gob` registration. Registering an alias twice with the same pointer is
accepted; with different pointers it is refused.

`SeamVtables.g.cs` is generated from the TSV by `tools/seam/gen-vtables.py` — never edit it by hand, and
re-run the script if it conflicts in a merge.

## The by-value slots need a per-OS declaration

17 slots return `Vector`, `Quaternion` or `Plane` by value, and the two ABIs do not agree in a way `sret`
ordering can paper over: SysV classifies all three into **xmm0:xmm1 with no hidden buffer**, Win64 passes a
buffer in **rdx** with `this` in rcx and pushes every later integer argument one register right. So such a slot
needs two `[UnmanagedCallersOnly]` statics and an `OperatingSystem.IsWindows()` pick at registration —
`SeamVtables.ByValue(logical)` says which slots those are, and `managed/Alloy/Core/AurXform.cs` is the pattern
the repo already uses. A single shared declaration is wrong on Linux twice over.

## What the shell is

`Facade.Mint` returns a **layout-compatible shell**, not a bare vtable: the game writes the SafePointer
back-reference list inline at +0x08 and reads a `Gob`'s name / scene / root / position / orientation /
behaviour / external-data words without a vtable
(CX-182). The shell keeps exactly those words current; everything else
about the record lives in `SceneDb` columns. CX's own header (magic, kind, `DbId`, retire state) sits in the
words **before** the pointer the game holds, so it cannot collide with any engine offset.

Rules for a slot implementation:

- Resolve with `Facade.Object(self, SeamSlot.<name>)` (or `Facade.Scene/Camera/StringRecord`). It normalises any
  interface pointer to its shell through the table's offset-to-top word, validates the handle, and on a retired
  or unknown pointer logs the slot's own name once and returns a null `DbId` — the poison path. Return zero then.
- A setter writes its column, keeps the mirrored shell word current (`Facade.SyncXform` does both for
  position/orientation) and marks the record dirty with one `Dirty` bit.
- **Allocate nothing.** Game code calls these per frame on the game thread; a NativeAOT GC pause lands on
  whichever thread the slot fired on.
