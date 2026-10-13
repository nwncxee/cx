#!/usr/bin/env python3
"""Emit managed/Framework/Engine/Views.g.cs: typed engine views and typed engine calls.

From EngineLayout.cs, one view per engine class. A view is a pointer-sized struct over the engine object, so
reading a pointer field AS a view is the same load as reading it as an IntPtr.
  `Field<T>(cls, f)`             -> `ref T F`
  `Field<T>(cls, f, count: n)`   -> `Span<T> F`, an array embedded in the object
  `Field<View>(cls, f, embedded: true)` -> `View F`, a record inside the object (its address, not a load)
  `Field(cls, "stride")`         -> `this[i]`, the i-th record of an array starting at this one
  `Field(cls, "vtbl.S", ..., sig: "R(args)")` -> `R S(args)`, the virtual call; every vtbl row also gets `IntPtr SFn`;
                                    `sig: "sret(args)"` returns by value into `void* ret`, ordered per OS
  untyped row                    -> nothing; typing it is the port
From SymbolMap.cs, every `[Fn("R(args)")]` symbol gets its signature declared once:
  `XFn`                          a global alias for the function-pointer type
  `EngineFn.X`                   the resolved, typed pointer (resolved on first use; null if the build lacks it)
  `hooks.InstallX(&OnX)` / `hooks.ChainX(&OnX, prio)`  typed hooks: the handler is compile-checked
  `view.X(args)`                 when the first parameter is `View this`
A view-typed parameter crosses as its pointer. `sret(View this, ...)` returns a class by value into a buffer the
caller passes first; the generated call orders it per OS (Itanium (sret, this), Win64 (this, sret) -- #456).
A parameter type that is neither a view nor a C# keyword (`VectorArg`, `Vec3*`) is a CX.Engine value type; the
`XFn` alias spells it in full, because a global using alias compiles with no usings in scope.

    tools/engine/views.py      # build.sh runs it; the output is checked in for the Windows builds
"""
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LAYOUT = sorted((ROOT / 'managed/Framework').glob('EngineLayout*.cs'))
SYMBOLS = ROOT / 'managed/Framework/SymbolMap.cs'
OUT = ROOT / 'managed/Framework/Engine/Views.g.cs'
DECL = re.compile(r'public static readonly int (\w+) =\s*Field(?:<([^>]+)>)?\(\s*"([^"]+)",\s*"([^"]+)"([^;]*)\);')
FN = re.compile(r'\[Fn\("([^"]*)"\)\]\s*public const string (\w+)')
SIG = re.compile(r'^\s*(\w[\w.]*\**)\s*\((.*)\)\s*$')
VIEW = 'CX.Engine.'
# The Span constructor is too big for the default inliner; the property is not.
INLINE = '[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]'
# Value types that live in CX.Engine but are copied, not pointed at.
VALUES = {'Quat', 'Vec3'}
# A global using alias compiles with NO usings in scope, so every name in one is spelled in full: a C#
# keyword and an already-qualified name stay, a bare name is a CX.Engine type.
PRIMS = {'void', 'bool', 'byte', 'sbyte', 'short', 'ushort', 'int', 'uint', 'long', 'ulong',
         'float', 'double', 'char', 'nint', 'nuint'}
# Classes whose objects the engine frees while we hold them (#145, #150): their views are ref structs, so a
# pointer cannot be stashed in a field or an array -- the stale read becomes a compile error (#193).
TRANSIENT = {
    # The scene graph the engine rebuilds under us. Part::~Part is the death notice every owned pointer
    # waits on (#145, #150); a gob dies with its object, a material with its model, an MdlNode and its
    # VertexPrimitive with the part that owns them, and Model is the fade owner hanging off gob+0x68.
    'Part', 'PartTriMesh', 'Gob', 'Material', 'SharedMaterial', 'VertexPrimitive',
    'MdlNode', 'MdlNodeTriMesh', 'MdlNodeSkin', 'MdlNodeDanglyMesh', 'Model',
    'PartDanglyMesh', 'VertexPrimitiveDangly',
    # CX-83: CNWCTrigger::~CNWCTrigger calls AurPartTriggerReleaseMemory, so a trigger dies with its object.
    'PartTrigger',
    # operator new recycles the 0x68-byte block and no destructor notice exists for VDOs (VdoSig).
    'VertexDataObject',
    # BORROWED: freed on area transitions AND module unload (#33, #138).
    'CAurTexture',
    # Particles: PartEmitterDtor is the death notice the particle table is hooked on.
    'PartEmitter', 'Particle',
}


