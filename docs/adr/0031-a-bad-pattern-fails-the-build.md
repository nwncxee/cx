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
   `p += n` and `p++` on a pointer or an `IntPtr` fail the build, and so do `&p[i]` and `Unsafe.AsPointer(ref p[i])`. Read engine memory through a field on a view
   (`Field<T>` rows and `Views.g.cs`). Give other memory a container of its own: a view under
   `Framework/Engine/`, an unmanaged struct, or a `Span<T>`. Reading a value as `px[i]` is allowed.
3. **The calls that do the same thing without the `+` are banned.** `Marshal.Read*`/`Write*` with an offset,
   `Marshal.PtrToStructure`, `IntPtr.Add`/`Subtract` and `Unsafe.Add`/`AddByteOffset` are lines in
   `BannedSymbols.txt`.
4. **A ban is added the day its helper exists.** A change that writes a helper meant to be the only way to
   make a call adds the ban line in the same change and moves every caller onto it. A review finding of the
   kind "this should have gone through X" asks for the ban line or the analyzer rule that would have caught
   it.
5. **One legal home, with its reason.** Only the helper a ban points at may disable it, with
   `#pragma warning disable RS0030 // <why>`. A pragma anywhere else is refused in review.

## Consequences

- xtul's finding and the 1108 sites like it are ported to views, structs and spans (CX-282). The rule is an
  error, with no baseline.
- Every new hook or engine read declares its row and reads the field. The `engine-views` gate keeps its
  `not-an-offset:` check.
- `tools/analyzers/` is a build input, so CI builds when it changes.
