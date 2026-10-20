# Engine ABI: native handler signatures and vtable slots across platforms

`docs/windows-symbols.md` says *where is this function*; `docs/engine-layout.md` says
*where is this field*. This file answers the third question the Windows port turned
per-platform: **what shape does the call have** — the argument and return convention of
every `[UnmanagedCallersOnly]` handler the extender hands the engine, and the vtable
slot of every virtual it calls through.

It matters for the same reason a moved field does, only louder: a signature that is right
on the Itanium ABI and wrong on Win64 corrupts the stack on the first call, and MSVC does
not order a vtable the way GCC does. #420 paid for these lessons on the 2D side — sret
**after** `this`, a `Vector` passed **by pointer**, a swallowed callee that **destroys**
the by-value `CExoString` it was handed, `GetPortrait` one slot earlier than Linux.

## The cheap oracle: MSVC mangling carries what Itanium drops

Itanium mangles parameters but **not the return type** of an ordinary function. MSVC
mangles both. So for any function name-exported by `nwmain.exe`, one `llvm-undname` gives
the complete C++ signature — arguments *and* return — for free, no decompiler:

    objdump -p nwmain.exe | grep -E '^\s+\[\s*[0-9]+\] \+base\[' | awk '{print $NF}' \
      | grep -F '?glImageBasic@CAurTextureBasic@@' | xargs llvm-undname
    public: int __cdecl CAurTextureBasic::glImageBasic(bool)

