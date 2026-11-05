# Shared by sign.sh's approved examples and regression test: stub gh, ssign and osslsigncode on PATH stand in for
# GitHub, Certum's cloud and the Authenticode check, and plant() fakes a draft release. ssh-keygen runs for real
# with a throwaway key. Source it from the repo root; it sets repo, rc, tmp, tag, certum, other and $tmp/code.
repo=$PWD
rc=0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
tag=v9.9.9
certum=6c1137379e841cac08898b423b1e8805aaff6f95ff5927a29b2678a4a8a99aa6
other=0000000000000000000000000000000000000000000000000000000000000000

mkdir -p "$tmp/stubs"
cat > "$tmp/stubs/gh" << 'EOF'
#!/usr/bin/env bash
case "$1 $2" in
  "release view")
    case "$*" in
      *isDraft*) echo true ;;
      *assets*) (cd "$CASE/draft" && ls | LC_ALL=C sort | paste -sd' ') ;;
    esac ;;
  "release download")
    shift 2; dir=""; while [ $# -gt 0 ]; do [ "$1" = -D ] && dir=$2; shift; done
    cp "$CASE"/draft/*.zip "$CASE/draft/SHA256SUMS" "$dir/" ;;
  "attestation verify") exit 0 ;;
  "release upload")
    shift 2
    for f in "$@"; do [ -f "$f" ] && { cp "$f" "$CASE/draft/"; echo "$f" >> "$CASE/uploaded"; }; done ;;
  "release edit") case "$*" in *--draft=false*) touch "$CASE/published" ;; esac ;;
esac
exit 0
EOF
cat > "$tmp/stubs/ssign" << 'EOF'
#!/usr/bin/env bash
[ "$1" = --version ] && { echo "ssign ${STUB_SSIGN_VERSION:-0.1.8}"; exit 0; }
echo called >> "$CASE/ssign-calls"
[ "$CERTUM_TOKEN" = 123456 ] || { echo "ssign: login refused" >&2; exit 1; }
for f in "$@"; do case "$f" in *.dll) printf 'SIGNED-LEAF:%s\n' "$STUB_LEAF" >> "$f" ;; esac; done
EOF
cat > "$tmp/stubs/osslsigncode" << 'EOF'
#!/usr/bin/env bash
[ "$1" = verify ] || exit 2
in="" hash=""
while [ $# -gt 0 ]; do case "$1" in -in) in=$2 ;; -require-leaf-hash) hash=${2#sha256:} ;; esac; shift; done
grep -q "SIGNED-LEAF:$hash" "$in"
EOF
chmod +x "$tmp/stubs"/*

ssh-keygen -q -t ed25519 -N '' -C cx-releases -f "$tmp/release-key"

# A fresh case under $tmp/<name>: a tree holding sign.sh, and a draft whose Windows zip carries version.dll
# with the text $2 (empty for unsigned). Prints the case folder.
plant() {
  local case=$tmp/$1 dll=$2
  mkdir -p "$case/tree/tools/release" "$case/tree/packaging" "$case/draft" "$case/win"
  cp "$repo/tools/release/sign.sh" "$case/tree/tools/release/"
  cp "$repo/tools/paths.py" "$case/tree/tools/"
  echo "cx-releases $(cat "$tmp/release-key.pub")" > "$case/tree/packaging/allowed_signers"
  printf 'MZ version\n%s' "$dll" > "$case/win/version.dll"
  echo 'MZ openal' > "$case/win/cx_openal.dll"
  echo 'MZ phonon' > "$case/win/phonon.dll"
  python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2])" \
    "$case/draft/cx-windows-x64-$tag" "$case/win"
  echo linux > "$case/linux" && (cd "$case" && python3 -m zipfile -c "draft/cx-linux-x64-$tag.zip" linux)
  echo plugin > "$case/plugin" && (cd "$case" && python3 -m zipfile -c "draft/cx-moonglow-plugin-$tag.zip" plugin)
  (cd "$case/draft" && sha256sum ./*.zip | sed 's#  \./#  #' > SHA256SUMS)
  cp "$case/draft/cx-windows-x64-$tag.zip" "$case/ci-windows.zip"
  echo "$case"
}

# Runs the case's sign.sh with stdin from $2, the stubs first on PATH, and the extra environment after it.
run() {
  local case=$1 input=$2; shift 2
  env CASE="$case" PATH="$tmp/stubs:$PATH" RELEASE_SIGN_KEY="$tmp/release-key" CERTUM_EMAIL=dev@example.com \
    STUB_LEAF=$certum "$@" bash "$case/tree/tools/release/sign.sh" "$tag" < "$input" > "$case/out" 2>&1
}

fail() { echo "!! $1"; rc=1; }

printf '123456\n' > "$tmp/code"