def ident(s):
    return re.sub(r'\W', '_', s)


def prop(field):
    return field[0].upper() + field[1:]


def sig(text, where):
    m = SIG.match(text)
    if not m:
        sys.exit(f'!! {where}: signature "{text}" is not R(T name, ...)')
    params = []
    for p in filter(None, (s.strip() for s in m[2].split(','))):
        t, _, n = p.rpartition(' ')
        if not t or not n:
            sys.exit(f'!! {where}: parameter "{p}" needs a type and a name')
        params.append((t.removeprefix(VIEW), n))
    return m[1].removeprefix(VIEW), params


def main():
    classes = {}

    def member(cls, name, text, doc):
        members = classes.setdefault(ident(cls), {})
        if name in members or name in ('Ptr', 'IsNull', ident(cls)):
            sys.exit(f'!! {cls}: view member {name} collides; rename it at its declaration')
        members[name] = f'    /// <inheritdoc cref="{doc}"/>\n    {text}'

    rows = DECL.findall(''.join(f.read_text() for f in LAYOUT))
    slots = []
    for row, typ, cls, field, rest in rows:
        if cls.startswith('('):                 # (abi): a layout of the C++ runtime, not an engine class
            continue
        cls = ident(cls)
        if field.startswith('vtbl.'):
            name = prop(field[5:])
            member(cls, f'{name}Fn', f'public IntPtr {name}Fn => *(IntPtr*)(*(IntPtr*)Ptr + EngineLayout.{row});',
                   f'EngineLayout.{row}')
            if s := re.search(r'sig:\s*"([^"]*)"', rest):
                slots.append((cls, name, row, sig(s[1], f'{cls}.{field}')))
            continue
        if field == 'stride':                  # records laid end to end: index from this one
            member(cls, 'this[]', f'public {cls} this[int i] => new(Ptr + i * EngineLayout.{row});', f'EngineLayout.{row}')
            continue
        if not typ:
            continue
        t = typ.removeprefix(VIEW)
        if typ.startswith(VIEW) and t not in VALUES:
            classes.setdefault(t, {})
        n = re.search(r'count:\s*(\d+)', rest)
        if re.search(r'embedded:\s*true', rest):
            if t not in classes:
                sys.exit(f'!! {cls}.{field}: embedded needs a view type')
            member(cls, prop(field), f'public {t} {prop(field)} => new(Ptr + EngineLayout.{row});', f'EngineLayout.{row}')
        elif t in TRANSIENT and n:                # a Span cannot hold a ref struct: the array as its raw pointers
            member(cls, prop(field), f'public Span<IntPtr> {prop(field)} {{ {INLINE} get => new((void*)(Ptr + EngineLayout.{row}), {n[1]}); }}',
                   f'EngineLayout.{row}')
        elif t in TRANSIENT:                    # no pointer to a ref struct: a copy of the pointer, and a setter
            at = f'*(IntPtr*)(Ptr + EngineLayout.{row})'
            member(cls, prop(field), f'public {t} {prop(field)} {{ get => new({at}); set => {at} = value.Ptr; }}',
                   f'EngineLayout.{row}')
        elif n:
            member(cls, prop(field), f'public Span<{t}> {prop(field)} {{ {INLINE} get => new((void*)(Ptr + EngineLayout.{row}), {n[1]}); }}',
                   f'EngineLayout.{row}')
        else:
            member(cls, prop(field), f'public ref {t} {prop(field)} => ref *({t}*)(Ptr + EngineLayout.{row});',
                   f'EngineLayout.{row}')

    fns = [(name, sig(text, name)) for text, name in FN.findall(SYMBOLS.read_text())]
    for _, (_, params) in fns:
        if params and params[0][1] == 'this':
            classes.setdefault(ident(params[0][0]), {})
    views = set(classes)

    def raw(t):                                # the type as it crosses the ABI
        return 'IntPtr' if t in views else t

    def qual(t):                               # the same type for a global using alias: no usings in scope there
        base, stars = t.rstrip('*'), t[len(t.rstrip('*')):]
        if t in views or base == 'IntPtr':
            return 'nint' + stars
        return t if base in PRIMS or '.' in base else VIEW + base + stars

    def arg(t, n):                             # a parameter handed on as the ABI wants it
        return f'{n}.Ptr' if t in views else n

    def ret(t, call):
        return f'new {t}({call})' if t in views else call

    aliases, fn_members, hook_members = [], [], []
    for name, (r, params) in fns:
        doc = f'SymbolMap.{name}'
        member_of = ident(params[0][0]) if params and params[0][1] == 'this' else None
        if r == 'sret':
            # The buffer is the caller's first argument; where it goes depends on the ABI, so no single
            # function-pointer type describes this symbol and it cannot be hooked through the typed surface.
            if not member_of:
                sys.exit(f'!! {name}: sret needs a `this` (a free function has one order on both ABIs)')
            rest = params[1:]
            lin = ', '.join(['IntPtr', 'IntPtr'] + [raw(t) for t, _ in rest] + ['void'])
            decl = ', '.join(['void* ret', f'IntPtr self'] + [f'{raw(t)} {n}' for t, n in rest])
            tail = ''.join(f', {n}' for _, n in rest)
            fn_members.append(
                f'    private static IntPtr _{name};\n    private static bool _{name}Done;\n'
                f'    /// <inheritdoc cref="{doc}"/>\n'
                f'    public static void {name}({decl})\n    {{\n'
                f'        IntPtr fn = _{name}Done ? _{name} : Resolve(ref _{name}, ref _{name}Done, {doc});\n'
                f'        if (OperatingSystem.IsWindows()) ((delegate* unmanaged<{lin}>)fn)(self, (IntPtr)ret{tail});\n'
                f'        else ((delegate* unmanaged<{lin}>)fn)((IntPtr)ret, self{tail});\n    }}')
            mdecl = ', '.join(['void* ret'] + [f'{t} {n}' for t, n in rest])
            member(member_of, name, f'public void {name}({mdecl}) => EngineFn.{name}(ret, Ptr'
                   + ''.join(f', {arg(t, n)}' for t, n in rest) + ');', doc)
            continue
        ptr = f'delegate* unmanaged<{", ".join([raw(t) for t, _ in params] + [raw(r)])}>'
        aliases.append(f'global using unsafe {name}Fn = '
                       f'delegate* unmanaged<{", ".join([qual(t) for t, _ in params] + [qual(r)])}>;')
        fn_members.append(
            f'    private static IntPtr _{name};\n    private static bool _{name}Done;\n'
            f'    /// <inheritdoc cref="{doc}"/>\n'
            f'    public static {name}Fn {name} => ({name}Fn)(_{name}Done ? _{name} : Resolve(ref _{name}, ref _{name}Done, {doc}));')
        hook_members.append(
            f'    /// <summary>Hooks <see cref="{doc}"/>; returns the original, or null.</summary>\n'
            f'    public static {name}Fn Install{name}(this HookService h, {name}Fn handler) =>\n'
            f'        ({name}Fn)h.Install({doc}, (IntPtr)handler);\n'
            f'    /// <summary>Chains onto <see cref="{doc}"/>; returns the slot holding the next layer, or null.</summary>\n'
            f'    public static {name}Fn* Chain{name}(this HookService h, {name}Fn handler, int priority) =>\n'
            f'        ({name}Fn*)h.Chain({doc}, (IntPtr)handler, priority);')
        if member_of:
            rest = params[1:]
            mdecl = ', '.join(f'{t} {n}' for t, n in rest)
            call = f'EngineFn.{name}(' + ', '.join(['Ptr'] + [arg(t, n) for t, n in rest]) + ')'
            member(member_of, name, f'public {r} {name}({mdecl}) => {ret(r, call)};', doc)

    for cls, name, row, (r, params) in slots:
        if r == 'sret':
            lin = ', '.join(['IntPtr', 'IntPtr'] + [raw(t) for t, _ in params] + ['void'])
            mdecl = ', '.join(['void* ret'] + [f'{t} {n}' for t, n in params])
            tail = ''.join(f', {arg(t, n)}' for t, n in params)
            member(cls, name, f'public void {name}({mdecl})\n    {{\n'
                   f'        var fn = (delegate* unmanaged<{lin}>){name}Fn;\n'
                   f'        if (OperatingSystem.IsWindows()) fn(Ptr, (IntPtr)ret{tail});\n'
                   f'        else fn((IntPtr)ret, Ptr{tail});\n    }}', f'EngineLayout.{row}')
            continue
        ptr = f'delegate* unmanaged<{", ".join(["IntPtr"] + [raw(t) for t, _ in params] + [raw(r)])}>'
        mdecl = ', '.join(f'{t} {n}' for t, n in params)
        call = f'(({ptr}){name}Fn)(' + ', '.join(['Ptr'] + [arg(t, n) for t, n in params]) + ')'
        member(cls, name, f'public {r} {name}({mdecl}) => {ret(r, call)};', f'EngineLayout.{row}')

    out = ['// <auto-generated> by tools/engine/views.py from EngineLayout.cs and SymbolMap.cs -- do not edit. </auto-generated>',
           *aliases, 'using System;', 'using CX.Framework;', '', 'namespace CX.Engine;', '']
    for cls in sorted(classes):
        kind = 'ref partial struct' if cls in TRANSIENT else 'partial struct'
        out += [f'/// <summary>Typed view of an engine <c>{cls}*</c>.</summary>',
                f'public readonly unsafe {kind} {cls}(IntPtr ptr)', '{',
                '    /// <summary>The engine object.</summary>',
                '    public readonly IntPtr Ptr = ptr;',
                '    /// <summary>No object.</summary>',
                '    public bool IsNull => Ptr == IntPtr.Zero;']
        out += [classes[cls][m] for m in sorted(classes[cls])]
        out += ['}', '']
    out += ['/// <summary>Engine functions, typed by their <c>[Fn]</c> signature in <see cref="SymbolMap"/>.</summary>',
            'public static unsafe class EngineFn', '{',
            '    // Zero-initialised fields keep the class free of a static constructor, so the getters inline;',
            '    // the first-use lookup stays out of line.',
            '    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]',
            '    private static IntPtr Resolve(ref IntPtr slot, ref bool done, string symbol)',
            '    {', '        slot = Native.Sym(symbol);', '        done = true;', '        return slot;', '    }',
            *fn_members, '}', '',
            '/// <summary>Typed hook installs, one pair per <c>[Fn]</c> symbol.</summary>',
            'public static unsafe class EngineHooks', '{', *hook_members, '}', '']
    text = '\n'.join(out)
    if not OUT.exists() or OUT.read_text() != text:
        OUT.parent.mkdir(exist_ok=True)
        OUT.write_text(text)
        print(f'>> wrote {OUT.relative_to(ROOT)} ({len(classes)} views, {len(fns)} functions)')


if __name__ == '__main__':
    main()
