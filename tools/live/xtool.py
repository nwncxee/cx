#!/usr/bin/env python3
"""X helper for the live harness: capture the nested display, inject input, ask the client to close.

Three jobs the shell cannot do without xdotool/xwd (neither is installed here), all against the
Xwayland server gamescope nests for the client:

    xtool.py shot  :N out.png        one screenshot of the client window (root as a fallback)
    xtool.py keys  :N "<script>"     XTEST input, ';'-separated steps:
                                       key:<keysym> | type:<text> | sleep:<ms>
                                       click:<x>,<y> | move:<x>,<y> (pointer only, for hover)
                                       rclick:<x>,<y> (button 3 -- the radial menu / use path)
                                       down:<x>,<y> | up:<x>,<y> (a HELD press, for a pressed state)
                                       scroll:<x>,<y>,<n> (the wheel: +n zooms the camera in)
                                       shot:<path> (a screenshot mid-script, for a timed capture)
                                       size:<w>,<h> (resize the client window, so the pointer can leave it)
                                       cursor:<tag> (log which pointer shape the X server shows: its size, hotspot and a
                                                     hash of its pixels, so a held cursor and the arrow tell apart)
    xtool.py close :N                WM_DELETE_WINDOW to the client window -- SDL turns it into SDL_QUIT

`shot` prefers the client's own window: under gamescope the root window is composited by gamescope
itself and can read back blank, while the redirected client window still holds the frame it presented.
"""
import sys
import time
import zlib
import struct

from Xlib import X, display, XK
from Xlib.ext import xtest
from Xlib.protocol import event


def client_window(dpy):
    """The biggest viewable child of the root -- the client's window, not gamescope's own helpers."""
    root = dpy.screen().root
    best, best_area = None, 0
    for w in root.query_tree().children:
        try:
            a = w.get_attributes()
            if a.map_state != X.IsViewable:
                continue
            g = w.get_geometry()
            if g.width * g.height > best_area:
                best, best_area = w, g.width * g.height
        except Exception:
            continue
    return best


def write_png(path, width, height, rgb):
    """Minimal PNG writer -- Pillow is not a dependency we can count on."""
    raw = b"".join(b"\x00" + rgb[y * width * 3:(y + 1) * width * 3] for y in range(height))

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    hdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", hdr))
        f.write(chunk(b"IDAT", zlib.compress(raw, 6)))
        f.write(chunk(b"IEND", b""))


