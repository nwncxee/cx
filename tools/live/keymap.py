"""Which key, and whether Shift, types one character: read off the X server's own keymap, never a table."""


def char_keysym(ch):
    """The X keysym of a printable character: Latin-1's is the code point, the rest sit at 0x01000000 + it."""
    o = ord(ch)
    if not ch.isprintable():
        raise SystemExit(f"xtool keys: type: takes printable text, not {ch!r}; use a key: step")
    return o if o <= 0xFF else 0x01000000 + o


def char_key(ch, keycodes):
    """(keycode, shift) for ch. keycodes(keysym) lists (code, index) pairs; index 1 is the shifted level."""
    pairs = sorted(keycodes(char_keysym(ch)), key=lambda p: p[1])
    for code, index in pairs:
        if index in (0, 1):
            return code, index == 1
    raise SystemExit(f"xtool keys: the keyboard map has no key that types {ch!r}")
