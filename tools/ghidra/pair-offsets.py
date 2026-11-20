#!/usr/bin/env python3
"""Pair Linux and Windows struct-field displacements from two OffsetDump.java TSVs.

    tools/ghidra/pair-offsets.py lin.tsv win.tsv --pairs anchors.tsv --alias aliases.tsv \
                                 --gt ground-truth.tsv --out candidates.csv [--top 3]

Function pairing is by demangled identity (namespace, name, parameter count); the `--pairs`
file adds hand-anchored pairs for the classes MSVC does not export by name, one per line:

    CNWCCreature::ShowFeedbackBubbleText <TAB> 14080b240

Every row of the output is a CANDIDATE, never a verified offset. Confirm each against live
memory with a boot-time probe in cx.log before any code reads it.
"""
import argparse, collections, csv

# How far apart two offsets may be and still be considered the same field.
MAX_DELTA = 0x400
# Locality half-width: evidence this many bytes away from the offset counts half.
LOCALITY = 128
# Scores this close count as tied, and body-order agreement picks the winner.
TIE_EPSILON = 0.1


def read_dump(path):
    d = dict(meta={}, funcs={}, acc=collections.defaultdict(list),
             vsl=collections.defaultdict(list), types=collections.defaultdict(dict),
             counters={})
    with open(path) as fh:
        for line in fh:
            f = line.rstrip("\n").split("\t")
            if f[0] == "F":
                d["funcs"][f[1]] = dict(entry=f[1], cls=f[2], name=f[3], np=int(f[4]),
                                        cc=f[5], status=f[6], thisreg=f[7],
                                        this=int(f[8]) if len(f) > 8 else 0)
            elif f[0] == "A":
                d["acc"][f[1]].append((int(f[2]), int(f[3]), int(f[4]), int(f[6])))
            elif f[0] == "V":
                d["vsl"][f[1]].append((int(f[2]), int(f[3])))
            elif f[0] == "T":
                d["types"][f[1]][int(f[2])] = f[3]
            elif f[0] == "S":
                d["counters"][f[1]] = int(f[2])
            elif f[0] == "P":
                d["meta"] = dict(program=f[1], imagebase=int(f[2], 16), compiler=f[3])
    return d


def accesses(dump, entry, param):
    """Distinct displacements off one parameter: occurrence count, access sizes, first order."""
    seen = {}
    for (o, p, total, size) in sorted(dump["acc"].get(entry, [])):
        if p != param:
            continue
        e = seen.setdefault(total, dict(count=0, sizes=set(), first=o))
        e["count"] += 1
        if size:
            e["sizes"].add(size)
    return seen


def this_index(dump, entry, f):
    """Which parameter is really the object pointer.

    `HighSymbol.isThisPointer()` is only set where a prototype was applied; where it is not,
    the F record falls back to 0, which is wrong for a SysV body whose first slots went to a
    by-value aggregate (`CGuiInGamePopUpBubble::Show(Vector)` carries `this` in parameter 2).
    The parameter typed as a pointer to the function's own class settles it.
    """
    for i, tn in sorted(dump["types"].get(entry, {}).items()):
        if tn == f["cls"]:
            return i
    n = collections.Counter(p for (_, p, _, _) in dump["acc"].get(entry, []))
    if f["this"] >= 0 and not n[f["this"]] and n:
        return n.most_common(1)[0][0]        # untyped body: the parameter it actually reads
    return f["this"]


def param_map(lf, wf):
    """Linux parameter index -> Windows parameter index, pinning `this` to `this`.

    Win64 puts the hidden struct-return pointer AFTER `this`, SysV puts it before, so the
    raw index of the object pointer differs whenever the function returns a class by value.
    """
    lt, wt = lf["_this"], wf["_this"]
    m = {}
    if 0 <= lt < lf["np"] and 0 <= wt < wf["np"]:
        m[lt] = wt
    lo = [i for i in range(lf["np"]) if i != lt]
    wo = [i for i in range(wf["np"]) if i != wt]
    for a, b in zip(lo, wo):
        m[a] = b
    return m


