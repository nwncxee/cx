#!/usr/bin/env python3
"""bin/discord-reports against a stub Discord API: open and archived posts both print, across archive pages,
attachments download into the output folder (without the bot token, under a contained name), and the agent docs send a Discord bug-report question to the reader instead of saying an agent can't read Discord.
Run: python3 tools/discord_reports_test.py
"""
import http.server
import json
import os
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
G, F, OTHER = "100", "200", "201"
ROUTES = {
    "/users/@me/guilds": [{"id": G}],
    f"/guilds/{G}/channels": [{"id": OTHER, "type": 0, "name": "general"},
                              {"id": F, "type": 15, "name": "bug-reports", "available_tags": [{"id": "9", "name": "Needs Testing"}]}],
    f"/guilds/{G}/threads/active": {"threads": [
        {"id": "5000", "parent_id": F, "name": "Open post", "applied_tags": ["9"], "thread_metadata": {"archived": False}},
        {"id": "5001", "parent_id": OTHER, "name": "Not a report", "thread_metadata": {"archived": False}}]},
    f"/channels/{F}/threads/archived/public?limit=100": {"has_more": True, "threads": [
        {"id": "4000", "parent_id": F, "name": "Archived post", "thread_metadata": {"archived": True, "archive_timestamp": "T1"}}]},
    f"/channels/{F}/threads/archived/public?limit=100&before=T1": {"has_more": False, "threads": [
        {"id": "3000", "parent_id": F, "name": "Older archived post", "thread_metadata": {"archived": True, "archive_timestamp": "T0"}}]},
    "/channels/5000/messages?limit=100": [{"timestamp": "2026-11-20T12:00:00", "author": {"username": "b"}, "content": "second",
                                           "attachments": [{"id": "77", "filename": "../../cx.log", "content_type": "text/plain", "size": 9, "url": "CDN/cx.log"}]},
                                          {"timestamp": "2026-11-20T11:00:00", "author": {"username": "a"}, "content": "first"}],
    "/channels/4000/messages?limit=100": [{"timestamp": "2026-11-19T10:00:00", "author": {"username": "c"}, "content": "fixed"}],
    "/channels/3000/messages?limit=100": [{"timestamp": "2026-11-18T10:00:00", "author": {"username": "d"}, "content": "old"}],
}


CDN_BODY = b"log line\n"
CDN_AUTH = []


class Stub(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/cdn/"):
            CDN_AUTH.append(self.headers.get("Authorization"))
            self.send_response(200); self.end_headers(); self.wfile.write(CDN_BODY); return
        if self.headers.get("Authorization") != "Bot test-token":
            self.send_response(401); self.end_headers(); return
        body = ROUTES.get(self.path.removeprefix("/api"))
        self.send_response(200 if body is not None else 404)
        self.end_headers()
        self.wfile.write(json.dumps(body).encode())

    def log_message(self, *_):
        pass


def main():
    fails = []
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Stub)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    ROUTES["/channels/5000/messages?limit=100"][0]["attachments"][0]["url"] = f"http://127.0.0.1:{srv.server_port}/cdn/cx.log"
    outdir = Path(tempfile.mkdtemp()) / "att"
    with tempfile.NamedTemporaryFile("w", delete=False) as tok:
        tok.write("test-token\n")
    env = dict(os.environ, DISCORD_API=f"http://127.0.0.1:{srv.server_port}/api", DISCORD_TOKEN_FILE=tok.name)
    r = subprocess.run([sys.executable, str(ROOT / "bin/discord-reports"), "--out", str(outdir)], env=env,
                       capture_output=True, text=True)
    out = r.stdout
    got = sorted(outdir.iterdir()) if outdir.is_dir() else []
    if [g.name for g in got] != ["5000-77-cx.log"] or got[0].read_bytes() != CDN_BODY:
        fails.append(f"attachment not downloaded into --out under a contained name: {[g.name for g in got]}")
    elif f"cx.log (text/plain, 9 B): {got[0].resolve()}" not in out:
        fails.append("attachment's local path not printed under its message")
    if CDN_AUTH != [None]:
        fails.append(f"attachment fetch must not carry the bot token: {CDN_AUTH}")
    again = subprocess.run([sys.executable, str(ROOT / "bin/discord-reports"), "--out", str(outdir)], env=env,
                           capture_output=True, text=True)
    if again.returncode == 0 or "not empty" not in again.stderr:
        fails.append("--out into a non-empty folder must refuse")
    bare = subprocess.run([sys.executable, str(ROOT / "bin/discord-reports"), "--no-attachments"], env=env,
                          capture_output=True, text=True)
    if "cdn/cx.log" not in bare.stdout or len(CDN_AUTH) != 1:
        fails.append("--no-attachments must list the url and download nothing")
    if r.returncode != 0:
        fails.append(f"exit {r.returncode}: {r.stderr.strip()}")
    for want in ("Open post | open | tags: Needs Testing", "Archived post | archived", "Older archived post | archived",
                 "] b +1att: second", "attachments: "):
        if want not in out:
            fails.append(f"missing {want!r}")
    if "Not a report" in out:
        fails.append("printed a thread from another channel")
    if out.find("first") > out.find("second"):
        fails.append("messages not oldest-first")
    if not (out.find("Open post") < out.find("Archived post") < out.find("Older archived post")):
        fails.append("posts not newest-first")
    gone = subprocess.run([sys.executable, str(ROOT / "bin/discord-reports")], capture_output=True, text=True,
                          env=dict(env, DISCORD_TOKEN_FILE=tok.name + ".missing"))
    if gone.returncode == 0 or "on purpose" not in gone.stderr or "Don't propose persisting" not in gone.stderr:
        fails.append(f"a missing token must say it is unpersisted on purpose: {gone.stderr.strip()!r}")
    doc = (ROOT / "docs/agents/issue-tracker.md").read_text()
    if "can't read Discord" in doc or "bin/discord-reports" not in doc:
        fails.append("issue-tracker.md must send Discord bug reports to bin/discord-reports")
    os.unlink(tok.name)
    srv.shutdown()
    for f in fails:
        print("FAIL", f)
    print("ok" if not fails else f"{len(fails)} failed")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