That is how this audit ran, and the whole Alloy hook surface is name-exported (#430), so
it covers every Alloy handler without a single disassembly. Two consequences worth
naming: the Windows binary is the best available **return-type reference for the Linux
build too** (same source, same declarations), and a Ghidra pass is only needed for the
client-GUI classes MSVC does not export.

## Handler verdicts

91 `[UnmanagedCallersOnly]` handlers across 83 hook install sites. (The map says 97; that
was a grep of the word, this is the count of attribute-decorated declarations. Nothing
hangs on the difference, but the number below is the one that was audited.) The count that
matters is the **distinct engine symbols** they attach to — 79 — because the shape is the
callee's, not the handler's.

### Alloy (37 handlers, 31 distinct symbols) — parameter shapes IDENTICAL

Every Alloy hook target demangles to the same parameter list on both platforms. **No Alloy
hook crosses the boundary with a by-value class, a `CExoString`, a `CResRef` or a
`Vector`**: `Scene::SpawnTile` and `Scene::DeleteTile` take `Vector const&`, which is a
pointer on both ABIs, and everything else is scalars and pointers. So none of the four
#433 hazard flags fires inside Alloy:

| Hazard | Alloy hits |
|---|---|
| by-value struct argument or return (sret placement) | **0** |
| `CExoString` / `CResRef` / `Vector` by value (who destroys it) | **0** |
| more than 4 integer/float arguments | 1 — `Scene::SpawnTile` (6) and `Scene::InitBSP` (6), both all-scalar |
| float mixed with integers in one list | 2 — `Scene::InitBSP`, `PartDanglyMesh::Animate` |

The last two are **not** findings. Win64 consumes one register *position* per argument
where SysV counts integer and SSE registers separately, but the C# declaration names the
types in order and NativeAOT lays them out per-platform — a faithful declaration is
correct on both. Hand-encoding the Itanium layout is what breaks; none of these do.

One real finding, and it is a **Linux bug the Windows export table caught**:

| Symbol | Callee returns | Handler declared | Verdict |
|---|---|---|---|
| `CAurTextureBasic::glImageBasic(bool)` | `int` | `void` (handler **and** trampoline) | **WRONG on both platforms.** The trampoline's value was dropped and then clobbered by the managed work that runs after it; the one direct caller (`0x48cbb0`) propagates the result to its own return. Fixed: both declared `int`, the trampoline's value carried through. |

Everything else matches, including `Scene::SpawnTile` → `CAurObject*` (declared `IntPtr`)
and `CExoResMan::Demand` → `void*` (declared `long`, same 8-byte RAX on both).

The `EngineZonesService` timers declare `long` for six `void` callees
(`ProcessReceivedFrames`, `Update`, `UpdateState`, `UpdateAndRender`, `ManageSceneBSP`,
`AddPartsToDrawBuckets`). That is benign on both ABIs — a callee may clobber RAX freely
and the caller of a `void` function does not read it — and it is deliberate: one delegate
shape for the whole zone table.

Five Alloy symbols have no Windows export and so no mechanical verdict:
`CNWCAnimBase::RefreshHilite`, `Part::~Part`, `PartEmitter::~PartEmitter`,
`CNWCArea::LoadBorder`, `CNWCArea::ReloadBorder`. All five are `(this)` or
`(this, ptr, float)` shapes with no by-value class, so the Win64 risk is the return type
alone; they need a decompiler pass when Alloy's Windows anchors are cut.

### Outside Alloy (54 handlers) — where the by-value hazards actually live

Every `CExoString` / `CResRef` / `Vector` crossing in the repo is on the 2D side, and all
of them are already ported or explicitly stood down:

| Symbol | By-value crossing | Status |
|---|---|---|
| `CGuiInGame::AppendToMsgBuffer` | `CExoString`, `CResRef` | #419: Windows takes a pointer to a copy; the swallowing handler runs `Native.DestroyExoString` or leaks one buffer per line. `CResRef` is 17 bytes → pointer to a copy on Win64, 16 bytes in memory on SysV — separate handler shapes, already split. |
| `CGuiInGameChatWindow::AppendToChatBuffer` | `CExoString`, `CResRef` | same, same fix |
| `CGuiInGameChatPrompt::SetFocus` | `CExoString` | same |
| `CNWCObject::SetPopUpBubbleText` | `CExoString` | #420 ported the offsets; the **read ordering** was wrong until PR #438 — see below |
| `CNWCObject::ShowFloatyText` | `CExoString` ×2, `Vector` | #420: the `ColourArg` struct in `CombatTextService` is the per-platform shape of the by-value `Vector` |
| `CGuiInGame::ShowReplyChosen` | `CExoString` | #420, ported |
| `CGuiInGame::ShowDialogEntry` | `CExoString const&` — a pointer, no hazard | ported |
| `CGuiInGame::ShowDialogReplies` | `CExoString*` — array, no hazard | ported |

**Ownership is only half of it: *when* the hook reads matters too.** A by-value `CExoString`
that the Win64 callee destroys is gone the moment the original returns — the caller "sees it
zeroed on return". So a hook that **calls through and then reads** is correct on Linux, where
the caller owns the copy, and silently reads an empty string on Windows. No crash, no error,
no log: the feature just produces nothing. That is exactly how chat bubbles died
(`SetPopUpBubbleText`, PR #438) while nameplates carried on, because the plate branch never
touched the string. This audit's first pass asked only "who destroys it" and would not have
caught it; the question to ask of every by-value crossing is **both**:

1. who runs the destructor (a swallow must run it itself), and
2. does every read happen *before* the forward.

`ChatService.OnAppend` is the pattern to copy — the read sits above the forward with the rule
written on the line.

The rest are scalars and pointers: `HandleWMKeyDownMessage(unsigned, unsigned) -> int`
and `WinMessageHandler(SDL_Event*) -> int` are name-exported and confirmed identical;
the `CGuiInGame*` / `CNWCMessage` / `COptionsPanel` / `CPanelQuickBar` /
`CCachedMouseCursorManager` handlers are `(this, scalars)` and carry no by-value class.

### Callbacks, not hooks (8 handlers)

Eight handlers never attach to an engine symbol, so there is no callee signature to
compare against. Their verdicts:

| Handler | Reached by | Verdict |
|---|---|---|
| `Entry.CxInit` | `bootstrap.c` via `dlsym`/`GetProcAddress` | **ours on both ends**; the native bootstrap declares the same `void(void)` |
| `GlContextService.XErrorNoop` | `XSetErrorHandler` | **Linux-only** — Xlib does not exist on the Windows leg |
| `GlContextService.OnDebugMessage` | `glDebugMessageCallback` | identical: `GLDEBUGPROC` is `APIENTRY`, which is `__stdcall` on Win32 and plain on x64 — one convention on Win64 |
| `HotkeyConsent.OnAnswer` | the engine, through `CPanelOkCancel+0x88` | `(void*, int)`, verified by #48's consent port, which runs on Windows |
| `SceneWalk.OnBspNode` | passed to the engine's BSP walker as a callback | `(node, ctx)` pointers only; no by-value class |
| `AlloyRenderer.EnsureBoneSlots`, `OwnedBuffers.Take`, `OwnedTextures.Adopt` | our own code, through a function pointer | internal; scalars and pointers, platform-neutral by construction |

## Vtable slots

MSVC gives a virtual destructor **one** slot (the scalar deleting destructor); Itanium
gives it **two** (complete + deleting). Everything declared after the destructor therefore
sits one pointer earlier on Windows. That is the whole of the `GetPortrait`
`0x1d8 → 0x1d0` shift #420 found by hand, and it holds for all nine slots Alloy calls
through — **measured, not assumed**, by reading both tables:

    tools/abi/vtable-slots.py Part
    tools/abi/vtable-slots.py CAurTexture --n 30

The tool needs no Ghidra. Linux exports `_ZTV<class>` and every virtual it points at.
`nwmain.exe` does not export vftables, but it *does* name-export the virtuals, so the tool
finds the vftable by scanning `.rdata` for a run of pointers into that class's virtuals —
and on `CAurTexture` it independently lands on `0xd90c70`, the RVA `SymbolMap` derived by
hand.

**And when nothing is exported, RTTI still is.** MSVC keeps a class's name in its RTTI
whether or not the linker exported a thing, so the tool falls back to: type-name string →
`TypeDescriptor` → complete object locator → the word that stores it, and the vftable begins
8 bytes later. That reaches the client-GUI classes the export scan cannot, which makes it an
**RVA source for `SymbolMap._win`, not only a slot source**: an override's address read out of
the Windows vftable *is* the `At(...)` value. Worked example — `CPanelQuickBar::Reposition`
is Linux slot `+0x128`, so Windows `+0x120`, and the entry there (`0xe52900 + 0x120`) is
`0x860010`. Confirm such a read before trusting it: that slot is the only one in its
neighbourhood whose entry differs from sibling `CGuiPanel` subclasses, which is precisely
what "`CPanelQuickBar` overrides it and they do not" looks like.

| Class | Slot | Linux | Windows | Read by |
|---|---|---|---|---|
| `Part` | `AsPartTriMesh()` | `0x60` | `0x58` | `SkyBox`, `BucketGate`, `GrassField` |
| `Part` | `AsPartBeam()` | `0x90` | `0x88` | `Beams` |
| `Part` | `PostProcess(Part*)` | `0xe0` | `0xd8` | `SkinPose` |
| `CAurTexture` | `AsCAurTextureBasic()` | `0x10` | `0x08` | `AlloyCensus`, `OwnedTextures` |
| `CAurTexture` | `GetReferencedTexture()` | `0x18` | `0x10` | `AlloyCensus`, `OwnedTextures` |
| `VertexPrimitive` | `SetupArrays()` | `0x38` | `0x30` | `SceneWalk` |
| `CAurBehavior` | `AsBehaviorCameraFollow()` | `0x48` | `0x40` | `SceneInputs` |
| `CNWCObject` | `GetPortrait(uchar)` | `0x1d8` | `0x1d0` | `NameplateNative`, `DialogService` (#420) |

All seven Alloy slots are now `Field(..., Expect.VtblSlot)` rows in `EngineLayout` (values diverging per OS in the build file), and
the files that read them read a named field. The `Expect.VtblSlot` tag is **declared but not
yet armed**: the predicate exists and would catch a slot that does not point into the image,
but nothing calls `EngineLayout.Probe` for `Part`, `CAurTexture`, `VertexPrimitive` or
`CAurBehavior` — today only `NameplateNative` probes, and only
`CNWCCreature`/`CNWCObject`. Wiring the four first-sighting calls is the sharp piece of
#429's "expanding the probe vocabulary".

**The one unmeasured slot** was `HotkeyMenuService.SetTextFromCharVtblOffset = 0xd8`, read off
the object at `CAuroraStringWrapper+0x10`, whose class this audit did not pin down. The service
and the slot were deleted with the nuklear hotkey manager (#592), so every vtable slot the repo
reads now has a Windows value.

### Two traps the dumps exposed

- **MSVC reverses an overload set.** `CAurBehavior::SetScene` is `(CAurScene*, CAurScene*)`
  then `()` on Linux, and `()` then `(CAurScene*, CAurScene*)` on Windows. It is a swap
  *within* the set, not a shift, so nothing after it moves — but a slot identified by
  "the Nth SetScene" is wrong on one of the two platforms. Identify by signature.
- **Identical code folding renames a slot.** In the Linux `PartEmitter` table, `Part::Draw`
  and `Part::Mat` share one address, so `nm` labels both slots `Part::Mat()`. A slot read by
  *name* would pick the wrong one; the index is what is stable.