def build_pairs(lin, win, extra):
    def index(d):
        by_key = collections.defaultdict(list)
        for e, f in d["funcs"].items():
            if f["cls"] == "-":
                continue
            by_key[(f["cls"], f["name"], f["np"])].append(e)
        return by_key

    li, wi = index(lin), index(win)
    pairs, stats = [], collections.Counter()
    for k, le in li.items():
        we = wi.get(k)
        if not we:
            stats["no_windows_name"] += 1
            continue
        if len(le) > 1 or len(we) > 1:
            stats["ambiguous_overload"] += 1
            continue
        pairs.append((le[0], we[0], "name"))
        stats["name_paired"] += 1

    by_name = collections.defaultdict(list)
    for e, f in lin["funcs"].items():
        by_name[f["cls"] + "::" + f["name"]].append(e)
    for qual, went in extra.items():
        cands = by_name.get(qual, [])
        if not cands:
            stats["anchor_linux_missing"] += 1
            continue
        if len(cands) > 1:
            stats["anchor_linux_ambiguous"] += 1
            continue
        if went not in win["funcs"]:
            stats["anchor_windows_missing"] += 1
            continue
        pairs.append((cands[0], went, "anchor"))
        stats["anchor_paired"] += 1
    return pairs, stats


def align(lmap, wmap):
    """Order-preserving alignment of two function bodies' field touches.

    The same source compiled twice touches its fields in the same order, so the n-th distinct
    displacement on one side is usually the n-th on the other. A plain zip only works when both
    sides touch the same number of fields; this is the gapped version (Needleman-Wunsch), which
    is what disambiguates a run of equal-sized fields -- a float array or a matrix -- where
    delta voting alone cannot tell +4 from +8.
    """
    lo = sorted(lmap, key=lambda k: lmap[k]["first"])
    wo = sorted(wmap, key=lambda k: wmap[k]["first"])
    if not lo or not wo or len(lo) * len(wo) > 40000:
        return []
    GAP = -0.6
    n, m = len(lo), len(wo)
    dp = [[0.0] * (m + 1) for _ in range(n + 1)]
    bt = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(1, n + 1):
        dp[i][0] = dp[i - 1][0] + GAP; bt[i][0] = 1
    for j in range(1, m + 1):
        dp[0][j] = dp[0][j - 1] + GAP; bt[0][j] = 2
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            sl, sw = lmap[lo[i - 1]]["sizes"], wmap[wo[j - 1]]["sizes"]
            sc = dp[i - 1][j - 1] + (1.0 if size_ok(sl, sw) else -1.0)
            best, b = sc, 0
            if dp[i - 1][j] + GAP > best: best, b = dp[i - 1][j] + GAP, 1
            if dp[i][j - 1] + GAP > best: best, b = dp[i][j - 1] + GAP, 2
            dp[i][j], bt[i][j] = best, b
    out, i, j = [], n, m
    while i > 0 and j > 0:
        if bt[i][j] == 0:
            if size_ok(lmap[lo[i - 1]]["sizes"], wmap[wo[j - 1]]["sizes"]):
                out.append((lo[i - 1], wo[j - 1]))
            i -= 1; j -= 1
        elif bt[i][j] == 1: i -= 1
        else: j -= 1
    return out


