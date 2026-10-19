# Windows symbol resolution

The Linux client (`nwmain-linux`) is an unstripped ELF with default-visibility
exports, so every hook/call target resolves by its Itanium-mangled name at runtime
(`./sym.sh -m`). The Windows client (`nwmain.exe`) is a PE whose exports are
**opt-in**: Beamdog marks only shared-engine classes for export and omits the
client-only GUI / message classes. So on Windows some symbols resolve by name and
the rest resolve by **RVA** (address = module base + RVA).

`managed/Framework/SymbolMap.cs` is the single source of truth pairing each Linux
name with its Windows resolution. This file explains where the RVAs came from and
how to regenerate them when Beamdog patches the client.

## Build lock

RVAs are only valid for the exact build they were read from. `SymbolMap` checks the
PE `TimeDateStamp` against the running build's file (`managed/Framework/Builds/`) and
refuses RVA-based hooks if they differ (fail-loud, degrade to no-op — never corrupt).
The RVAs in the tables below are `8193.37-17`'s, and so is its build file; a later build's file
carries all of them anew, re-anchored by `tools/update/rvamatch.py` (`docs/update-day.md`).

| | |
|---|---|
| Build revision | `26c6e57364a45246e3b58e4f2c49366df72d15db` (Mon Oct 6 2025) |
| PE TimeDateStamp | `0x68e408bc` |
| Image base | `0x140000000` |

Linux and Windows binaries were confirmed same-revision (`bin/*/build.txt`) before
deriving anything — required, or the RVAs point into a different build.

## Resolution table

**By name (exported on both platforms):**

