#!/usr/bin/env python3
"""Cut the font self-checks' fixtures from installed fonts and write fontTools' golden outlines.

    python3 tools/fonts/make-fixtures.py <dejavu dir> <NotoSans.ttf> <NotoSans-Italic.ttf> <Cantarell-VF.otf> <out dir>

Needs fontTools. Each fixture keeps only the glyphs of TEXT, every name record, OS/2 and the variation tables, so it
stays a few KB and reads like the file it came from. The goldens are fontTools' own instancer, the oracle the
variable-font self-check compares its outlines with.
"""
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.ttLib.ttCollection import TTCollection
from fontTools.misc.roundTools import otRound
from fontTools.varLib import instancer

TEXT = "Hamburgefonstiv HOLx01"
DEJAVU = ["DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-ExtraLight.ttf", "DejaVuSans-Oblique.ttf",
          "DejaVuSans-BoldOblique.ttf", "DejaVuSansCondensed.ttf", "DejaVuSansCondensed-Bold.ttf"]
WEIGHTS = [100, 200, 300, 400, 500, 600, 700, 800, 900]


def cut(src, dst, hinting=False):
    opt = subset.Options()
    opt.name_IDs = ["*"]
    opt.name_languages = ["*"]
    opt.name_legacy = True
    opt.notdef_outline = True
    opt.layout_features = []
    opt.drop_tables += ["GSUB", "GPOS", "GDEF", "STAT", "MVAR", "DSIG", "kern", "hdmx", "VDMX", "LTSH"]
    opt.hinting = hinting
    font = TTFont(src)
    s = subset.Subsetter(opt)
    s.populate(text=TEXT)
    s.subset(font)
    font.save(dst)
    return dst


def golden(var_path, out):
    """One line per (weight, glyph): advance, then each contour's points as x,y,on."""
    lines = []
    for w in WEIGHTS:
        inst = instancer.instantiateVariableFont(TTFont(var_path), {"wght": w, "wdth": 100})
        glyf, hmtx, cmap = inst["glyf"], inst["hmtx"], inst.getBestCmap()
        for ch in sorted(set(TEXT) - {" "}):
            name = cmap[ord(ch)]
            g = glyf[name]
            coords, ends, flags = g.getCoordinates(glyf)
            pts, start = [], 0
            for end in ends:
                pts.append(" ".join(f"{otRound(x)},{otRound(y)},{f & 1}" for (x, y), f in zip(coords[start:end + 1], flags[start:end + 1])))
                start = end + 1
            lines.append(f"{w}\tU+{ord(ch):04X}\t{otRound(hmtx[name][0])}\t" + " | ".join(pts))
    with open(out, "w") as f:
        f.write("\n".join(lines) + "\n")


def main(dejavu, noto, noto_italic, cantarell, out):
    os.makedirs(out, exist_ok=True)
    for name in DEJAVU:
        cut(os.path.join(dejavu, name), os.path.join(out, name))
    vf = cut(noto, os.path.join(out, "NotoSans-VF.ttf"))
    cut(noto_italic, os.path.join(out, "NotoSans-Italic-VF.ttf"))
    cut(cantarell, os.path.join(out, "Cantarell-VF.otf"), hinting=True)  # fontTools cannot strip CFF2 hints
    narrow = instancer.instantiateVariableFont(TTFont(vf), {"wght": (300, 700)})
    narrow.save(os.path.join(out, "NotoSans-300-700-VF.ttf"))
    pair = TTCollection()
    pair.fonts = [TTFont(os.path.join(out, "DejaVuSans.ttf")), TTFont(os.path.join(out, "DejaVuSansCondensed.ttf"))]
    pair.save(os.path.join(out, "DejaVuPair.ttc"))
    cff2 = TTCollection()
    cff2.fonts = [TTFont(os.path.join(out, "Cantarell-VF.otf"))]
    cff2.save(os.path.join(out, "Cantarell-CFF2.ttc"))
    golden(vf, os.path.join(out, "NotoSans-VF.golden.tsv"))


if __name__ == "__main__":
    if len(sys.argv) != 6:
        sys.exit(__doc__)
    main(*sys.argv[1:])