class Evidence:
    def __init__(self):
        self.l = collections.defaultdict(collections.Counter)      # class -> off -> occurrences
        self.w = collections.defaultdict(collections.Counter)
        self.sl = collections.defaultdict(lambda: collections.defaultdict(set))
        self.sw = collections.defaultdict(lambda: collections.defaultdict(set))
        self.pos = collections.defaultdict(collections.Counter)    # class -> (l, w) -> votes
        self.fns = collections.defaultdict(set)

    def add(self, cls, lmap, wmap, tag=None):
        self.fns[cls].add(tag)
        for off, d in lmap.items():
            self.l[cls][off] += d["count"]
            self.sl[cls][off] |= d["sizes"]
        for off, d in wmap.items():
            self.w[cls][off] += d["count"]
            self.sw[cls][off] |= d["sizes"]
        for a, b in align(lmap, wmap):
            self.pos[cls][(a, b)] += 1

    def merge(self, base, src):
        """Fold a derived class's evidence into its base -- single inheritance at offset 0."""
        for store, other in ((self.l, self.l), (self.w, self.w)):
            store[base].update(other[src])
        for store in (self.sl, self.sw):
            for k, v in store[src].items():
                store[base][k] |= v
        self.pos[base].update(self.pos[src])
        self.fns[base] |= {("via", src, x) for x in self.fns[src]}


def collect(lin, win, pairs):
    fields, vslots = Evidence(), Evidence()
    fnpairs = collections.defaultdict(int)
    for le, we, how in pairs:
        lf, wf = lin["funcs"][le], win["funcs"][we]
        lf["_this"] = this_index(lin, le, lf)
        wf["_this"] = this_index(win, we, wf)
        pm = param_map(lf, wf)
        for lp, wp in pm.items():
            lmap, wmap = accesses(lin, le, lp), accesses(win, we, wp)
            if not lmap or not wmap:
                continue
            if lp == lf["_this"]:
                cls = lf["cls"]
            else:
                cls = lin["types"].get(le, {}).get(lp)      # attribute by what the pointer points AT
                if not cls or cls == lf["cls"]:
                    continue
            fields.add(cls, lmap, wmap, (le, we, lp))
            fnpairs[cls] += 1
        lv = collections.Counter(s for (p, s) in lin["vsl"].get(le, []) if p == lf["_this"])
        wv = collections.Counter(s for (p, s) in win["vsl"].get(we, []) if p == wf["_this"])
        if lv and wv:
            vslots.add(lf["cls"],
                       {k: dict(count=v, sizes=set(), first=k) for k, v in lv.items()},
                       {k: dict(count=v, sizes=set(), first=k) for k, v in wv.items()},
                       (le, we))
    return fields, vslots, fnpairs


def size_ok(a, b):
    return (not a) or (not b) or bool(a & b)


def candidates(ev, kind, top, max_delta=MAX_DELTA):
    rows = []
    for cls, lcnt in sorted(ev.l.items()):
        wcnt = ev.w.get(cls)
        if not wcnt:
            continue
        loffs, woffs = sorted(lcnt), sorted(wcnt)
        wset = set(woffs)
        for l in loffs:
            scored = []
            for d in {w - l for w in woffs if abs(w - l) <= max_delta}:
                w = l + d
                if not size_ok(ev.sl[cls].get(l, set()), ev.sw[cls].get(w, set())):
                    continue
                s = 0.0
                for l2 in loffs:
                    if l2 + d in wset and size_ok(ev.sl[cls].get(l2, set()),
                                                 ev.sw[cls].get(l2 + d, set())):
                        s += min(lcnt[l2], wcnt[l2 + d]) / (1.0 + abs(l2 - l) / LOCALITY)
                if d == 0:
                    s *= 1.05        # identical layout is the prior; break exact ties toward it
                scored.append((s, d, w, ev.pos[cls].get((l, w), 0)))
            # Delta evidence decides; body-order agreement only breaks a near-tie, which is where
            # it earns its keep (a run of equal-sized fields votes equally for every stride).
            scored.sort(key=lambda t: (-round(t[0], 1), -t[3], abs(t[1])))
            for rank, (s, d, w, _) in enumerate(scored[:top], 1):
                rows.append(dict(cls=cls, kind=kind, linux=l, windows=w, delta=d, rank=rank,
                                 score=round(s, 2),
                                 margin=round(s - scored[1][0], 2) if rank == 1 and len(scored) > 1 else "",
                                 fns=len(ev.fns[cls]), seen_l=lcnt[l], seen_w=wcnt[w]))
    return rows