| Linux (Itanium) | Windows (MSVC) |
|---|---|
| `HandleWMKeyDownMessage(uint,uint)` | `?HandleWMKeyDownMessage@@YAHII@Z` |
| `WinMessageHandler(SDL_Event*)` | `?WinMessageHandler@@YAHPEATSDL_Event@@@Z` |
| `GLRender::SwapBuffers(SDL_Window*)` | `?SwapBuffers@GLRender@@CAXPEAUSDL_Window@@@Z` |
| `CAurBehaviorCameraFollow::Control(float)` | `?Control@CAurBehaviorCameraFollow@@UEAAHM@Z` (#536; returns `int`, no by-value crossing) |
| `CExoString::InitFromCharArray` | `?InitFromCharArray@CExoString@@AEAAAEAV1@PEBDI@Z` |
| `CExoString::~CExoString` | `??1CExoString@@QEAA@XZ` |
| `g_pAppManager` (data) | `?g_pAppManager@@3PEAVCAppManager@@EA` |
| `CGuiEdit::GetText()` | `?GetText@CGuiEdit@@QEAA?AVCExoString@@XZ` (#580; returns `CExoString` by value -- sret AFTER `this` on Win64, `PromptCommandService.ReadLine` branches) |
| `CExoConfig::Get<bool>(std::string const&, bool)` | `??$Get@_N@CExoConfig@@QEAA_NAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z` (#571; scalar return, no sret; the `std::string const&` is a pointer to a header `StockConfig` forges per ABI) |
| `CExoConfig::Get<long>(std::string const&, bool)` | `??$Get@_J@CExoConfig@@QEAA_JAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z` (#571; `_J` = `__int64`, the same 64-bit instantiation) |
| `CExoConfig::Get<double>(std::string const&, bool)` | `??$Get@N@CExoConfig@@QEAANAEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@std@@_N@Z` (#571) |

**By RVA (not exported; build `0x68e408bc`):**

| Linux symbol | Windows RVA | How anchored |
|---|---|---|
| `CNWCMessage::HandleServerToPlayerMessage` | `0x7a41a0` | unique strings `"HandleServerToPlayerMessage"` + `"[%s] Discarding unknown message: %s"` |
| `CNWCMessage::HandleServerToPlayerLogin` | `0x7a31b0` | unique strings `"HandleServerToPlayerLogin"` + `"nNumClasses <= 8"` |
| `CNWCMessage::SendPlayerToServerMessage` | `0x7c25c0` | tracy zone string `"SendPlayerToServerMessage"` |
| `CNWCMessage::SendPlayerToServerDeviceAdvertiseProperty` | `0x7bbac0` | caller of SendMsg; write-sequence + `SendMsg(major 0x36)` |
| `CClientExoApp::GetNWCMessage` | `0x6abf90` | decompile of `COptionsPanel::HandleSaveCharButton` @`0x82fb60` (first call; that handler is no longer hooked, #592) |
| `CGuiInGame::SetFocusChatPrompt` | `0x6fae30` | decompile: reads `+0x18`, forwards to chat-prompt SetFocus |
| `CGuiInGameChatPrompt::HandleEnter` | `0x87b0d0` | caller of `CGuiEdit::LoseFocus`; GetText+Find(`a`,`d`)+SetCaretPos |
| `CGuiInGameChatPrompt::HandleEsc` | `0x87b390` | caller of LoseFocus, 4 callees, no GetText |
| `CGuiInGameChatPrompt::HandleLMouseDownGuiEvent` | `0x87b450` | Notify event enum **1** (matches Linux) |
| `CGuiInGameChatPrompt::HandleLoseKeyboardFocusLMouseDownGuiEvent` | `0x87b4b0` | Notify event enum **2** (matches Linux) |
| `TurnOffKeyboardModal()` | `0x6e5d80` | decompile of `CGuiEdit::SetFocus` (exported): the call right after `CExoInput::KeyboardModeOn`; body reads `*(*g_pAppManager+8)`, resets modal keys, clears the WASD drive latch + sends `AbortDriveControl` (#34) |
| `operator new(size_t)` (consent modal alloc) | `0xc10324` | `_malloc_base` + `_callnewh` loop (VS2017/2019 release); the exact allocator the ExitButton `CPanelOkCancel` spawn calls |
| `CGuiInGame::AppendToMsgBuffer` | `0x6f68d0` | unique strings `"[CHAT WINDOW TEXT] [%s] %s\n"` + `"AppendToMsgBuffer"` (guiingame.cpp:0x2a3). **Win64 ABI: 6th arg is `CResRef*`** (17-byte aggregate -> pointer to a caller copy) |
| `CGuiInGameChatWindow::AppendToChatBuffer` | `0x879080` | the 6-arg callee AppendToMsgBuffer calls twice (once per window); shares `"BUT_CHAT_REPLY"` with SetFilter |
| `CGuiInGameChatWindow::SetFilter` | `0x87a0e0` | the other `"BUT_CHAT_REPLY"` user; 3 args, writes the filter mask `this+0xae0` first (Linux `+0xc58`) |
| `CGuiInGameChatWindow::Show` | `0x87a940` | caller of `CGuiPanel::Activate` (exported) with the `if(+0xaf0) return; Activate; +0xaf0=1` shape (Linux flag `+0xc68`); called from ShowMsgWindow only |
| `CGuiInGameChatPrompt::Show` | `0x87cc00` | same shape on flag `+0x200` (Linux `+0x230`); its lone caller is ShowChatPrompt |
| `CGuiInGameChatPrompt::CGuiInGameChatPrompt()` | `0x87abb0` | the `+0x200` Activate shape that RETURNS `this` and is stored to `gui+0x18` by its caller. **MSVC inlined Show here**, so Windows hooks the ctor and Deactivates after it |
| `CGuiInGameChatPrompt::SetFocus` | `0x87cb30` | callee of PrepChatPromptForTell (below) and of the mapped SetFocusChatPrompt; calls `CGuiEdit::SetFocus` (exported) then NotifyServerAboutGuiEvent(…,1,…,0x7f000000). Embedded edit at `+0xf8` (Linux `+0x100`) |
| `CGuiInGame::ShowChatPrompt` | `0x6fb480` | the one caller of ChatPrompt::Show, forwarding `*(this+0x18)` |
| `CGuiInGame::PrepChatPromptForTellToPlayer` | `0x6f9ec0` | unique string `"/tp \"%s\" "`; reads `player+0x10` (name) |
| `CGuiInGame::NotifyServerAboutGuiEvent` | `0x6f9bd0` | 5-arg callee of ChatPrompt::SetFocus; GetPlayerCreature + GetNWCMessage (mapped `0x6abf90`) guards |
| `CClientExoApp::GetPlayerByPlayerName` | `0x6ac120` | ParseChatString (`"tellplayer"` string, `0x87b590`) calls it once between the two ByCreatureName calls, matching the Linux order; body copies the string and walks `intern+0x38/+0x40` comparing `player+0x10/+0x18` |
| `CClientExoApp::GetPlayerByCreatureName` | `0x6ac080` | ParseChatString's first and third lookup; inner walk compares `player+0x40/+0x48` |
| `CClientExoApp::GetModule` | `0x6abea0` | forwarder to `*(intern+0x30)`; ConnectToServer (caller of exported `CGuiMan::EscapeAllModalPanels`) reads `GetModule()+0xf8` through it, and the tracy-anchored `HandleServerToPlayerModuleUpdate_Time` (`0x7a58e0`) writes `+0x160..+0x16c` on its result |
| `CNWCModule::TurnCamera(float,int)` | `0x7cfe60` | #539: `CNWCModule` has 0 exports; found by offset signature with `tools/ghidra/ScanOffsets.java` (`+0x2bc` flag byte & 0x10, behavior `+0x58`/`+0x88` yaw velocity/impulse, `+0x134 \|= 1`, `+0x280 = 1`) -- 3 hits, the only one in the camera-setter cluster (SetCameraLimits `0x7cf9d0`..); body decompiles identical to Linux with vtable slots `-8` |
| `CNWCModule::TiltCamera(float,int)` | `0x7cfdb0` | same scan on the pitch rows (`+0x2bc` & 0x20, `+0x60`/`+0x8c`, `+0x134`); sits 0xb0 below TurnCamera as on Linux (0x7d88a0 / 0x7d8960) |
| `CClientExoAppInternal::UpdateDriveMode()` | `0x6f1e40` | #540: the only function carrying both drive cadence constants 499999 (`0x7a11f`, keep-alive) and 250000 (`0x3d090`, prediction) -- `ScanOffsets.java 7a120` / `3d090` intersect on it (MSVC compares `< 500000`); body decompiles identical to Linux @0x65f780: same fields `+0x1b0..+0x1cc`, `+0x198/+0x1a0`, `+0x208`, callees DriveControl `0x7bf980`, TurnOnSpot `0x7c03b0`, DrivePlayer `0x6ea9e0`, GetPlayerCreature `0x6ac1a0`, vtable snap slot `+0x120` (Linux `+0x128`) |
| `CGuiInGame::ToggleOff()` | `0x6fc6f0` | #543: `HandleServerToPlayerMessage` (`0x7a41a0`) case 0x33 -> the cutscene handler `0x786450` (ReadBOOL/ReadFLOAT arms identical to Linux `HandleServerToPlayerCutscene`) -> minor 7's the arm for value 1 arm; body identical to Linux @0x66dda0 (`+0x374` flag, `CGuiPanel::ToggleOff` to all panels, NUI scope 2) |
| `CGuiInGame::UnToggleOff()` | `0x6fc900` | the `else` arm of the same minor 7; body identical to Linux @0x66e930 (`+0x374`, `CTL_BTN_PB_LVLUP`, `AnimateScene(1e6)`) |
| `CNWCMessage::SendPlayerToServerChat_Talk` | `0x7ba7e0` | caller of SendPlayerToServerMessage with major 9 minor 1 |
| `…Chat_Shout` | `0x7ba700` | major 9 minor 2 |
| `…Chat_Whisper` | `0x7baab0` | major 9 minor 3 |
| `…Chat_Tell(uint, CExoString)` | `0x7ba8c0` | major 9 minor 4 after `WriteBOOL(0)` + `WriteDWORD(id)` |
| `…Chat_TellName(CExoString const&, CExoString const&)` | `0x7ba9b0` | major 9 minor 4 after `WriteBOOL(1)`, two strings |
| `…Chat_Party` | `0x7ba620` | major 9 minor 6 |
| `…Chat_DungeonMaster` | `0x7ba540` | major 9 minor 0xe |
| `CNWCMessage::HandleServerToPlayerChat` | `0x77c140` | `case 9` of the mapped HandleServerToPlayerMessage dispatch (`0x7a41a0`); also references the `"HandleServerToPlayerChat"` string directly |
| `CNWCMessage::HandleServerToPlayerPlaceableUpdate_Delete` | `0x7a8240` | its Tracy `__FUNCSIG__` `"int __cdecl CNWCMessage::HandleServerToPlayerPlaceableUpdate_Delete(void)"`; one call site, `0x7b35b0` (the Update_Delete dispatch); body matches Linux (`45000` hold, `+0x104 = 1`) |
| `CNWCMessage::HandleServerToPlayerDialog` (not hooked; the anchor for the four below) | `0x7962f0` | `case 0x14` of the same dispatch |
| `CGuiInGame::CloseDialog` | `0x6f6d80` | the Dialog handler's minor-5 branch: `GetInGameGui` (`0x6abbc0`) + `Initialized` (`0x6f9a90`) guard, then this. Body: panel at `gui+0x70` (same), `ResetGuiState(0x298)` |
| `CGuiInGame::ShowDialogEntry` | `0x6fb560` | minor-1 branch, `(gui, &str, oid, oid)` after `ReadCExoLocStringClient` |
| `CGuiInGame::ShowDialogReplies` | `0x6fb8e0` | minor-2 branch; body writes `panel+0x134` and `panel+0xbc0` (Linux `+0x144` / `+0xd08`) then `FixDialogSize` |
| `CGuiInGame::ShowReplyChosen` | `0x6fbbc0` | minor-3 branch; **destroys its by-value `CExoString` at the tail** -- the skip must too |
| `CNWCMessage::SendPlayerToServerDialog_Reply` | `0x7bbc50` | caller of SendPlayerToServerMessage with major `0x14` minor `3`; `CreateWriteMessage(9)`, OBJECTID + DWORD + BYTE + DWORD |
| `CClientExoApp::GetGameObject` | `0x6abb60` | forwarder to `Internal::GetGameObject` `0x6d81f0`; the Dialog handler calls it with `GetPlayerCreature()+0xdc` exactly like Linux |
| `CNWCCreature::SpawnHitVisuals` | `0x80bb10` | its own log tag `"SpawnHitVisuals"` @`0x140e47eb8`, 5 refs all inside it; by-value `Vector` and `CExoString` arrive as pointers (CX-158 §3.1, §6) |
| `CClientExoApp::GetObjectName` | `0x6abfc0` | forwarder to `Internal::GetObjectName` `0x6d83f0` = the one `Internal` caller of `C2DA::GetINTEntry(int, CExoString const&, int*)` (exported, `0x1a7010`) with the `"StrRef"` column; out-param order `(this, id, CExoString*)` unchanged |
| `CGuiInGamePopUpBubble::SetChatBubbleMode(int)` | `0x6e90e0` | callee of the console wrapper anchored by `"SetChatBubbleMode 0/1/2: Off, text-only, or full background"` (`0x6e9060`); walks the bubble list writing `+0x114` / `+0x148` (Linux `+0x11c` / `+0x150`) |
| `CNWCObject::SetPopUpBubbleText(CExoString, int, float, float)` | `0x766450` | the common callee of the three ShowFeedbackBubbleText bodies and the chat handler; checks `this+0x104 == 1` (retired) first; **frees its by-value `CExoString`** (forwarded, never skipped) |
| `CNWCCreature::ShowFeedbackBubbleText` | `0x80b240` | caller of exported `CTlkTable::GetSimpleString` building the `"STRING_REF"` 2DA column; sets `this+0x448 = 1` (Linux `+0x468`) |
| `CNWCPlaceable::ShowFeedbackBubbleText` | `0x871b60` | SetPopUpBubbleText caller with the `+0x1f8 = 1` / `bubble+0x144 = 0` shape (Linux `+0x218`) |
| `CNWCItem::ShowFeedbackBubbleText` | `0x83f230` | SetPopUpBubbleText caller with `+0x264 = 1`; **MSVC passes the `CNWCObject` subobject as `this`** (the body does `this-0x48` for `CNWCItem::GetName`), so no thunk to avoid |
| `CNWCObject::ShowFloatyText(CExoString, Vector, CExoString, int)` | `0x766a70` | callee of `HandleServerToPlayerCreatureUpdate_FloatyText` (`0x781a10`, tracy source-location struct at `0xe3e988` referencing the `__FUNCSIG__` string). **Win64: the `Vector` arrives as a pointer** (12-byte aggregate); both strings are callee-owned |
| `CNWCAnimBase::GetAnimationName(ushort)` | `0x961540` | the only user of `"pausewalkfl"` and `"runfr"` (and of every `"CustomNN"` name) (`StringRefs.java`). Body confirmed against Linux `0x9ccd80`: the same id switch on the third argument, the `+0xde == 1` humanoid test choosing `pausewalkfl` over `cwalk`, then the `GetAnimationReplace` tail. **Win64: `(this, sret, id)`**, the buffer returned in rax (Linux `(sret, this, id)`) |
| `CNWCAnimBase::RefreshHilite` | `0x966170` | its own `ExoLog::Emit` call — file `game\\clientcore\\nwcanimbase.cpp`, line `0x123a`, `"Attempting to unhighlight a gob in a null scene"` — plus the tracy zone string `"RefreshHilite"`. Structure confirmed against Linux `0x9cb260`: the same 5-channel loop, lowest-active-wins, then `Scene::AddHighlightGob` |
| `CNWCArea::LoadBorder` | `0x7dea10` | `__FUNCSIG__` string `"int __cdecl CNWCArea::LoadBorder(class CGuiAreaLoadscreen *,float)"` via its tracy srcloc struct. **Win64 ABI: the `float` is the 3rd arg slot = `xmm2`**, not `xmm0` |
| `CNWCArea::SetWeather(unsigned char, float)` | `0x7e63c0` | #615: the only user of `"weatherlight"` and `"ModelLightning"` (and of `"weather"` as a CAurObject tag). Body confirmed against Linux `0x702b10`: clamps density into `+0x2f4`, deletes the six fx objects at `+0x258` and the lightning one at `+0x288` when density is 0 or the type at `+0x2f0` changes, re-creates them from `weathertypes.2da` "Model"/"ModelLightning", drives the "Animation" through the CAurObject vcalls. Every field offset Same. **Win64 ABI: the `float` is the 3rd arg = `xmm2`** |
| `CNWCArea::StartWeather(unsigned char)` | `0x7e69a0` | #615: the ReadBOOL-true callee of `HandleServerToPlayerArea`'s case 4 (the other being StopWeather). Body identical to Linux `0x704de0`: `+0x2f1 = type`, `+0x2ec = 6000`, `CNWCAmbientSound::Rain(+0x2d8, type == 1, +0xe3 != 0, 6000)` |
| `CNWCArea::StopWeather(unsigned char)` | `0x7e69e0` | #615: the ReadBOOL-false callee of the same case; `+0x2f1 = 0`, `+0x2ec = 6000`, the same sound call with 0 |
| `CNWCArea::ReloadBorder` | `0x7e1e40` | no string of its own; anchored through its ONE caller, `CNWCMessage::HandleServerToPlayerArea` (`0x76d910`, `__FUNCSIG__`-anchored), whose tail is three flag-bit branches in the same order on both builds — bit 1, bit 4, then bit 2 → this. Body confirmed: walks the scene at `area+0x248` calling `DeleteTile` then `SpawnTile` |
| `CPanelQuickBar::Reposition()` | `0x860010` | #439: RTTI vftable `0xe52900` (`tools/abi/vtable-slots.py`) slot `+0x120` = Linux `+0x128` − 8 (#433). Body confirmed: reads `g_pGuiMan+0xb8`, spawns `"GUI_QBAR_ENDS"` once, ends in `SetPositionScreen((w-800)/2, 0)`; the CAurObject vcall inside shows the same skew (Linux `+0xa0`, Windows `+0x98`) |
| `CGuiQuickButton::SetIcon(CGuiIcon*, CGuiIcon*)` | `0x86baa0` | `SetButtonItem` (`0x867760`)'s icon call; body frees `+0x130` (Linux `+0x150`) and attaches both icon models -- same shape as Linux @0x6a8cc0 |
| `CGuiQuickButton::SetCountText(CExoString)` | `0x86b710` | `UpdateButtonCountText` (`0x86bef0`, `SetButtonItem`'s tail call) passes it the `"*"` copy by address; the Win64 callee destroys it |
| `CPanelQuickBar::ClickButton(uchar)` | `0x85c3b0` | unique strings `"ClickButton"` + `"nButton < CPANELQUICKBAR_SLOTS_NUM_VISIBLE"`; reads the active set at `+0x6510` (Linux `+0x7298`), stride `0x2c8` |
| `CGuiQuickButton::HandleLeftButton()` | `0x85c8f0` | `ClickButton`'s one call, on the active set's button |
| `CGuiInGame::ShowCompass()` | `0x6fb490` | #439: the one user of `"pnl_compass"` + `"Viewport_Compass"`; builds the `CGuiCompassPanel` into `gui+0x68` (same), then `ResetGuiState(0x1000)` (`0x6fa7b0`) |
| `CGuiInGame::HideCompass()` | `0x6f9060` | #439: the `'0'` branch of the `"usage: GuiCompass [0|1]"` console handler (`0x6f3f80`, whose `'1'` branch is ShowCompass above). Body: deleting-dtor vcall on `gui+0x68`, null it, `ResetGuiState(0x1000)` |
| `SharedMaterial::~SharedMaterial` (#275) | `0x81f80` | the exported `??1SharedMaterial@@QEAA@XZ` has NO callers -- MSVC inlined it into the scalar deleting destructor `(this, flags)`, called from `RemoveRef` and `RefreshSharedMaterial`, the same two callers Linux D2 has. Decompiled: `(this, uint flags)`, the eleven SafePointer releases + `AurTextureRelease`, `delete` when `flags & 1`, returns `this`; both callers pass 1 |
| `CNWCCreature::UpdateLookAt()` | `0x80fa60` | head tracking: `CNWCCreature` name-exports nothing, so the chain starts at the Windows `CNWCCreature::LookAt` (`0x803f20`), the ONE function referencing `"HEADTRACK"`, `"HEAD_ARC_H"` and `"HEAD_NAME"` (`StringRefs.java`), and takes its only caller (`Callers.java`, three call sites = the same three `LookAt` calls Linux makes). Body confirmed against Linux `0x726f00`: the same `& 3 == 3` mode gate (`+0x37c`, the class's -0x20 shift), the four candidate slots at `+0x424..+0x430` (Linux `+0x444..+0x450`), the 110 deg rejection and the 8.0 / 5.0 reaches. `(this) -> void` |
| `CActionTarget::ActivateTargetMode()` | `0x83b9e0` | auto-target: `CActionTarget` is client-side and name-exports nothing, so it is anchored through the `'AcTg'` `GetGuiObjType` tag cluster and confirmed by decompile against Linux `0x9652b0` -- the same `CaptureMouse` / `CaptureKeyboard` / `RegisterUpdate` / `RegisterActionTargetGUI` body. `(this) -> void`, no by-value crossing |
| `CActionTarget::HandleLMouseDown()` | `0x83be00` | the neighbouring function of the same cluster; body line-for-line the Linux `0x965840`: `CGuiMan::GetMousePos` -> `FeedbackHitCheck` -> the allowed-target scan (`+0x108` for `+0x110` entries, Linux `+0x148` / `+0x150`) -> the per-mode fire callback. Called, never hooked. `(this) -> void` |
| `CClientExoAppInternal::PerformDefaultActionOnObject(CNWCObject*)` | `0x6efec0` | CX-636 click-to-lock: `WalkPlayerToPoint` (`0x6f3650`) has two callers; the big one (`0x6f02c0`) is `PerformLButtonDownAction` (GUI first, then the hostile/non-hostile pick, the `== 2` object branch, the drag-square arm) and the function it calls in that branch is this one -- the same 5 / 6 / 9 / 0xa type-byte switch as Linux `0x660830`, creature to `0x6efa40`, placeable and door inlined. `(this, CNWCObject*) -> int`; non-zero = handled, the caller then skips the walk |
| `CNWCCreature::WalkUpdateLocation()` | `0x8114d0` | CX-682 step guard: the ONE function referencing both `"RUNDIST"` and `"WALKDIST"` (StringRefs; Linux `0x725830` inlines the first WALKDIST as an 8-byte literal, the others match). Body identical: rate off the animation, two `WalkUpdateLocationDistance` calls, stores the desired position at `+0x2c0` (Linux `+0x2e0`), `MoveCreatureToPosition`, the ground-normal vcall at `+0x208` (Linux `+0x210`), the driving flag at `+0x2f8` (Linux `+0x318`). `(this) -> int` |
| `CNWCArea::TestDirectLine(CNWCCreature*, Vector, Vector, float, float, int)` | `0x7e6a20` | CX-682: the 7-slot callee of the anchored `WalkPlayerToPoint` (`0x6f3650`) called as `(module+0xf8, creature, &from, &to, *(pf+0xe0), *(pf+0xe4), 1)` with `pf = creature+0x300` (Linux `+0x320`). **Win64 ABI: both `Vector`s are pointers to caller copies**; the floats sit in the 5th and 6th slots (stack) |
| `CNWCCreature::MoveCreatureToPosition(Vector)` | `0x804840` | **evidence only** for the `SetPosition` vtable slot: the callee `WalkUpdateLocation` hands the new desired position; its body calls `vtbl+0x128` twice with `(this, Vector*)` where Linux `0x71cd70` calls `+0x130` -- the class's -8 |
| `g_vCreatureEnemyHighlight` / `Friend` / `Neutral` / `g_vPlaceableHighlight` / `g_vDoorHighlight` | `0x1296eb8` / `0x1296ec8` / `0x1296ed8` / `0x1296f08` / `0x1296ef8` | CX-636 lock colour: the `DAT_` reads of the Windows `HiliteAreaObjects` (`0x7db2f0`), one per attitude / type arm, matched to the Linux arm reading the named global. 16-byte stride on both, different order (Linux: placeable, local player, door, item, neutral, friend, enemy; Windows: local player, enemy, friend, neutral, item, door, placeable) |
| `CNWCArea::HiliteAreaObjects(int)` | `0x7db2f0` | **evidence only** -- not a `SymbolMap` row: it is the function the aim-assist layout rows were read off (`ObjVisible`, `ObjHighlightable`, `AreaObjectIds`/`Count`, `GameObjAsNWCObjectSlot`, `GameObjAsNWCPlaceableSlot`). Found with `ScanOffsets.java` over `{0x11c, 0x10c, 0x318, 0x320}` -- six hits, one decompiling line-for-line as Linux `0x7119f0` (same `+0xa8` byte test, same 5/6/9/0xa type switch, same `MouseOver` / `MouseOff` arms) |
| `CClientExoAppInternal::UpdateMouseCursorAppearance(int)` | `0x6f2830` | `At()`, hooked by the cursor fold: anchored by the unique string `"UpdateMouseCursorAppearance"` in its own ExoLog warning. The one writer of `CreatureHoveredOid` (Linux `+0x448`, Windows `+0x428`) and the caller that passes `internal+0xc8` (`InternalCursorManager`) to every `SetMouseCursor` |
| `CCachedMouseCursorManager::SetMouseCursor(int,int,int,int,int)` | `0x83b3e0` | `At()`, hooked by the cursor fold: the `0x125` id ceiling plus the `+0x1cb0` early-out-then-store, identical to Linux `0x6b72a0`; that store is `CursorCurrentId` |
| `CNWCMessage::SendPlayerToServerArea_AreaLoaded()` | `0x7ba050` | #672: body is exactly `SendPlayerToServerMessage(this, 4, 3, 0, 0)` = `FUN_1407c25c0(...)`, the already-mapped `0x7c25c0`. `(this) -> void`. Full walk: the research on CX-132 §3 |
| `CClientExoApp::StartLoadBar(int, char const*, int)` | `0x6acd20` | #672: one-line forwarder to the internal `0x6e37f0`, itself the only user of both `"ls_save"` and `"ls_load"`; stores arg1 at `internal+0x450` (Same). `-> void` |
| `CNWCArea::SetTile(CNWCTile*, int, int, int)` | `0x7e6040` | lightmap static-set reopen: the (ReadWORD, ReadINT, ReadBYTE, ReadBYTE) callee of `HandleServerToPlayerArea` case 0x14 on both builds; body identical to Linux `0x708e60` (GetTileData, GetLocation, ignore-list drop, UnloadModel, SetID, SetTileData, GetHeightTransition, SetPosition, SetOrientation, LoadModel, main/source light colour, PlayAnimation 7/8, RespawnStaticPlaceablesForTile). `(this, tile, int, int, int) -> void`, no sret |
| `CNWCArea::LoadArea(CAurCamera*, CGuiAreaLoadscreen*)` | `0x7dba70` | CX-52 step 5: the function carrying `"CNWCArea::LoadArea() failed: message overflow or underflow."` and the `"LoadArea"`/`nwcarea.cpp` ExoLog sites; its one caller (`0x76d910`, the area packet handler) tests the int, as Linux `0x8a9426` does. `(this, camera, loadscreen) -> int` |
| `CNWCArea::AddPlaceableObjectStatic(uint, ushort, Vector, Vector)` | `0x7d95a0` | #672/research #663 found it as evidence; CX-55 step 2 promoted it to an `At()` row. The call inside `LoadArea`'s `"Static Placeables"` tracy zone; `CExoArrayList<unsigned int>::Add((… *)(area + 0x338), id)` on both builds. The list's count at `+0x340` is read off the exported `?Add@?$CExoArrayList@I@@QEAAXI@Z` (rva `0x8ae10`): data `+0`, count `+8`, alloc `+0xc`, same as Linux. Decompiled whole for CX-55: body matches Linux `0x7091c0` line for line (`GetPlaceableByGameObjectID`, the `LowGore`/`ModelName` pair off `*(C2DA**)(*(g_pRules+0x130)+0x140)`, `new CNWCPlaceable`, appearance stored at object `+0x190`/Linux `+0x1b0`, `AddExternalObject`, the scene's add-static-object slot, the `+0x338` Add). **Win64 ABI: both `Vector`s are pointers to caller copies** (`VectorArg`); `(this, uint, ushort, Vector, Vector) -> int` |
| `CNWCObject::SetPosition(Vector)` / `CNWCAnimBase::SetPosition(Vector)` | `0x766760` / `0x9680a0` | **evidence only** (#672, research #663): `CNWCPlaceable::vftable + 0x128` and `CNWCAnimBase::vftable + 0xa0` (RTTI `0xe53950` / `0xe6e978`). The first reads `CNWCObject+0x78` (the object's word `0xf`) before its anim-base vcall, the second the pointer at `+200` = `CNWCAnimBase+0xc8`; both identical to Linux `0x7e9180` / `0x9c9d60` |
| `CAurTexture::vftable` / `CAurTextureBasic::vftable` (data) | `0xd90c70` / `0xd90d90` | RTTI type descriptors `.?AVCAurTexture@@` / `.?AVCAurTextureBasic@@` -> complete-object locator -> vftable (Ghidra names them). MSVC's symbol is the object's first word: no Itanium `+0x10` skew (`AurTex.Attach`) |

**By name — client-native features (verified in the `0x68e408bc` export table):**

| Linux symbol | Windows (MSVC) |
|---|---|
| `CPanelOkCancel::CPanelOkCancel()` | `??0CPanelOkCancel@@QEAA@XZ` |
| `CPanelOkCancel::Activate(CExoString,uint,uint)` | `?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z` |
| `g_pGuiMan` (data) | `?g_pGuiMan@@3PEAVCGuiMan@@EA` |
| `CAurora::GetGUIScale()` → float (GUI-scale multiplier) | `?GetGUIScale@CAurora@@SAMXZ` (export ord. 8543) |
| `SDL_GetKeyName` | `SDL_GetKeyName` |
| `CGuiPanel::Deactivate(int)` | `?Deactivate@CGuiPanel@@QEAAXH@Z` |
| `CGuiModalPanel::Deactivate(int)` | `?Deactivate@CGuiModalPanel@@QEAAXH@Z` |
| `CGuiPanel::UnToggleOff()` | `?UnToggleOff@CGuiPanel@@QEAAXXZ` |
| `RenderInterface::ResetRenderStates()` / `BindBuffer(uint,uint)` | `?ResetRenderStates@RenderInterface@@SAXXZ` / `?BindBuffer@RenderInterface@@SAXII@Z` |
| `stbtt_InitFont` … `stbtt_FindGlyphIndex` (7) | same C names |
| `g_pTlkTable` (data) | `?g_pTlkTable@@3PEAVCTlkTable@@EA` |
| `CTlkTable::GetSimpleString(uint)` -> `CExoString` | `?GetSimpleString@CTlkTable@@QEAA?AVCExoString@@I@Z` -- sret pointer is the 2nd arg on Win64 |
| `Gob::BeginLookAtAnimate(CAurObject*, char const*, char const*, float, float, float, float)` | `?BeginLookAtAnimate@Gob@@UEAAXPEAVCAurObject@@PEBD1MMMM@Z` (rva `0x50000`) -- head tracking; all-void, no by-value crossing |
| `LookAtAnimation::Control()` | `?Control@LookAtAnimation@@UEAAXXZ` (rva `0x50650`) -- the per-frame head aim, chained |
| `RenderInterface::GetViewPort(float*)` | `?GetViewPort@RenderInterface@@SAXPEAM@Z` |

**Not anchored, optional:** `CGuiInGameChatDialog::ForceDialogVisible(float)` is an empty body on both builds
(MSVC folds/inlines it); `CClientExoApp::GetModuleCamera()` is inlined at every Windows call site, so both
platforms read `GetModule()+0xe8` directly.
| `SDL_GL_GetProcAddress`, `SDL_GetMouseState`, `SDL_StartTextInput`, `SDL_Get/SetClipboardText`, `SDL_free` | same C names |

## The four forms, and how the table was filled (#447)

Every `public const string` in `SymbolMap.cs` has a row in `_win`. There is no fifth
form and no absence — `build.sh` refuses a constant without one, because a missing key
used to mean *both* "never looked" and "deliberately Linux-only".

| Form | Meaning | Counted? |
|---|---|---|
| `Nm("?Foo@Bar@@…")` | name-exported; resolved like Linux | no |
| `At(0x…)` | not exported; module base + RVA, build-locked | no |
| `LinuxOnly("why")` | a decision: not wanted on Windows | no |
| `Hole("why")` | not yet anchored | **yes** |

`/knobs` prints the tally beside `EngineLayout`'s unresolved offsets, and `Resolve`'s skip
line quotes the row's own words, so a log reader can tell a decision from a gap.

**The 142 `Nm` rows are mechanical, not hand-derived.** MSVC mangling encodes the return
type Itanium drops (#433), so `llvm-undname` over the 18237 PE exports and `llvm-cxxfilt`
over the Linux names pair on one key — qualified name plus parameter list, with return type,
`__cdecl`, `class`/`struct`, `__ptr64` and the `__int64`/`long long` spellings normalised
away. Nothing came back ambiguous. The licence for trusting it: run the same pass over the
39 rows already derived by hand and it reproduces **all 39 exactly**, wrong on none.

`tools/abi/check-win-symbols.py` is the standing check — every `Nm` row is a real export,
every `At` RVA lands inside the image (181 / 58, 0 bad). Alloy stands down under Proton, so
nothing exercises an Alloy `Nm` row at runtime yet; this is the only instrument that does.

**Measured per row, not paired:**

| Symbol | Windows | How |
|---|---|---|
| `PartDanglyMesh::vftable` | `0xd88698` | same |
| `CycleTIDTextureController::vftable` | `0xd905c0` | same |
| `primTypeTranslationTable` | `0xd8ad20` | the `{0,1,3,4,5,6,2}` dword run in `.rdata`, one hit in the image; `GLRender::RenderVertexData` @`0xb59b0` `lea`s it three times |
| `GLRender` pool `{base,count}` pair | `0x128ead8` | `GetPoolBuffer` @`0xb3cb0` loads it with `mov rax,[rip+0x11dae19]` then indexes `pool*0x20+0xc`; `UnlockPool` @`0xbbad0` `lea`s the same address into the helper. Linux has no symbol on the pair and steps `noemittervar+0x236c`, so `EngineLayout.PoolTableAnchorOffset` is `Diverges(linux: 0x236c, win: 0x0)` — the row wanted an *anchor*, not a value |

**The 12 declared holes** are every symbol that still has none, and not one of them is
Alloy's: nuklear (10 — the library is linked but exports nothing), `COptionsPanel::HandleXButton`
and `CClientAIMaster::UpdateState`. The last one is worth a note for anyone hunting it: the
*server* twin **is** exported (`CServerAIMaster`) because the client exe carries the
single-player server, so a name search finds a function with the right name and the wrong
class. `CClientExoApp::GetNetLayer` was a hole until CX-694 replaced the call with the field
it forwards to (`EngineLayout.InternalNetLayer`, `intern+0x18`): the Windows one-liner is
`0x6abfb0`, between `GetNWCMessage` `0x6abf90` and `GetObjectName` `0x6abfc0`, and the
Windows twins of `CNWCModule::LoadModule` / `CPlayModulePanel::SetServerInfo` reach it through
the app vtable slot `+0x38` right before their exported `CNetLayer::GetServerNetworkAddress` call.

## Radial menu seam (CX-689) -- `FindSym.java` on build `0x68e408bc`

The whole `CRadialMenu` surface is name-exported (its `protected` members included), so the
seam's calls and its one hook resolve by name. The client roots (`CGui*Menu`, `CClientSpellMenu`,
`CClientTrapMenu`) export nothing; the seam never calls them, it classifies a root by comparing
its vptr against the class's `vftable` label, which is an `At` row.

**By name:**

| Linux symbol | Windows (MSVC) |
|---|---|
| `CRadialMenu::ActivateInternal()` (the hook) | `?ActivateInternal@CRadialMenu@@IEAAXXZ` (rva `0x1f7f00`); `(this) -> void` |
| `CRadialMenu::CreateSubMenu(int)` | `?CreateSubMenu@CRadialMenu@@IEAAHH@Z` (`0x1f8960`); `(this, int) -> int` |
| `CRadialMenu::RecursiveAutoDelete()` | `?RecursiveAutoDelete@CRadialMenu@@IEAAHXZ` (`0x1f9da0`); `(this) -> int` |
| `CRadialMenu::static_nMenuDepth` (data) | `?static_nMenuDepth@CRadialMenu@@1HA` (`0x140cd58`) |
| `CGuiObject::s_pCurrentEventObject` (data) | `?s_pCurrentEventObject@CGuiObject@@1PEAV1@EA` (`0x140cdb8`) |
| `g_nCRadialMenu_ctorCounter` (data) | `?g_nCRadialMenu_ctorCounter@@3HA` (`0x140cd80`) -- the Windows ctor `??0CRadialMenu@@QEAA@XZ` increments it too; an earlier row called it Linux-only, wrongly |

Not in `SymbolMap` but read for the layout rows (`docs/engine-layout.md`, "The radial rows"):
`?SetEntryx@CRadialMenu@@QEAAXHPEBD0PEAXP8CCallbackHandlerBase@@EAAX_J@Z2@Z` (`0x1fa250`) --
its 6th parameter is the member-function pointer, **8 bytes on MSVC** (single inheritance)
against Itanium's 16-byte `{fn, adj}`, so a tap there has a per-platform signature;
`?SetSubMenuCreatorx@...` (`0x1fa9e0`), `?SetSubMenu@CRadialMenu@@IEAAXHPEBD0PEAV1@@Z`
(`0x1fa490`), `?DisableEntry@...` (`0x1f8b30`), `?HandleMenuAction@CRadialMenu@@IEAAXXZ`
(`0x1f9350`), `?Activate@CRadialMenu@@QEAAXXZ` (`0x1f7e80`), `?BuildIcon@CRadialMenu@@MEAAXH@Z`
(`0x1f8400`), `??0CRadialMenu@@QEAA@XZ` (`0x1f7950`), `??1CRadialMenu@@UEAA@XZ` (`0x1f7be0`),
`?Initialize@CGuiIcon@@QEAAXVCResRef@@HHH@Z` (`0x1e4430`), `?ReplacePrimaryIconTexture@...`
(`0x1e4690`), `?SetCountText@CGuiIcon@@QEAAXVCExoString@@@Z` (`0x1e4720`),
`?GetCountText_FirstLineOnly@CGuiIcon@@QEAA?AVCExoString@@XZ` (`0x1e4290`; **returns
`CExoString` by value, sret is the 2nd arg on Win64** -- the body writes through `RDX`),
`??0CGuiTextBubble@@QEAA@XZ` (`0x1fd310`).

**Measured per row, not paired (the `At` rows):**

| Symbol | Windows | How |
|---|---|---|
| `CRadialMenu::vftable` | `0xdb2e20` | the MSVC `vftable` label (Ghidra names it off the RTTI locator). The object's vptr points **at** the label -- no Itanium `_ZTV + 0x10` skew. Referenced by the ctor, the dtor and the scalar deleting dtor `FUN_1401f7d40` |
| `CGuiCreatureMenu::vftable` | `0xe74340` | same; its two writers are the two ctors (`0x98e070` = `(uint, uint, int, uchar, uint, uchar)`, `0x98e140` = `(CNWCCreature*, Vector)`), paired with Linux `@0x73f2e0` / `@0x73f110` |
| `CClientSpellMenu::vftable` | `0xe75bd8` | writers: the ctor `0x9a10e0` (`(Vector, uint, CGuiQuickButton*, int)`, Linux `@0x6f1650`; the by-value `Vector` arrives as a pointer) and the deleting dtor `0x9a19c0` (size `0x8a0`) |
| `CRadialSpellMenu::vftable` | `0xe76af8` | writer: the ctor `0x9aaed0` (Linux `@0x6f35b0`), zeroing the four per-slot int arrays from `+0x7f8` |
| `CClientTrapMenu::vftable` | `0xe77b90` | ctor `0x9b11d0` (`(uint)`, Linux `@0x9c7660`) + deleting dtor `0x9b1230` (`0x800`) |
| `CGuiItemMenu::vftable` | `0xe761d8` | ctor `0x9a5940` (`()`, Linux `@0x7d1e70`) + deleting dtor `0x9a5990` (`0x808`); `ActivateRadialMenu` is `0x9a59d0`, the callee every `new CGuiItemMenu` site hands the object to |
| `CGuiPlaceableMenu::vftable` | `0xe78030` | ctor `0x9b1950` (`(uint)`, Linux `@0x7ecf70`) |
| `CGuiDoorMenu::vftable` | `0xe77670` | ctor `0x9b0060` (`(uint)`, Linux `@0x756d00`) |
| `CGuiQuickbarMenu::vftable` | `0xe77340` | ctor `0x9ab450` (Linux `@0x6e4ec0`) + deleting dtor `0x9ab490` (`0x800`) |
| `CGuiChatWindowMenu::vftable` | `0xe78838` | ctor `0x9b3040` + deleting dtor `0x9b3080` (`0x800`) |

Item icons (CX-693), the same `FindSym.java` run -- the icon classes ARE name-exported (ctors,
`CopyIcon`, `CreateCopyIconModel`), only the `vftable` labels need an `At`:

| Windows symbol | RVA | Provenance |
|---|---|---|
| `CLayeredIcon::vftable` | `0xdaf750` | ctor `0x1e3000` (`??0CLayeredIcon@@QEAA@VCResRef@@HPEAGHHH@Z`, Linux `@0x58dcb0`): count `+0x48`, `new ushort[count]` at `+0x50` |
| `CCompositeIcon::vftable` | `0xdaf708` | ctor `0x1e2e40` (Linux `@0x58d9d0`): resrefs `+0x8` / `+0x48` / `+0x59` |
| `CArmorIcon::vftable` | `0xdaf6c0` | ctor `0x1e2ca0` (Linux `@0x58d5c0`): six resrefs `+0x48..+0xae`, count `+0xb0`, scheme `+0xb8`, per-layer table `+0xc0` |

`AurTextureFind` is a name export (`?AurTextureFind@@YAPEAVCAurTexture@@PEBD0PEAHH_N@Z`, rva
`0xe7560`); its body keys the hashed lookup on the same `+0xa8` / `+0xd4` / `+0x48` texture
fields as Linux `@0x488b90`. The item lookup reuses `CClientExoApp::GetGameObject` above plus two
`EngineLayout` rows read off `CClientExoAppInternal::GetItemByGameObjectID` (`0x6d82a0`, the
callee of `CGuiItemMenu::ActivateRadialMenu`'s first call `0x6abc80`; Linux `@0x63f3a0`):
`go+0xc == 6`, then `vtbl+0x68` (`AsNWCItem`; Linux `+0x70`). The item's icon field is
`CNWCItem+0x238` (Linux `+0x258`), read where the same `ActivateRadialMenu` hands it to `SetCenter`.

**Two ABI facts, confirmed in the bodies** (quoted in `docs/engine-layout.md`): the Windows
`CreateSubMenu` and `HandleMenuAction` invoke a callback as `(*slot.fn)(slot.obj, param)` -- no
adjust word, no virtual-thunk decode -- where Linux does `obj + adj` and the `fn & 1` vtable
lookup; and the per-slot callback stride is `0x10` against Linux's `0x18`, which is the whole
of the §1 column shift.

### Self pins (CX-694) -- the leaf handlers CX invokes on a stand-in, build `0x68e408bc`

None of these is exported. Each Windows twin was found by the `SetEntryx` tuple its creator
passes it with (icon resref, strref, param -- identical on both builds) and confirmed by body
against the Linux decompile; the two ctors were already paired above. `anchors.tsv` carries them.

| Linux | Windows rva | Evidence |
|---|---|---|
| `CGuiCreatureMenu::RadialMenuUseFeat(long)` @0x73d540 | `0x998520` | `"RadialMenuUseFeat"` / `nwccreaturemenu.cpp:0xdc9` warning, the toggle-feat ladder (0x1c, 0x13 ... 0x3b3), `PerformFeat` twin `0x805310` with `+0x7f8` / `+0x830` |
| `CGuiCreatureMenu::RadialMenuEmote(long)` @0x73dc90 | `0x997c30` | `IR_SALUTE 0x2bf param 3` / `IR_THREATEN 0x1052b param 8` tuples; ruleset `...MOTE_ANIMATIONS_USE_MDL_TIMINGS`, the sound-id table |
| `CGuiCreatureMenu::RadialMenuToggleMode(long)` @0x73dac0 | `0x9984d0` | body: quickbutton at `+0x828`, else `PerformToggleMode(creature, param, 0x7f000000)` (`0x8055c0`, the same callee `RadialMenuUseSkill` uses for skill 10) |
| `CGuiCreatureMenu::RadialMenuQuickChat(long)` @0x73d8d0 | `0x998100` | `IR_STANDGROUND 0x5c5 param 10`, `IR_HEALME 0x5cb param 4` ... (the voice commands); body `PerformQuickChat` twin `0x805440(creature, param)` |
| `CGuiCreatureMenu::RadialMenuUseSkill(long)` @0x73c030 | `0x9987d0` | `isk_pocket 0x11a param 0xd`, `isk_aniemp 0x10d param 0`; body: 10 → `PerformToggleMode(2)`, 0x16 special-cased |
| `CGuiCreatureMenu::RadialMenuRest(long)` @0x73efb0 | `0x998150` | `IR_REST 0x3b6`; body: feedback 0x36 / 0x11, `Deactivate(gui+0x78)` |
| `CGuiCreatureMenu::RadialMenuCharacterSheet(long)` @0x73bd70 | `0x996110` | `IR_CHARSHEET 0x423`; body: one call, `ToggleCharSheet` twin (`0x997e00` on the same tuple is Examine -- it reads `+0x7f8`) |
| `CClientSpellMenu::RadialMenuCast(long)` @0x6efb10 | `0x9a4540` | as before (the `PerformCastSpell` unpack) |
| `CClientSpellMenu::RadialMenuCastSpellLikeAbility(long)` @0x6efbc0 | `0x9a4610` | next function; `SetButtonSpellLikeAbility` twin on `+0x890`, `PerformCastSpellLikeAbility(spell & 0xffff, level)` |
| `CClientSpellMenu::RadialMenuUseFeat(long)` @0x6f0050 | `0x9a4770` | `"RadialMenuUseFeat"` / `guispellmenu.cpp:0x7eb`; `+0x88c` / `+0x880` |
| `CClientSpellMenu::TargetIsValid(uint)` @0x6f0450 | `0x9a4910` | `GetSpell`, `+0x898 & spell+0x50`, the type-byte ladder 5 / 6 / 0xa / 9 / 7 |
| `CGuiCreatureMenu::CGuiCreatureMenu(CNWCCreature*, Vector)` @0x73f110 | `0x98e140` | `(this, creature, Vector*)`; `sizeof` `0x840` from `operator_new` at its caller |
| `CClientSpellMenu::CClientSpellMenu(Vector, uint, CGuiQuickButton*, int)` @0x6f1650 | `0x9a10e0` | `(this, Vector*, target, btn, build)`; stores `+0x880/+0x888/+0x88c/+0x890` after the player-creature-with-stats guard |

## NUI widget cull -- RTTI vftables on build `0x68e408bc`

`Nui::*` exports nothing on Windows (the export directory holds two `Nui::JSON` destructors and no
`Nui::Layout`), so both rows are `At`. Each vftable was found from its RTTI type descriptor
(`.?AVWidget@Layout@Nui@@`, `.?AVStatefulWidget@Layout@Nui@@`) -> complete object locator -> the
pointer that precedes the vftable, the #134 recipe (`tools/abi/vtable-slots.py`).

| Linux symbol | Windows | how |
|---|---|---|
| `Nui::Layout::Widget::Render(nk_context*)` | rva `0x9528e0`; `(this, ctx) -> void` | slot 4 of both `Widget` (rva `0xe39700`) and `StatefulWidget` vftables. MSVC slot order `~dtor, SetChildren, OnAttached, OnDetach, Render, ...` is pinned by `Layoutable` slot 4 = `0x951260` (the `"trying to render Layoutable without layout group"` ExoLog) and slot 2 = `0x94f4c0` (`"Attached Layoutable twice..."`) |
| `Nui::Window::GetImageWithCache(string const&, std::function)` | rva `0x6c24b0`; `(this, sret, key, loader) -> shared_ptr<NkImage>` | an unexported function, not inlined: the three `std::function<shared_ptr<NkImage>()>` lambdas that draw a `DynamicValue<string>` resref (vftables `0xe3b140`, `0xe3b1b0`, `0xe3b5c8`, `_Do_call` rvas `0x754a70`, `0x753ac0`, `0x753df0`) all call one resref wrapper (rva `0x6c2100`, `(window, sret, char* resref, ushort type)`), which builds the key `<resref>.<type>` and calls this. Its body is the Linux one: a string-keyed table at `Window+0x180` (Linux `+0x140`), a `CExoTimers::GetMillisecondTimer` stamp on the hit |
| `_ZTVN3Nui6Layout14StatefulWidgetE` | rva `0xe6dff0` (the vftable itself; `VtblSymbolHeader` is 0 on Windows) | RTTI as above; `NuiService` checks at init that its Render slot holds `Widget::Render` |
| `_ZTVN3Nui6Layout11LayoutGroupE` / `11SingleGroupE` / `4SpanE` | rva `0xe6ce10` / `0xe6ce58` / `0xe39670` | RTTI: `vftable[-1]` -> complete object locator -> type descriptor `.?AVLayoutGroup@Layout@Nui@@` etc. `NuiLayoutErrors` tells a NUI group's solver and a span's axis by them |
| `Nui::JSON::JsonWindow::HandleError(char const*, char const*)` | rva `0x746650`; `(this, tag, message) -> void` | its own `.pdata` entry (`0x746650..0x746ee5`) holding the `"Error "` reference at `0x746a46`; its three callers (`0x74eb68`, `0xd32e5b`, `0xd3370b`) match Linux's three. Copies both strings during the call |
| `Nui::JSON::JsonWindow::JsonWindow(int, std::string, json const&)` | rva `0x730620`; `(this, id, name*, json*) -> this` | the one function storing the JsonWindow vftable with an id, a name and the window JSON: id at `+0x230`, name (a `std::string`) at `+0x238`, window JSON at `+0x258`. Each is Linux `+0x50` |
| `Nui::JSON::JsonWindow::Update()` | rva `0x74d750`; `(this) -> void` | the "unknown-bind" and "bind update for unknown bind %s, value discarded" strings. The window's own bind-update deque is an MSVC deque of pointers to 0x40-byte records at `+0x4a0` (proxy, map `+0x4a8`, map size `+0x4b0`, offset `+0x4b8`, size `+0x4c0`); a record is the window id at `+4`, the name (a `std::string`) at `+8`, the JSON at `+0x28`. The sender, `SendPlayerToServerNui_Binds` (rva `0x7c2da0`), writes the id at `+4` first, then the name and the JSON, so the id at `+4` is the window id on both builds |
| `Nui::JSON::JsonWindow::Bind(json const&)` | rva `0x7408f0`; `(this, sret DynamicBinding, json*) -> DynamicBinding*` | the "bind", "number_flags", "number_precision" and "text_flags" strings. Linux orders it `(sret, this, json*)`. `DynamicBinding` is 0x188 bytes on both builds |
| `Nui::JSON::DynamicBinding::~DynamicBinding()` | rva `0x735970`; `(this) -> void` | `Bind` calls it on its own temporary right after copying it into the return slot |
| `Nui::JSON::PushEvent` (no Linux symbol; Linux lva `0x86f680`) | rva `0x746130`; `(window*, name*, element*, index, json*) -> void` | the call `Update` makes for "unknown-bind". Copies the strings and the JSON into a 0x60-byte record on the event queue (rva `0x1423bc8`) and queues nothing for an empty element id. Linux takes the address of the window's id, Windows the window |
| `DynamicBinding::SelfUpdate(json const&)` (rva `0x74be40`, no export) | | a widget writing its own bind: guarded by a re-entrancy flag at `+0x180`, assigns the value at array index `s_array_index` and notifies. Not hooked: a bind the player changes never reaches the server's queue of updates CX reads |

## NUI `@item:` icons -- build `0x68e408bc`

`nk_loadimg(char const*, ushort)` is nuklear and unexported: rva `0xa95e30`, the only function citing both
`"nk_image: failed to load texture '%s' with ANY, no compatible format found"` and
`"nk_image: failed to load '%s'.%d, not found"`; its body makes the same `CResRef::operator=` + `CExoResMan::Get`
pair as Linux, and the `ExoLog::Emit` after it carries line `0x168` (360) on both. A free function returning
`shared_ptr` by value: `(ret, resref, type)` on both ABIs. Everything else is by name:

| Linux symbol | Windows (MSVC) |
|---|---|
| `CExoResMan::Get(CResRef const&, ushort)` (hook) | `?Get@CExoResMan@@QEAA?AV?$shared_ptr@VDataBlock@@@std@@AEBVCResRef@@G@Z` (`0x18f920`); a member returning by value: `(this, ret, ref, type)` here, `(ret, this, ref, type)` on Linux |
| `DataBlock::FromBase64(std::string const&)` | `?FromBase64@DataBlock@@SA?AV?$shared_ptr@VDataBlock@@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@3@@Z` (`0x309040`); static, `(ret, str)` on both |
| `AurGetLayeredRasterWrapper` | `?AurGetLayeredRasterWrapper@@YAPEAEPEBDPEAH11@Z` (`0xe5ef0`) |
| `AurReleaseLayeredRaster` (data) | `?AurReleaseLayeredRaster@@3P6AXPEAXHH@ZEA` (`0x140b270`) |
| `paletteimage` / `paletteheight` (data) | `?paletteimage@@3PEAEEA` (`0x140b290`) / `?paletteheight@@3HA` (`0x140b2a0`) |
| `CNWCMessage::CreateItem(uint, ushort*, uchar*, uchar*, char, uchar, ObjectVisualTransformData const&)` | not exported: **rva `0x769130`**, its own `.pdata` entry and the only function in the image citing `"gifp"` and `"gi_armor01"` (both `lea`s at `0x76a276`/`0x76a2a1`); `(this, base, models, colors, partColors, gender, vfx, vt)` in the Win64 slots, no by-value crossing |
| `ObjectVisualTransformData::s_IdentityVisualTransformData` (data) | `?s_IdentityVisualTransformData@ObjectVisualTransformData@@0U1@B` (`0x141f670`) |
| `CNWCObject::AddVisualEffect(ushort, int, uint, uchar, uchar, Vector, ObjectVisualTransformData)` | not exported: **rva `0x762180`**, `CNWCObject` vtable slot 47 (Linux 48, one fewer for the single destructor). News a 0x138-byte effect, AddTails it on the list at `this+0x80`, tests `0x163` and `0x77`. **The transform is a pointer to a caller copy that the callee erases on every return path** (frees the map's 0x2a8-byte head), so the caller makes a fresh copy per call |
| `CNWCObject::KillAllVisualEffects()` | not exported: **rva `0x763a50`**, slot 49 (Linux 50), two after Add: drains the list at `this+0x80`, calling the effect's kill, dtor and a 0x138 free |
| `std::_Tree<map<int, CAurObjectVisualTransformData>>` copy constructor | `??$?0V?$allocator@U?$_Tree_node@...` (`0x3591a0`): `(this, source, allocator&&)`, builds a new head node on the engine's heap and copies the nodes; the allocator argument is never read. The model view's way to make the copy `AddVisualEffect` destroys |
| `CExoSound::Play3DOneShotSound`, `CExoSoundSource::Play` | by name: `?Play3DOneShotSound@CExoSound@@QEAAXAEBVCResRef@@VVector@@MEIE@Z`, `?Play@CExoSoundSource@@QEAAIXZ` (muted around the add call by `vfxmute`) |
| `CNWCItem` deleting destructor | vftable rva `0xe4ceb0` (the `CNWCItem` ctor at `0x83c480` stores it at `+0`), slot 0 = `0x83c8a0`: `~CNWCItem`, then `operator delete(this, 0x2b8)` when `flags & 1`. Linux: D0 `_ZN8CNWCItemD0Ev`, slot +0x08 |

## NUI scrollbars -- nuklear statics on build `0x68e408bc`

`nk_scrollbar_behavior` and `nk_draw_scrollbar` are static on BOTH builds: Windows `At` rows, and on Linux the
same constants resolve from `lva` rows (`SymbolMap.LinuxAnchored`), since nm has no name for them either. Each
is called only by `nk_do_scrollbarv` / `nk_do_scrollbarh` (Windows `0xa65d10` / `0xa65700`, found as the two
callees of `nk_panel_end` `0xa6db10` whose float result is stored through `panel+0x20` / `+0x18`), with every
rect by pointer, so one signature serves both ABIs.

| static | Linux lva | Windows rva | how |
|---|---|---|---|
| `nk_scrollbar_behavior` | `0xdf5ba0` | `0xa6f320` | the one callee of both scrollbar functions that writes `in->mouse.buttons[0].clicked_pos` (`in+0x114/+0x118`) |
| `nk_draw_scrollbar` | `0xddf6b0` | `0xa68af0` | their other private callee; picks style items `0/0x50/0xa0` (track) and `0xf8/0x148/0x198` (thumb) by state bits `0x20/0x10` |
| `nk_do_scrollbarv` | `0xdf6100` | `0xa65d10` | `nk_panel_end`'s callee whose float result is stored through `panel+0x20`; its only other caller is `nk_do_edit` |
| `nk_do_scrollbarh` | `0xdf65c0` | `0xa65700` | the same, stored through `panel+0x18` |

`nk_do_scrollbarv/h` take the track `nk_rect` by value: in xmm0/xmm1 on Linux, through a pointer in r8 on Windows. So
`NuiScrollbarHide` declares one handler per ABI and no `[Fn]` row.

## NUI layout solve -- kiwi by name on build `0x68e408bc`

All three are name exports; the layout rows are in docs/engine-layout.md ("The kiwi solver rows").

| Linux symbol | Windows (MSVC) |
|---|---|
| `kiwi::impl::SolverImpl::substitute(Symbol const&, Row const&)` (hook) | `?substitute@SolverImpl@impl@kiwi@@AEAAXAEBVSymbol@23@AEBVRow@23@@Z` (`0x3458e0`) |
| `kiwi::impl::SolverImpl::optimize(Row const&)` (hook through native/cxthunk.c: it throws) | `?optimize@SolverImpl@impl@kiwi@@AEAAXAEBVRow@23@@Z` (`0x3433d0`) |
| `kiwi::impl::SolverImpl::createRow(Constraint const&, Tag&)` (hook; returns `unique_ptr<Row>` by value, `(this, sret, constraint, tag)`) | `?createRow@SolverImpl@impl@kiwi@@AEAA?AV?$unique_ptr@VRow@impl@kiwi@@U?$default_delete@VRow@impl@kiwi@@@std@@@std@@AEBVConstraint@3@AEAUTag@123@@Z` (`0x340e00`) |

The two builds differ in inlining, and the hook depends on it. GCC calls `substitute` from all five of its users
(addConstraint `0x85f456`, addWithArtificialVariable `0x85e0f6`, optimize `0x85d053`, dualOptimize `0x85c533`,
removeConstraint `0x85d93f`). MSVC calls it from four and **inlined it into `optimize`** (its body calls
`Row::substitute` `0x345800` per row instead), so on Windows `NuiSolver` also hooks `optimize` and rebuilds a solver's
row index after any `optimize` that can pivot. `optimize` itself is called, not inlined, from its three users on both
(addConstraint, addWithArtificialVariable, removeConstraint).

## Chat raw offsets (#419) -- decomp-diffed against `0x68e408bc`

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CClientExoAppInternal` player list ptr / count | `+0x38` / `+0x40` | same | `Internal::GetPlayerBy*` walks (`0x6d8a20`, `0x6d8910`, `0x6d89c0`, `0x6d89f0`) |
| `CClientExoAppInternal` local creature id | `+0x48` | same | `Internal::GetPlayerCreature` `0x6d8ad0` |
| `CClientExoAppInternal` module | `+0x30` | same | `Internal::GetModule` `0x6d8300` via `CClientExoApp::GetModule` |
| `CNWCPlayer` id / name / name len / creature id | `+0x8` / `+0x10` / `+0x18` / `+0x34` | same | `GetPlayerByPlayerID` `0x6d89f0`, `GetPlayerByGameObjectID` `0x6d89c0`, name walk `0x6d8a20` |
| `CNWCModule` hour / minutes-per-hour | `+0x16c` / `+0x15c` | same (`+0x15c` by adjacent layout, `+0x160..+0x170` identical) | `ModuleUpdate_Time` `0x7a58e0` |
| `CGuiInGameChatWindow` filter / shown | `+0xc58` / `+0xc68` | **`+0xae0` / `+0xaf0`** | SetFilter / Show above. Nothing reads them on Windows today |
| `CGuiInGameChatPrompt` shown / edit | `+0x230` / `+0x100` | **`+0x200` / `+0xf8`** | Show / SetFocus above |
| `CClientExoApp` internal / `CClientExoAppInternal` in-game gui | `+0x8` / `+0x90` | same | `CClientExoApp::GetInGameGui` (`0x737c70` / rva `0x6abbc0`), one line on both (#439) |
| `CGuiInGame` quickbar panel | `+0x60` | same | `CGuiInGame::Initialize` (`0x772600` / rva `0x6f9190`): the CPanelQuickBar ctor's one caller stores to `+0x60` then `+0x28 = 1` (#439) |
| `CGuiMan` viewport width / height | `+0xb8` / `+0xbc` | same | `CGuiMan::SetGuiSize` (exported; `0x67efc0` / rva `0x1f6640`) stores its two args there (#439) |

## CNWC\* layout rows Alloy reads (#446) -- decomp-diffed against `0x68e408bc`

The seven `EngineLayout` rows whose owning class has no Windows name export, so the
class sweep dumps it empty. Each was read off a function on **both** builds, using the
three anchors above.

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CNWCModule` current area | `+0xf8` | same | `CNWCCreature::UpdatePopUpBubble` (`0x72e600` / rva `0x8107a0`): dereferenced, then `area+0x118` compared against the creature's own area id |
| `CNWCArea` flags (bit 0 = interior) | `+0x8` | same | `CNWCArea::LoadArea` (`0x70e010` / rva `0x7dba70`): three `ReadINT`s into `0x2a0`/`0x2a4`/`0x2a8`, then the next `ReadDWORD` into `+8`. **Wire order is the pin** — no compiler may reorder it |
| `CNWCArea` tile width / height | `+0xc` / `+0x10` | same | `CNWCArea::LoadBorder` (`0x70a5c0` / rva `0x7dea10`): `+0xc` is the row-index multiplier, `+0x10` the row loop bound; `(height-1)*width` appears in both |
| `CNWCArea` tile array (0x60 stride) / scene | `+0x298` / `+0x248` | same | the same `LoadBorder`; `ReloadBorder` reaches the scene virtuals through `+0x248` on both |
| `CNWCAnimBase` hilite active / glow / gob | `+0x9c[ch]` / `+0xb0[ch]` / `+0xc8` | same | `RefreshHilite` (`0x9cb260` / rva `0x966170`): flags stride 4 over 5 channels, colour `+0x60[ch]` stride 12, gob the guarded pointer |

**No probe on the hilite rows.** `CNWCAnimBase::Hilite` writes the global `HiliteEnabled`
into the active flag rather than a literal `1`, and a fresh `CNWCAnimBase` has every
channel zeroed — an `Expect.Byte01` would pass on any offset that happens to read zero,
and a probe that cannot fail is not a probe.

#433's −8 virtual-destructor rule fires three more times in passing, on three
different classes: `RefreshHilite` calls **Gob** slots `0x180`/`0x188`/`0x220` (the
highlight gob at `+0xc8`, not the scene) where Windows calls `0x178`/`0x180`/`0x218`;
it calls **Scene** `AddHighlightGob`/`RemoveHighlightGob` at `0x120`/`0x128` where
Windows calls `0x118`/`0x120`; and `ReloadBorder` calls **Scene** `DeleteTile`/`SpawnTile`
at `0x58`/`0x38` where Windows calls `0x50`/`0x30`. None is a row here, but the two
`Scene` readings land on the same class as the eight measured `Scene` `vtbl.*` rows and
agree with them.

## Nameplate / combat text / dialog raw offsets (#420) -- decomp-diffed against `0x68e408bc`

| Struct / field | Linux | Windows | Where seen |
|---|---|---|---|
| `CNWCObject` id / type / pos / bubble / retired / override mode+text+len | `+0x8` / `+0xc` / `+0x38` / `+0xb8` / `+0x104` / `+0x124`,`+0x128`,`+0x130` | same | `SetPopUpBubbleText` `0x766450`, `Creature::ShowFeedbackBubbleText` `0x80b240`, `Object::UpdatePopUpBubble` `0x766ea0`, `Internal::GetObjectName` `0x6d83f0` |
| `CNWCCreature` reaction / stats / plated / area | `+0x3a8` / `+0x3d0` / `+0x468` / `+0x478` | **`+0x388` / `+0x3b0` / `+0x448` / `+0x458`** | `0x80b240`; `Creature::UpdatePopUpBubble` `0x8107a0` (`this+0x458 == module[0xf8][0x118]`) |
| `CNWCPlaceable` plated | `+0x218` | **`+0x1f8`** | `0x871b60` |
| `CNWCItem` plated (from the `CNWCObject` subobject) | `+0x2cc-0x48` | **`+0x264`** | `0x83f230` |
| `CNWCCreatureStats` first / last / override name (+len) / damage byte | `+0x20`,`+0x28` / `+0x30`,`+0x38` / `+0x40`,`+0x48` / `+0x8e` | same | `CNWCCreatureStats::GetFullName` `0x7f3f00`; `0x80b240` |
| `CGuiInGamePopUpBubble` live-text / mode / anchor x,y | `+0x14c` / `+0x150` / `+0x154`,`+0x158` | **`+0x144` / `+0x148` / `+0x14c`,`+0x150`** | `SetChatBubbleMode` `0x6e90e0`; `Show(Vector)` `0x6e9820` |
| `CNWCModule` area / camera | `+0xf8` / `+0xe8` | same | every `GetModule()` reader |
| `CNWCArea` id | `+0x118` | same | `0x8107a0` |
| `Camera` position / orientation / viewport rect | `+0xb0` / `+0xbc` / `+0x4d4` | same / same / **`+0x4d8`** | exported `Camera::GetPosition` `0x58e10`, `SetOrientation` `0x5b6d0`, `SetViewPort` `0x5b810` |
| `CClientExoAppInternal` mouse-over feedback word | `+0x180` | same | the `ui.mouseover-feedback` setter lambda `0x6bdb20` |
| `GetPortrait(uchar)` virtual slot | `+0x1d8` | **`+0x1d0`** | the chat handler `0x77c140` and `0x766450` call it `(this, sret, 1)` -- **sret is the 2nd arg on Win64** |

**Win64 by-value conventions that differ from SysV** (every hook or call on this page was checked against them):
a non-trivial class (`CExoString`) arrives as a pointer to a copy **the callee destroys** -- a skipped body must run the
dtor or it leaks (`Native.DestroyExoString`); a call site that passed a `NewExoString` sees it zeroed on return, and the
later `FreeExoString` is a null-safe no-op. A 17-byte `CResRef` arrives as a pointer to a copy (SysV: 16 bytes in memory).

## Consent modal — PORTED to Windows (decomp-verified, build `0x68e408bc`)

The client-owned yes/no consent panel (`HotkeyConsent`) runs on Windows. The four symbols
above resolve by name; `operator new` resolves by the RVA in the table. Everything was verified
against the Windows spawn path (the ExitButton `CPanelOkCancel` handler = the MSVC twin of Linux
`COptionsPanel::HandleQuitClick`). The real Linux→Windows deltas turned out small:

| Aspect | Linux | Windows | Note |
|---|---|---|---|
| `sizeof(CPanelOkCancel)` (operator new arg) | `0x3a0` | `0x360` | `HotkeyConsent.PanelSize`, per-platform |
| context field | `+0x80` | `+0x80` | **same** |
| on-close callback field | `+0x88` | `+0x88` | **same** |
| `+0x90` zero-write | present | absent | Linux-only; MSVC ctor leaves it safe (its own code omits it) |
| `Activate(msg, ok, cancel)` strrefs | `0x2052/0x2053` | `0x2052/0x2053` | **same** |
| allocator | libstdc++ `operator new` | game's own `operator new` @ RVA `0xc10324` | static CRT — see below |

Two things were the actual work, not the field offsets:

- **Allocator = the game's own `operator new`, by RVA.** `nwmain.exe` statically links the CRT
  (no CRT DLL in its import table at all — `objdump -x` shows none), so `operator new`/`delete`
  and `malloc`/`free` are internal, non-exported. The panel self-frees via the game's `operator
  delete`, so allocating from *any other* heap (the .NET runtime's, a separately-loaded CRT)
  would corrupt on free. We resolve the game's own scalar `operator new(size_t)` by RVA `0xc10324`
  — build-locked like every other RVA, so a mismatched client degrades to fail-closed, never a
  wrong-heap crash.
- **By-value `CExoString` destruction flips ABI.** `Activate` takes the message `CExoString` by
  value. Itanium destroys by-value params in the **caller**; MSVC in the **callee**. The Windows
  `?Activate@CPanelOkCancel@@QEAAXVCExoString@@II@Z` (RVA `0x200b40`) frees *and zeroes* the passed
  `CExoString` before returning, so `Native.FreeExoString`'s later `~CExoString` sees a null buffer
  → `free(null)` no-op, not a double-free. Same call site is correct on both platforms unchanged.

Earlier notes in this doc claimed Windows offsets `+0x188/+0x270/+0x278` and that `malloc`/`free`
were imported — **both were wrong** (the `0x270` was a `CClientExoApp` field in the ExitButton
handler's player-loop, misattributed; there is no CRT DLL import). Corrected here from the decomps.

**Client-native manager (nuklear) — deleted (#592).** The Hotkeys rows live in the settings panel
now; the six core `nk_*` widget symbols, `COptionsPanel::PostAttachmentInitialize`,
`HandleSaveCharButton` and `HandleXButton` left `SymbolMap` with it. `nk_sdl_render`, `g_pNuklear`,
`nk_window_is_any_hovered` and `nk_sdl_copy_event` stay (the frame host's draw site and the
NUI-typing / pointer-precedence gates), still unanchored on Windows.

**Unresolved on Windows (skip + log, graceful):**

| Linux symbol | Why skipped | Cost |
|---|---|---|
| `CClientExoApp::OnGainFocus` | empty stub, dispatched indirectly — no string/callee/caller anchor | focus gate lost; OS only delivers keys to the focused window, so redundant |
| `CClientExoApp::OnLostFocus` | same | stuck-key clear on focus loss lost; self-heals on next full press |
| `g_pNuklear` (data) | client global, not exported; data symbols aren't string-anchorable | NUI-typing gate lost; a hotkey pressed while typing in a NUI field may fire |

## Day/night machine (CX-285) -- anchored on build `0x68e408bc`

Seventeen `CNWCArea`/`CNWCModule`/`CNWCMessage` functions, none name-exported. Two seed
anchors carry the rest, and every body decompiles statement-for-statement against its named
Linux original with **every field displacement identical**.

Seeds:

- `StringRefs.java "controlpart sun gidy_sun_amb color %f %f %f"` and the `_diff` twin: one
  reference each, which is `SetGlobalLightAmbient` / `SetGlobalLightDiffuse`.
- `StringRefs.java "mainscene.fogcolor %f %f %f"`: exactly two references, `SetFog` (the one
  that also sets the clear colour) and `SetFogAmount` (the one that re-pushes the stored colour).
- `StringRefs.java "flash0%i"` + `"PNL_LIGHTNING"`: one reference each, both in `CNWCArea::Update`.
- `CNWCModule::SetTimeOfDayState` was already anchored by the `ModuleMinutesPerHour` row
  (`+0x178 = byte[+0x15c] * 60.0`).

| Linux symbol | Linux nm | Windows rva | How anchored |
|---|---|---|---|
| `CNWCModule::SetTimeOfDayState` | `0x7d96e0` | `0x7cfbb0` | the `minutes-per-hour * 60` body; state 1/2/3/4 arms match |
| `CNWCArea::SetAsDay` | `0x7064a0` | `0x7e5390` | `SetTimeOfDayState`'s state-1 callee (and state-4's first) |
| `CNWCArea::SetAsNight` | `0x7062f0` | `0x7e55f0` | its state-2 callee (and state-3's first) |
| `CNWCArea::SetAsDawn` | `0x7067a0` | `0x7e5330` | its state-3 second callee; body sets dawn-running + isNight |
| `CNWCArea::SetAsDusk` | `0x706640` | `0x7e5580` | its state-4 second callee; body sets dusk-running, clears isNight |
| `CNWCArea::SetGlobalLightAmbient` | `0x7022d0` | `0x7e5b40` | `gidy_sun_amb` string, sole reference |
| `CNWCArea::SetGlobalLightDiffuse` | `0x702510` | `0x7e5be0` | `gidy_sun_diff` string, sole reference |
| `CNWCArea::SetGlobalLightDirection` | `0x704000` | `0x7e5d10` | `SetAsDay`/`SetAsNight`'s direction callee; `9233.093`, cos 0.9998477 / 0.9961947 |
| `CNWCArea::SetFog` | `0x702380` | `0x7e5850` | `mainscene.fogcolor`, the arm that also calls `SetClearColor` |
| `CNWCArea::SetFogAmount` | `0x7026e0` | `0x7e5990` | the other `mainscene.fogcolor` reference |
| `CNWCArea::SetFogColors` | `0x704440` | `0x7e5a50` | sole callee of `HandleServerToPlayerArea` with the `-1e7` sentinels |
| `CNWCArea::SetAreaLightColor` | `0x704140` | `0x7e5010` | sole 4-slot callee of the same handler; slot offsets `0x164/0x170/0x1c8/0x1d4` |
| `CNWCArea::SetAreaLightDirection` | `0x704350` | `0x7e5250` | the handler's 2-slot direction callee; tail-calls `SetGlobalLightDirection` |
| `CNWCArea::UpdateDayNightCycle` | `0x706a20` | `0x7e90b0` | `CNWCArea::Update`'s only float callee that reads module `+0x174/+0x178` and writes `projectionalpha` |
| `CNWCArea::Update` | `0x707560` | `0x7e7da0` | `flash0%i` + `PNL_LIGHTNING`, both unique |
| `CNWCMessage::HandleServerToPlayerArea` | `0x7a79b0` | `0x76d910` | sole caller of `LoadArea` (rva `0x7dba70`), `SetAsDawn`, `SetAsDusk`, `SetAreaLightColor`, `SetFogColors` |
| `shadowalpha` (data) | `0x18aa17c` | — | name-exported: `?shadowalpha@@3MA` |

**The lightning flash has no leaf on Windows.** `CNWCArea::SetLightningDone` exists at rva
`0x7e5eb0`, but MSVC inlined its only call site into `CNWCArea::Update`, so the standalone body
is never entered; the flash start is inline in `Update` on both builds. A consumer watches the
flash from `AreaUpdate`.

Vtable slots seen in passing, all consistent with #433's −8 rule for `CNWCArea`:
`SetFog` Linux `vtbl+0x30` / Windows `+0x28`, `SetGlobalLightDiffuse` `+0x50` / `+0x40`,
`SetGlobalLightAmbient` `+0x58` / `+0x48`. All three are hooked at the body, so the slot is
provenance, not a row.

## Reading the tester's log (#421)

No debugger on the tester's side: `cx.log` (in `Documents\Neverwinter Nights\`, beside `nwn.ini`)
has to answer "which symbol is wrong" on its own. `cx.cfg` goes beside `nwn.ini` (the engine may use a
`Neverwinter Nights.0` instance folder; `cx.log` always lands in the unsuffixed one). `general.logs=1` must be set **before launch**
-- the gate closes at `settings:` and everything after is silent except the stand-down lines.

| Line | Meaning |
|---|---|
| `host: N service(s) discovered (Alloy stood down: Windows, #48)` | the DLL loaded and Windows was detected |
| `<Feature>: standing down as a unit -- <symbol>: nwmain.exe build 0x… != expected 0x68e408bc` | **rot** reaching the RVA guard. Unreachable in practice since `#457`: the build-key gate in `ServiceHost.Run` refuses the same client before discovery, so the player sees `host: client build changed, extender standing down WHOLE` and the message box instead. This line survives as the second check, not the first |
| `<Feature>: standing down as a unit -- <symbol>: no Windows anchor (by design)` | a Linux-only feature; expected, not a bug |
| `<Feature>: standing down as a unit -- <symbol>: Windows export ?…@@… missing` | the export table changed: a "By name" row is stale |
| `ChatService: inbound tap installed (AppendToMsgBuffer, Win64 CResRef* ABI)`, `NameplateService: armed`, `CombatTextService: armed`, `DialogService: hooks installed` | each tier-B unit passed its gate |
| `UiHostService: first frame presented -- hook is LIVE` | the present hook fires; the port is live |
| a crash with no line after `armed` | a hook that installs clean and faults on first call: an **offset**, not a symbol. Set `general.hookdebug=1` (funchook's trace lands in `funchook.log`) and look up the feature's row in the offset tables above |
| in-game `/nameplate probe` | one line per plate with every term of the anchor conversion (viewport source, offsets read) -- the offset probe for the plate/bubble rows |
| in-game `/hotkeys consent` | opens the consent panel on the game's own allocator and logs the answer, granting nothing -- the probe for the `operator new` RVA |

## Regenerating after a client patch

Both binaries are analyzed in the one Ghidra project `$GHIDRA_PROJECT/nwn-linux.rep`
(programs `nwmain-linux`, `nwmain.exe`). The tables above ARE the regen path: each RVA
row's "How anchored" column is the recipe for that symbol, and each offset row names
the function it was read from. `tools/update/` now does the first pass
(`docs/update-day.md`): it re-anchors every RVA it can prove and hands the rest over
as `# TODO` lines, and those are what the method below is for. The Ghidra scripts used (`~/ghidra_scripts`: TracyFn
= tracy string -> srcloc struct -> function, Qwords = MSVC vtable dump, GrepDecomp =
regex over a decompiled name range) run headless over `nwmain.exe` in the `nwn-win` project, the
way `tools/ghidra/headless.sh` runs the scripts in `tools/ghidra/`; the method is:

1. Confirm the two binaries are the same build (`build.txt`), update the build lock.
2. **String anchors** — a target referencing a unique string maps to the lone
   Windows function referencing the same string. Compiler-invariant.
3. **Anchor chaining** — from a resolved/exported neighbor (`CGuiEdit::LoseFocus`,
   `SendPlayerToServerMessage`), enumerate callers/callees; match by structure.
4. **Immediate constants** — compiler-invariant. Distinguish structural twins by the
   enum/major they pass (e.g. the chat handlers' `NotifyServerAboutGuiEvent(…,1/2,…)`).
5. **Decompile-diff** — confirm each candidate against the named Linux original.

6. Put the RVA in the build file (`docs/update-day.md`), then the boot log under Proton (`./build-win.sh &&
   ./run-proton.sh`) must show every unit `armed` and none standing down.

Names resolve directly on the Windows side after Ghidra applies the ~16k exports;
only the 50 RVA rows above need the steps.
