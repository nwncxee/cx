#!/usr/bin/env bash
# Regression tests for tools/deps.py's download: a host that answers 504 a few times and then serves the file.
# Oracle: a local HTTP server that counts requests and fails a set number of them, and a fixture tarball's sha256.
cd "$(dirname "$0")/.." || exit 1
tmp=$(mktemp -d)
trap 'kill "$srv" 2> /dev/null; rm -rf "$tmp"' EXIT
rc=0
fail() { echo "!! deps regression: $*" >&2; rc=1; }

mkdir -p "$tmp/src/pkg" "$tmp/tool"
echo payload > "$tmp/src/pkg/file.txt"
tar -czf "$tmp/www.tar.gz" -C "$tmp/src" pkg
sum=$(sha256sum "$tmp/www.tar.gz" | cut -d' ' -f1)
cp tools/deps.py "$tmp/tool/deps.py"

cat > "$tmp/server.py" << 'PY'
import http.server, pathlib, sys
root = pathlib.Path(sys.argv[1])
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        name = self.path.strip('/')
        log = root / f'{name}.hits'
        hits = int(log.read_text()) + 1 if log.exists() else 1
        log.write_text(str(hits))
        fails = int((root / f'{name}.fails').read_text())
        if fails < 0 or hits <= fails:
            self.send_response(504 if fails != -404 else 404); self.end_headers(); return
        body = (root / 'www.tar.gz').read_bytes()
        self.send_response(200); self.send_header('Content-Length', str(len(body))); self.end_headers()
        self.wfile.write(body)
    def log_message(self, *a): pass
s = http.server.HTTPServer(('127.0.0.1', 0), H)
(root / 'port').write_text(str(s.server_port))
s.serve_forever()
PY
python3 "$tmp/server.py" "$tmp" & srv=$!
for _ in $(seq 50); do [ -s "$tmp/port" ] && break; sleep 0.1; done
port=$(cat "$tmp/port")

# name, how many requests fail (-1 every one, -404 a 404 every time)
stage() {
  echo "$2" > "$tmp/$1.fails"
  python3 - "$tmp/tool/deps.json" "$1" "http://127.0.0.1:$port/$1" "$sum" << 'PY'
import json, pathlib, sys
f = pathlib.Path(sys.argv[1]); d = json.loads(f.read_text()) if f.exists() else {}
d[sys.argv[2]] = {'url': sys.argv[3], 'sha256': sys.argv[4], 'file': sys.argv[2] + '.tar.gz', 'members': None}
f.write_text(json.dumps(d))
PY
}
fetch() { CX_DEPS_CACHE="$tmp/cache" timeout 120 python3 "$tmp/tool/deps.py" "$1" "$tmp/out-$1" > "$tmp/$1.log" 2>&1; }

# Two 504s and then the file: the download is retried and the archive unpacks.
stage flaky 2
if fetch flaky; then
  [ "$(cat "$tmp/out-flaky/pkg/file.txt" 2> /dev/null)" = payload ] || fail "flaky: the archive did not unpack"
  [ "$(cat "$tmp/flaky.hits")" = 3 ] || fail "flaky: $(cat "$tmp/flaky.hits") requests, expected 3"
else
  fail "flaky: deps.py gave up after a 504 -- $(tail -1 "$tmp/flaky.log")"
fi

# A 404 is not retried: the pin is wrong, and waiting will not fix it.
stage gone -404
if fetch gone; then fail "gone: deps.py succeeded on a 404"; fi
[ "$(cat "$tmp/gone.hits")" = 1 ] || fail "gone: a 404 was asked $(cat "$tmp/gone.hits") times, expected once"

# A host that never recovers stops the build after five attempts, with the URL in the message.
stage down -1
start=$SECONDS
if fetch down; then fail "down: deps.py succeeded against a host that only answers 504"; fi
[ $((SECONDS - start)) -ge 14 ] || fail "down: gave up after $((SECONDS - start)) s, expected the 1+2+4+8 s waits"
[ "$(cat "$tmp/down.hits")" = 5 ] || fail "down: $(cat "$tmp/down.hits") attempts, expected 5"
grep -q "127.0.0.1:$port/down" "$tmp/down.log" || fail "down: the failure does not name the URL"

[ $rc = 0 ] && echo "deps regression: passed"
exit $rc
