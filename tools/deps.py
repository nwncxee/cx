#!/usr/bin/env python3
"""CX-57: the pinned audio runtime, and FSR's SDK source, fetched once and checked by sha256. The pins are in
tools/deps.json, which flake.nix reads too.

    tools/deps.py <name> <dest>

Unpacks the named archive's wanted members under <dest> (idempotent: a stamp file names the sha it came from).
The archives cache in $CX_DEPS_CACHE (default ~/.cache/cx-deps), so a clean build does not re-download 180 MB.

  openal-src   OpenAL Soft 1.25.2 source (LGPL-2.1): the Linux libopenal.so.1 is built from it, and its AL
               headers (efx-presets.h included) are what native/cxaudio.c compiles against on both OSes.
  openal-win   the upstream Win64 soft_oal.dll of the same release, shipped as cx_openal.dll.
  steamaudio   Steam Audio 4.8.1 (Apache-2.0): libphonon.so / phonon.dll only -- the headers are vendored in
               third_party/steamaudio.
  ffx-sdk      AMD FidelityFX SDK 1.1.4 (MIT): the headers and sources the FSR 3.1 upscaler builds from
               (build.sh, native/ffx/).
  vulkan-headers  Khronos Vulkan-Headers 1.4.363 (Apache-2.0 / MIT): what the FSR library compiles against.
  glslang-win  glslang 16.6.0 for Windows: build-win-native.sh compiles FSR's shaders with it; Linux takes the
               nix shell's.
"""
import hashlib, json, os, pathlib, shutil, sys, tarfile, time, urllib.error, urllib.request, zipfile

DEPS = {name: (d['url'], d['sha256'], d['file'], d.get('members'))
        for name, d in json.loads((pathlib.Path(__file__).parent / 'deps.json').read_text()).items()}


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


ATTEMPTS = 5


def download(url, tmp):
    """A host's 5xx, 429 or dropped connection is retried with 1, 2, 4, 8 s waits; any other error stops at once."""
    for attempt in range(ATTEMPTS):
        try:
            with urllib.request.urlopen(url, timeout=60) as r, open(tmp, 'wb') as f:
                shutil.copyfileobj(r, f)
            return
        except urllib.error.HTTPError as e:
            if e.code < 500 and e.code != 429:
                sys.exit(f'!! {url}: HTTP {e.code}')
            why = f'HTTP {e.code}'
        except (urllib.error.URLError, OSError) as e:
            why = str(e)
        if attempt + 1 < ATTEMPTS:
            print(f'>> {url}: {why}, retrying in {2 ** attempt} s', file=sys.stderr)
            time.sleep(2 ** attempt)
    sys.exit(f'!! {url}: {why} on all {ATTEMPTS} attempts')


def fetch(name):
    url, want, file, _ = DEPS[name]
    cache = pathlib.Path(os.environ.get('CX_DEPS_CACHE', pathlib.Path.home() / '.cache' / 'cx-deps'))
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / file
    if not path.exists() or sha256(path) != want:
        print(f'>> fetching {url}', file=sys.stderr)
        tmp = path.with_suffix(f'.{os.getpid()}.part')  # two CI jobs can share one cache
        download(url, tmp)
        got = sha256(tmp)
        if got != want:
            tmp.unlink()
            sys.exit(f'!! {file}: sha256 {got} != pinned {want}')
        tmp.replace(path)
    return path


def main():
    name, dest = sys.argv[1], pathlib.Path(sys.argv[2])
    _, want, _, members = DEPS[name]
    stamp = dest / f'.{name}.sha256'
    if stamp.exists() and stamp.read_text().strip() == want:
        return
    path = fetch(name)
    dest.mkdir(parents=True, exist_ok=True)
    if path.name.endswith('.tar.gz'):
        with tarfile.open(path) as t:
            # a tarball's members are path prefixes, unpacked in place
            keep = None if members is None else [m for m in t.getmembers() if m.name.startswith(tuple(members))]
            t.extractall(dest, members=keep, filter='data')
    else:
        with zipfile.ZipFile(path) as z:
            for m in members:
                out = dest / pathlib.Path(m).name
                with z.open(m) as src, open(out, 'wb') as dst:
                    shutil.copyfileobj(src, dst)
    stamp.write_text(want)


if __name__ == '__main__':
    main()