def grab(win, geom):
    """XGetImage in slices: one request for a 1280x720 window exceeds the X maximum request size."""
    rows = []
    step = max(1, 2_000_000 // max(1, geom.width * 4))
    y = 0
    while y < geom.height:
        h = min(step, geom.height - y)
        img = win.get_image(0, y, geom.width, h, X.ZPixmap, 0xFFFFFFFF)
        rows.append(img.data)
        y += h
    return b"".join(rows)


def to_rgb(data, width, height):
    """ZPixmap at depth 24/32 on a little-endian server is BGRX; drop X and swap."""
    n = width * height
    if len(data) < n * 4:
        raise SystemExit(f"short image: {len(data)} bytes for {width}x{height}")
    data = bytes(data[:n * 4])
    out = bytearray(n * 3)
    out[0::3], out[1::3], out[2::3] = data[2::4], data[1::4], data[0::4]
    return bytes(out)


def cmd_shot(dpy, path):
    win = client_window(dpy) or dpy.screen().root
    geom = win.get_geometry()
    rgb = to_rgb(grab(win, geom), geom.width, geom.height)
    write_png(path, geom.width, geom.height, rgb)
    nonblack = sum(1 for i in range(0, len(rgb), 3 * 97) if rgb[i] or rgb[i + 1] or rgb[i + 2])
    total = len(range(0, len(rgb), 3 * 97))
    # The WORLD region: the centre of the window, clear of the chat panel (top left), the portrait (top right)
    # and the quickbar (bottom). A world pass that draws black still presents frames, so presents alone
    # cannot grade a render change -- this is the pixel half of the verdict.
    w, h = geom.width, geom.height
    lit = seen = 0
    acc = acc2 = 0.0
    for y in range(h * 30 // 100, h * 80 // 100, 4):
        row = y * w * 3
        for x in range(w * 35 // 100, w * 80 // 100, 4):
            i = row + x * 3
            seen += 1
            luma = 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2]
            acc += luma
            acc2 += luma * luma
            if luma > 24:
                lit += 1
    # A uniform fill (the clear colour, nothing drawn) is "lit" but has no structure: the luma spread is the
    # second half of the verdict.
    mean = acc / max(1, seen)
    spread = max(0.0, acc2 / max(1, seen) - mean * mean) ** 0.5
    print(f"{path} {w}x{h} nonblack={nonblack}/{total} world_lit={100 * lit // max(1, seen)}% world_spread={int(spread)}")


def cmd_keys(dpy, script):
    win = client_window(dpy)
    if win is not None:
        win.set_input_focus(X.RevertToParent, X.CurrentTime)
        dpy.sync()
    for step in script.split(";"):
        step = step.strip()
        if not step:
            continue
        kind, _, arg = step.partition(":")
        if kind == "sleep":
            time.sleep(int(arg) / 1000.0)
        elif kind == "key":
            tap(dpy, arg)
        elif kind == "click":
            x, y = (int(v) for v in arg.split(","))
            xtest.fake_input(dpy, X.MotionNotify, x=x, y=y)
            dpy.sync()
            time.sleep(0.15)
            xtest.fake_input(dpy, X.ButtonPress, 1)
            xtest.fake_input(dpy, X.ButtonRelease, 1)
        elif kind == "rclick":
            # Button 3, the radial-menu button: a right-click on an item cell or on the world is the only
            # way to reach CNWCItem::ActivateRadialMenu and the engine's CRadialMenu construction.
            x, y = (int(v) for v in arg.split(","))
            xtest.fake_input(dpy, X.MotionNotify, x=x, y=y)
            dpy.sync()
            time.sleep(0.15)
            xtest.fake_input(dpy, X.ButtonPress, 3)
            xtest.fake_input(dpy, X.ButtonRelease, 3)
        elif kind == "scroll":
            # Buttons 4 and 5 are the wheel, which is the only input the camera zoom listens to: the
            # game ships no key bind for it, so a close camera is reachable no other way.
            x, y, n = (int(v) for v in arg.split(","))
            xtest.fake_input(dpy, X.MotionNotify, x=x, y=y)
            dpy.sync()
            button = 4 if n > 0 else 5
            for _ in range(abs(n)):
                xtest.fake_input(dpy, X.ButtonPress, button)
                xtest.fake_input(dpy, X.ButtonRelease, button)
                dpy.sync()
                time.sleep(0.05)
        elif kind in ("down", "up"):
            # Press and release as SEPARATE steps, so a shot can be taken while the button is held:
            # a GUI button's pressed state only exists between the two, and `click` closes it at once.
            x, y = (int(v) for v in arg.split(","))
            xtest.fake_input(dpy, X.MotionNotify, x=x, y=y)
            dpy.sync()
            time.sleep(0.05)
            xtest.fake_input(dpy, X.ButtonPress if kind == "down" else X.ButtonRelease, 1)
        elif kind == "move":
            # Pointer motion with no button: what makes the engine run its cursor hit-check over
            # whatever is under it, which is the "hovered a creature" half of a pick.
            x, y = (int(v) for v in arg.split(","))
            xtest.fake_input(dpy, X.MotionNotify, x=x, y=y)
        elif kind == "size":
            w, h = (int(v) for v in arg.split(","))
            if win is not None:
                win.configure(width=w, height=h)
        elif kind == "shot":
            # A capture from inside the script, so a screenshot can be timed against a step
            # rather than only taken at the soak's halves.
            cmd_shot(dpy, arg)
        elif kind == "cursor":
            print(cursor_line(dpy, arg), flush=True)
        elif kind == "type":
            for ch in arg:
                tap(dpy, char_keysym(ch), shift=ch.isupper())
                time.sleep(0.03)
        else:
            raise SystemExit(f"xtool keys: unknown step {step!r}")
        dpy.sync()


def cursor_line(dpy, tag):
    """One line about the pointer shape the X server shows now. The hash is of the ARGB pixels, so two shapes differ."""
    from Xlib.ext import xfixes
    dpy.xfixes_query_version()
    c = dpy.xfixes_get_cursor_image(dpy.screen().root)
    px = c.cursor_image
    opaque = sum(1 for p in px if (p >> 24) & 0xFF)
    digest = zlib.crc32(struct.pack(f"<{len(px)}I", *px)) if px else 0
    return f"cursor {tag}: {c.width}x{c.height} hot={c.xhot},{c.yhot} opaque={opaque} crc={digest:08x}"


SHIFTED = {"/": "slash", "?": ("slash", True), " ": "space", ".": "period", ",": "comma",
           "-": "minus", "_": ("minus", True), "=": "equal", "+": ("plus", False),
           ":": ("colon", False), "'": "apostrophe", '"': ("quotedbl", False)}


def char_keysym(ch):
    if ch in SHIFTED:
        v = SHIFTED[ch]
        return v[0] if isinstance(v, tuple) else v
    if ch.isalnum():
        return ch.lower() if ch.isalpha() else ch
    return XK.keysym_to_string(ord(ch)) or ch


def tap(dpy, keysym_name, shift=False):
    ks = XK.string_to_keysym(keysym_name)
    if ks == 0 and len(keysym_name) == 1:
        ks = ord(keysym_name)
    code = dpy.keysym_to_keycode(ks)
    if code == 0:
        raise SystemExit(f"xtool keys: no keycode for {keysym_name!r}")
    shift_code = dpy.keysym_to_keycode(XK.string_to_keysym("Shift_L"))
    if shift:
        xtest.fake_input(dpy, X.KeyPress, shift_code)
    xtest.fake_input(dpy, X.KeyPress, code)
    xtest.fake_input(dpy, X.KeyRelease, code)
    if shift:
        xtest.fake_input(dpy, X.KeyRelease, shift_code)


def cmd_close(dpy):
    win = client_window(dpy)
    if win is None:
        raise SystemExit("xtool close: no client window")
    wm_protocols = dpy.intern_atom("WM_PROTOCOLS")
    wm_delete = dpy.intern_atom("WM_DELETE_WINDOW")
    win.send_event(event.ClientMessage(window=win, client_type=wm_protocols, data=(32, [wm_delete, X.CurrentTime, 0, 0, 0])))
    dpy.sync()
    print("WM_DELETE_WINDOW sent")


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    cmd, disp = sys.argv[1], sys.argv[2]
    # A clean UI quit takes the X server down with the client, so the rest of a shutdown sequence
    # finds nothing to talk to. That is the success case, not a stack trace.
    try:
        dpy = display.Display(disp)
    except Exception as e:
        raise SystemExit(f"xtool: display {disp} is gone ({type(e).__name__}) -- the client exited")
    try:
        if cmd == "shot":
            cmd_shot(dpy, sys.argv[3])
        elif cmd == "keys":
            cmd_keys(dpy, sys.argv[3])
        elif cmd == "close":
            cmd_close(dpy)
        else:
            raise SystemExit(f"xtool: unknown command {cmd!r}")
    except (ConnectionResetError, BrokenPipeError, OSError) as e:
        raise SystemExit(f"xtool {cmd}: display {disp} went away ({type(e).__name__}) -- the client exited")
    except Exception as e:
        if type(e).__module__.startswith("Xlib"):
            raise SystemExit(f"xtool {cmd}: display {disp} went away ({type(e).__name__}) -- the client exited")
        raise


if __name__ == "__main__":
    main()