def evaluate(rows, gt_path, top):
    gt = []
    with open(gt_path) as fh:
        for line in fh:
            if not line.strip() or line.startswith("#"):
                continue
            cls, l, w, kind, note = (line.rstrip("\n").split("\t") + ["", ""])[:5]
            gt.append((cls, int(l, 16), int(w, 16), kind or "field", note))
    by_key = collections.defaultdict(list)
    for r in rows:
        by_key[(r["cls"], r["kind"], r["linux"])].append(r)
    hit1 = hitn = wrong = absent = 0
    print("\n== ground truth ==")
    for cls, l, w, kind, note in gt:
        cands = sorted(by_key.get((cls, kind, l), []), key=lambda r: r["rank"])
        if not cands:
            absent += 1
            print(f"  NOCAND {cls:<24} {l:#06x} -> {w:#06x}   ({note})")
            continue
        got = [c["windows"] for c in cands]
        if got[0] == w:
            hit1 += 1; hitn += 1; verdict = "HIT@1"
        elif w in got:
            hitn += 1; verdict = f"HIT@{got.index(w) + 1}"
        else:
            wrong += 1; verdict = "WRONG"
        print(f"  {verdict:<6} {cls:<24} {l:#06x} -> {w:#06x}   got {[hex(g) for g in got]}"
              f"  margin={cands[0]['margin']}")
    n = len(gt)
    answered = n - absent
    print(f"\n  answered {answered}/{n}:  hit@1 {hit1}  hit@{top} {hitn}  WRONG {wrong}"
          f"   |  no candidate {absent}/{n}")
    return hit1, hitn, wrong, absent, n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("lin"); ap.add_argument("win")
    ap.add_argument("--pairs"); ap.add_argument("--alias"); ap.add_argument("--gt")
    ap.add_argument("--out"); ap.add_argument("--top", type=int, default=3)
    a = ap.parse_args()

    lin, win = read_dump(a.lin), read_dump(a.win)
    extra = {}
    if a.pairs:
        with open(a.pairs) as fh:
            for line in fh:
                if line.strip() and not line.startswith("#"):
                    q, e = line.split()[:2]
                    e = e.lower()
                    extra[q] = e[2:] if e.startswith("0x") else e
    pairs, stats = build_pairs(lin, win, extra)
    print(f"linux funcs {len(lin['funcs'])}  windows funcs {len(win['funcs'])}  pairs {len(pairs)}")
    print("  " + "  ".join(f"{k}={v}" for k, v in sorted(stats.items())))

    fields, vslots, fnpairs = collect(lin, win, pairs)
    if a.alias:
        with open(a.alias) as fh:
            for line in fh:
                if line.strip() and not line.startswith("#"):
                    base, *derived = line.split()
                    for dcls in derived:
                        fields.merge(base, dcls)
                        vslots.merge(base, dcls)
    rows = candidates(fields, "field", a.top) + candidates(vslots, "vslot", a.top, 0x200)
    firsts = [r for r in rows if r["rank"] == 1]
    print(f"classes with evidence {len(fields.fns)}  offsets with a candidate {len(firsts)}")

    if a.out:
        with open(a.out, "w", newline="") as fh:
            w = csv.DictWriter(fh, fieldnames=["cls", "kind", "linux", "windows", "delta",
                                               "rank", "score", "margin", "fns", "seen_l", "seen_w"])
            w.writeheader()
            for r in sorted(rows, key=lambda r: (r["cls"], r["kind"], r["linux"], r["rank"])):
                r = dict(r); r["linux"] = hex(r["linux"]); r["windows"] = hex(r["windows"])
                w.writerow(r)
        print(f"wrote {a.out}")
    if a.gt:
        evaluate(rows, a.gt, a.top)


if __name__ == "__main__":
    main()
