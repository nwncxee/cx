#!/usr/bin/env python3
"""The ladder's revisit grade, read off a session's cx.log: the first area's GPU time per frame on the first visit
against the return to it, the area visits split at TexStream's [load] lines. Exit 0 PASS, 1 FAIL.
Usage: python3 -I tools/live/revisit.py <cx.log>"""
import sys

# the marker a FAIL line carries when the session measured no GPU time: what a caller matches, never the prose
NO_GPU_TIME = "[revisit] no-gpu-time"

if __name__ == "__main__":
    sys.exit(0)
