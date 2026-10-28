# Coding standards

The rules every change to `managed/` follows. A rule the compiler can check is a build error (ADR 0031), and the
rest are checked in review. `CLAUDE.md` holds the workflow, the comment rule, the engine-memory rule, the Windows
rule and the gates.

## A bad pattern fails the build

- **A rule the compiler can check is a compiler error.** A call that must go through one helper is a line in
  `managed/BannedSymbols.txt` (RS0030). A pattern that is no single symbol is a rule in `tools/analyzers/`.
  `managed/.editorconfig` makes both errors. A gate is for what the compiler cannot see.
- **Ban a call the day its helper exists.** A helper meant to be the only way to make a call adds its ban line in
  the same change and moves every caller onto it.
- **A review finding gets its ban.** A finding of the kind "this should have gone through X" or "this should
  never appear" gets a ban line or an analyzer rule in the change that fixes it.
- **Prove a new ban once.** A planted call fails the build in the change that adds the line or the rule. A member
  line with no parameter list bans one overload only, so ban each overload, or the type.
- **One legal home.** Only the helper a ban names disables RS0030, under `managed/Framework/`, with
  `#pragma warning disable RS0030 // <why>`. CX rules are never switched off.
- **No tests that read source text.** A rule a test would grep for is a ban line or an analyzer rule instead.

## A place in memory has a name

- **Only `managed/Framework/` adds an offset to a pointer (CX0001).** Elsewhere the build refuses `p + n`, `p - n`,
  `p += n`, `p++`, `(nint)p + n`, `(long)p`, `((float*)&v)[i]`, `((float*)p)[k]`, `&p[i]` and
  `Unsafe.AsPointer(ref p[i])`. Reading a value as `px[i]` on a pointer of the right type is fine.
- **Engine memory reads through a view.** Declare the row (`Field<T>`, both OS values in the build file) and read
  the generated member.
- **Our own memory is a record.** Use a struct of named fields (`Vector3`, `Matrix4x4`, a nested struct), or an
  `[InlineArray]` of a named element for N like things indexed by a real index.
- **No blobs.** A generic `StrideN` struct, an `[InlineArray]` of bytes or floats read at hand-picked indices, or a
  record kept as lanes of a flat array (`a[i * 3 + k]`) is a field nobody named. `fixed` buffers fail the build
  (CX0002).
- **One layout, one declaration.** A hand-written struct does not restate a generated view, and two structs do
  not describe one GPU block.
- **A Framework container holds layout, not logic.** No Framework method takes a layout offset, a stride or a
  header size as a parameter: the offset comes from the type (a field, `sizeof(T)`, a view row). An offset the
  memory itself stores, such as nuklear's command `next`, is data and may be passed.
- **Only `managed/Framework/` builds a span from a raw pointer.** A view builds one from its count row, and a
  `NativeBuffer<T>` from its allocation. `new Span<T>(ptr, n)` elsewhere checks nothing, because the caller wrote
  `n`. CX-284 bans it; until then, review refuses a new one. Slicing, pinning and `BinaryPrimitives` over an
  owner's span are fine.

## Threads and the host

- **Only the files CX0003 lists issue GL.** Alloy renders on Vulkan; elsewhere the build refuses a `Gl.*` call
  or a captured entry point, and constants, `Gl.Init` and a null check are fine. The list is the bindings, CX's
  own UI windows on the vanilla renderer, the engine's model views and texture path, and the uniform shim with
  its callers; it lives in `tools/analyzers/EngineGl.cs` and `tools/gates/gl-confined.sh` keeps it honest.
- **A thread blocks through `BlockingWait.Sleep(ms, why)`.** The game's main thread never blocks.
- **No tasks.** The extender has no async path, so the `Task` and `ValueTask` types are banned.
- **The environment is read through `HostEnv`.** A setting is a knob, never an environment variable (ADR 0025).
