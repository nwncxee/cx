# `DbAnim` — the animation/transform API the Aurora seam calls (CX-120 seam-anim)

Everything that **moves a record on the game thread** under the scene database lives behind
`CX.Alloy.Db.DbAnim`. A `CAurObject` / `CAurScene` slot implementation is a handle resolve plus one call into
this file; nothing else creates or advances animation state.

Declared in `Db/DbAnim.Api.cs` (the surface), implemented across `Db/DbAnim.*.cs` and `Db/DbXform.cs`.

## Contract

- **Game thread only.** Every entry assumes the DB's single writer (ADR 0028 §5). Nothing here is safe from
  the render thread.
- **Signatures mirror the engine's.** Same arguments in the same order as the slot
  (`tools/seam/aurora-seam-vtables.tsv`), with a `DbId` where the engine passes `this` and a `ModelDef`
  where it passes a `Model*`.
- **Setters mark their own records.** A caller never has to `Touch`.
- **Allocation-free on the steady path.** The run queue, the callbacks and the dynamic vertex buffers are
  pre-sized pools; a refusal is logged once and counted on `[dbanim]`, never silent.

## What seam-obj calls, per slot

| slot | call |
|---|---|
| 6 `Gob::Animate` / `CAurObject::AnimateExternal` | `AnimateExternal(obj, dt)` |
| 7 `PlayAnimation(name, speed, flags, fade)` | `PlayAnimation(obj, name, speed, flags, fade)` → the engine's int |
| 8 `GetAnimation(name, &len, &trans)` | `GetAnimation(obj, name, out len, out trans)` → an opaque non-null token (zeroed; never dereference it as an `Animation*`) |
| 9 / 10 add-in animations | `LoadAddInAnimations(obj, resref)` / `RemoveAddInAnimations(obj)` |
| 11 / 83 / 84 the event path | `RegisterCallBack(obj, fn, name, data)` → a real engine-layout `CallBack*`; `DeregisterCallback(obj, cb)`; `DoEvent(obj, name, data)` |
| 57–60 / 85 the gates | `EnableForceAnimations` / `DisableForceAnimations` / `EnableAnimations` / `DisableAnimations` / `IsAnimating` / `IsForceAnimated` |
| 74 / 75 look-at | `BeginLookAtAnimate(obj, target, headPart, targetPart, rate, yawLimit, pitchLimit, weight)` / `EndLookAtAnimate(obj)` |
| 78 synchronise | `SetSynchronizeAnimations(obj, on)` |
| `Gob::SetBehavior` / `GetBehavior` | `SetBehavior(obj, beh)` / `Behavior(obj)` (the **type test** stays in the slot: it runs through the behaviour's own `AsBehavior*` virtuals) |
| `Part::GetWorld` on a facade part | `PartGetWorld(part, out pos, out rot)` |
| `Gob::GetPart(name)` | `PartByName(obj, name)` → an absolute part index |
| `GetPosition` / `GetOrientation` and the other by-value getters | `ObjectWorld(obj)` / `PartWorld(part)` — both **compose on demand**, so a getter straight after a setter answers the new value |
| object creation / retirement / tree change | `ObjectCreated(obj)` / `ObjectRetired(obj)` / `TreeChanged(obj)` |
| LOD | `SetLodModels(obj, l1, l2)` / `SetLodDistances(obj, near, far)` |
| `Gob::AnimateOffScreen` | `SetAnimateOffScreen(obj, on)` |
| animation speed / visual scale (`Gob+0x400` / `+0x1c0`) | `SetAnimationSpeed(obj, speed, overSeconds)` / `SetVisualScale(obj, scale, overSeconds)` |

seam-scene calls `DbAnimCamera.Animate(cam, dt)` from the camera's `Animate` slot (its stub is folded in and
deleted), `EnableSceneAnimations` / `DisableSceneAnimations` / `IsSceneAnimating`, `SetEye(eye)` once
per view request, and `Animate(scene, dt)` from the `CAurScene::Animate` slot.

## Four hooks seam-obj MUST install at boot

These are function-pointer fields on `DbAnim`, declared here because the behaviour solve needs them and the
facade's interior is seam-obj's. Each unset one logs once, loudly, and names what stops working:

```csharp
DbAnim.ResolvePart   = &MyPartOfFacade;   // CAurPart* (facade interior) -> absolute part index, -1
DbAnim.ShellPoseSync = &MySyncPose;       // read the shell's +0xa8/+0xb4 back into ObjPos/ObjRot
DbAnim.DestroyObject = &MyDestroy;        // the facade's deleting-destructor path (DieWithoutParent)
DbAnim.GuiScale      = &MyGuiScale;       // CAurora::GetGUIScale, for the GUI-scaled attach
```

`ShellPoseSync` is not optional for GUI attachments: a **game-side** `CAurBehavior` (the cross-scene attach,
the subwindow) writes the object's placement into the shell by interior address through `Part::GetWorld`, so
without the sync those columns never learn about it.

## What seam-feed reads

- `SceneDb.PartWorld`, `PartPos/Rot/Scale`, `PartAlpha`, `PartSelfIllum` — written by the pose and transform
  steps, with the part marked `Dirty.Anim` / `Dirty.Material`.
- `SceneDb.ObjWorld` **and `ObjSphere`** — written together by the transform step.
- `SceneDb.PartBoneBase` — the ABSOLUTE first bone of this part's palette in the renderer's bone stage, or -1:
  `slot * 64` for a skin, which owns its slot; a cell inside a shared slot for a rigged dangly part.
- `SceneDb.ObjLodShown` — the part-tree alternate the LOD choice picked.
- Nothing more for a STOCK-solved dangly part or an animmesh: such a part carries its own arena range and its own interned recipe in
  `SceneDb.PartRecipe`, so the existing record path draws the solved vertices. `DbAnim.DanglyPositions(part,
  out count)` + `DbAnim.DynGeneration(part)` expose the host copy for a consumer that wants the positions
  themselves. Layout and ownership: `Db/DbPose.LAYOUT.md`.
- `DbAnim.ChannelSink` — set it to receive the controller tracks the pose columns do not carry (light colour,
  radius, multiplier; emitter rates). Unset, those tracks are sampled by nobody and counted.
