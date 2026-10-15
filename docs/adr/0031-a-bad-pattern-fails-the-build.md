# 0031 — A bad pattern fails the build: compiler rules, banned calls, and no pointer plus an offset

Status: accepted (2026-10-15). Origin: xtul9's review of PR #53.

## Context

A review on PR #53 found `*(ulong*)(q + MsvcSize)`: a pointer plus a named offset, read in place. Naming the
offset is not enough. The place is a field nobody declared, and every reader has to know the layout behind it.
The `engine-views` gate refused this only for an `EngineLayout` row that already had a view member, so a local
`const`, a literal or an untyped row went through.

A gate is a script reading text. It misses a pointer moved two lines earlier, and it says nothing in the
editor. The compiler sees types, so it can tell a pointer plus an integer from a sum of two integers.

## Decision

1. **A rule the compiler can check is a compiler error.** A call that must go through one helper is a line in
   `managed/BannedSymbols.txt` (RS0030, from Microsoft's BannedApiAnalyzers). A pattern that is no single
   symbol is a rule in `tools/analyzers/`, the repository's own analyzer. `managed/.editorconfig` makes both
   errors. A gate is for what the compiler cannot see: comments, file sizes, shaders, docs.
2. **CX0001: only `managed/Framework/` adds an offset to a pointer.** Anywhere else, `p + n`, `p - n`,
   `p += n` and `p++` on a pointer or an `IntPtr` fail the build, and so do `(nint)p + n`, `((float*)&v)[i]`, `&p[i]` and
   `Unsafe.AsPointer(ref p[i])`. Read engine memory through a field on a view
   (`Field<T>` rows and `Views.g.cs`). Give other memory a container of its own: a view under
   `Framework/Engine/`, an unmanaged struct, or a `Span<T>`. Reading a value as `px[i]` is allowed. A bounds-checked span is a container too: slicing one, pinning the slice
   for a native call and reading a file format out of it with `BinaryPrimitives` are allowed.
3. **CX0002: no `fixed` buffers.** A fixed buffer decays to a pointer, so a record made of one is read at indices
   written by hand. Name the fields, or use an `[InlineArray]` of a named element type.
4. **The calls that do the same thing without the `+` are banned.** `Marshal.Read*`/`Write*` with an offset,
   `Marshal.PtrToStructure`, `IntPtr.Add`/`Subtract` and `Unsafe.Add`/`Subtract`/`AddByteOffset` are lines in
   `BannedSymbols.txt`, every overload.
5. **A ban is added the day its helper exists.** A change that writes a helper meant to be the only way to
   make a call adds the ban line in the same change and moves every caller onto it. A review finding of the
   kind "this should have gone through X" asks for the ban line or the analyzer rule that would have caught
   it.
6. **One legal home, with its reason.** Only the helper a ban points at may disable it, under `managed/Framework/`, with
   `#pragma warning disable RS0030 // <why>`. A pragma anywhere else is refused in review.

## Consequences

- xtul's finding and the 1108 sites like it are ported to views, structs and spans (CX-282). The rule is an
  error, with no baseline.
- Every new hook or engine read declares its row and reads the field. The `engine-views` gate keeps its
  `not-an-offset:` check.
- `tools/analyzers/` is a build input, so CI builds when it changes. The Windows job compiles the `#if WINDOWS`
  code, so a Windows-only site fails there.
- The same list bans the other calls with a helper: `Thread.Sleep` goes through `BlockingWait.Sleep`, which takes
  the reason the thread may block. Blocking on a `Task` is banned outright, because the extender has no async
  path. `Environment.GetEnvironmentVariable` goes through `HostEnv`.
