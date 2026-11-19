"""Typed text keeps its shifted characters: '_' types as Shift+minus, not '-'."""
import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from keymap import char_key

US = {0x2d: [(20, 0)], 0x5f: [(20, 1)], 0x61: [(38, 0)], 0x41: [(38, 1)], 0x62: [(56, 0)],
      0x3b: [(47, 0)], 0x3a: [(47, 1)], 0x2f: [(61, 0)], 0x3f: [(61, 1)], 0x20: [(65, 0)]}
look = lambda ks: US.get(ks, [])

fail = 0
for text, want in [("a_b", [(38, False), (20, True), (56, False)]),
                   ("-", [(20, False)]), ("A:", [(38, True), (47, True)]),
                   ("/?", [(61, False), (61, True)]), (" ", [(65, False)])]:
    got = [char_key(c, look) for c in text]
    ok = got == want
    fail |= not ok
    print(("ok  " if ok else "FAIL") + f" type:{text} -> {got}" + ("" if ok else f", wanted {want}"))
try:
    char_key("é", look)
    print("FAIL a character the keymap lacks was typed anyway"); fail = 1
except SystemExit:
    print("ok   a character the keymap lacks is refused by name")
sys.exit(fail)
