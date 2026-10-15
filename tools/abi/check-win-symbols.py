#!/usr/bin/env python3
"""Assert every Nm(...) row in SymbolMap._win names a real nwmain.exe export, and every At() row has an
RVA inside the image in the build file (managed/Framework/Builds/*.tsv) whose stamp is this exe's. Alloy stands down under Proton, so no Nm row is exercised at runtime
yet -- this is the only instrument that says the 142 mechanical rows of #447 (and the 39 that
predate them) are not typos.

    tools/abi/check-win-symbols.py          # exit 1 on the first bad row
Env: NWN_WIN_EXE (tools/paths.py) names the binary.
"""
import os, pathlib, re, struct, subprocess, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import paths  # noqa: E402  tools/paths.py: env, local.env, then the Steam library
EXE = paths.require("NWN_WIN_EXE")
SRC = pathlib.Path(__file__).resolve().parents[2] / "managed/Framework/SymbolMap.cs"
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "update"))
from tables import read_builds, symbols  # noqa: E402


def pe_exports(path):
    d = pathlib.Path(path).read_bytes()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec, optsz = struct.unpack_from("<H", d, pe + 6)[0], struct.unpack_from("<H", d, pe + 20)[0]
    secs = [struct.unpack_from("<III", d, pe + 24 + optsz + i * 40 + 12) for i in range(nsec)]
    size = max(va + sz for va, sz, _ in secs)

    def foff(rva):
        for va, sz, praw in secs:
            if va <= rva < va + sz:
                return praw + (rva - va)
        return None

    e = foff(struct.unpack_from("<I", d, pe + 24 + 112)[0])
    nnam = struct.unpack_from("<I", d, e + 24)[0]
    anam = struct.unpack_from("<I", d, e + 32)[0]
    names = set()
    for i in range(nnam):
        o = foff(struct.unpack_from("<I", d, foff(anam) + 4 * i)[0])
        names.add(d[o:d.index(b"\0", o)].decode())
    return names, size, struct.unpack_from("<I", d, pe + 8)[0]


# #456: a member function returning a class BY VALUE takes a hidden return buffer, and the two platforms
# order it differently -- Itanium (sret, this, ...), Win64 (this, sret, ...). Declaring one order on both
# hands the engine our own stack frame as `this`. The MSVC mangling is the only place that fact is written
# down (Itanium drops the return type), so this is the only mechanical way to find these.
#
# INSTANCE members only: @@QEAA/@@AEAA/@@IEAA (and their const forms). A free (@@YA) or static (@@SA)
# function puts sret first on BOTH platforms, so "fixing" one would be introducing the bug.
INSTANCE = re.compile(r"@@[QAI]E[AB]A")

# A row declared [Fn("sret(View this, ...)")] is handled by tools/engine/views.py, which orders the buffer
# per OS. Each row below instead had its argument order decided AT THE CALL SITE. Value = where to look.
SRET_HANDLED = {
    "GetEmitterLighting":      "managed/Alloy/Core/EmitterLighting.cs",
    "SceneGetWindVector":      "managed/Alloy/Scene/DanglyPhysics.cs (Wind), managed/Alloy/Core/AurXform.cs (Wind)",
    "ExoResManGet":            "managed/Services/Nui/NuiItemIcons.cs (OnGetItanium / OnGetWin64, a hook)",
}
SRET_DECLARED = set(re.findall(r'\[Fn\("sret\([^"]*"\)\]\s*public const string (\w+)', SRC.read_text()))


def demangle(mangled):
    out = subprocess.run(["llvm-undname", mangled], capture_output=True, text=True).stdout.splitlines()
    hit = [l.strip() for l in out if l.strip() and l.strip() != mangled]
    return hit[-1] if hit else ""


def returns_class_by_value(dem):
    """True if the demangled signature returns a class/struct by value (not a pointer or reference)."""
    if "__cdecl" not in dem:
        return False
    head = re.sub(r"^(public|private|protected):\s*", "", dem.split("__cdecl")[0].strip())
    head = re.sub(r"^(virtual|static)\s+", "", head).strip()
    return bool(re.match(r"^(class|struct)\s", head)) and not head.endswith(("*", "&"))


def check_sret(rows, at_keys=frozenset()):
    """Every instance member returning a class by value must be in SRET_HANDLED.

    The evidence is the MSVC mangling, which only an Nm row carries, so a symbol resolved by At() (an
    unexported client class) cannot be checked here either way: its by-value return was read off the Windows
    body by hand. Such a row is therefore exempt from the stale report rather than counted as a lie.
    """
    bad = 0
    seen = set()
    for key, mangled in rows:
        if not INSTANCE.search(mangled):
            continue
        dem = demangle(mangled)
        if not returns_class_by_value(dem):
            continue
        seen.add(key)
        if key not in SRET_HANDLED and key not in SRET_DECLARED:
            print(f"UNHANDLED sret ORDER  {key}\n      {dem}\n"
                  f"      Win64 passes (this, sret, ...); Itanium (sret, this, ...). Declare it\n"
                  f"      [Fn(\"sret(View this, ...)\")] in SymbolMap, which orders the buffer per OS.")
            bad += 1
    for key in (SRET_HANDLED.keys() | SRET_DECLARED) - seen - set(at_keys):
        print(f"STALE SRET_HANDLED  {key} no longer returns a class by value -- drop the entry")
        bad += 1
    print(f"{len(seen)} by-value-return row(s), {bad} bad")
    return bad


def main():
    names, size, stamp = pe_exports(EXE)
    consts, win = symbols()
    build = next((b for b in read_builds() if int(b.get("stamp", "0"), 16) == stamp), None)
    if build is None:
        print(f"NO BUILD FILE for nwmain.exe stamp {stamp:#010x} (managed/Framework/Builds/*.tsv)")
        return 1
    bad = 0
    nm = at = 0
    nm_rows = []
    for key, kind, arg in win:
        if kind == "Nm":
            nm += 1
            nm_rows.append((key, arg))
            if arg not in names:
                print(f"MISSING EXPORT  {key} -> {arg}"); bad += 1
        else:
            at += 1
            rva = build["rvas"].get(consts[key])
            if rva is None or not 0 < rva < size:
                print(f"RVA MISSING OR OUT OF IMAGE  {key} -> {rva} ({build['name']})"); bad += 1
    print(f"{nm} Nm row(s), {at} At row(s), {bad} bad")
    bad += check_sret(nm_rows, {key for key, kind, _ in win if kind != "Nm"})
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
