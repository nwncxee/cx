#!/usr/bin/env bash
# Regression tests, not approved ones: behaviour sign.sh already had, or that its approved examples leave
# open. Oracle: the Certum certificate's SHA-256, worked out by hand from the issued certificate, and the
# fixture zips plant() builds.
cd "$(dirname "$0")/../.." || exit 1
. tools/release/sign_harness.sh

# A draft whose version.dll CI already signed is left alone: no code asked, only SHA256SUMS.sig uploaded.
c=$(plant signed "SIGNED-LEAF:$certum")
if run "$c" /dev/null; then
  [ ! -f "$c/ssign-calls" ] || fail "signed: sign.sh ran ssign on a signed version.dll"
  [ "$(cat "$c/uploaded" 2> /dev/null)" = SHA256SUMS.sig ] ||
    fail "signed: sign.sh uploaded [$(paste -sd' ' "$c/uploaded" 2> /dev/null)], expected [SHA256SUMS.sig]"
else
  fail "signed: sign.sh failed -- $(tail -3 "$c/out")"
fi

# After signing, SHA256SUMS still lists every zip of the draft, each with its checksum.
c=$(plant sums "")
if run "$c" "$tmp/code"; then
  for z in cx-linux-x64 cx-moonglow-plugin cx-windows-x64; do
    grep -q "  $z-$tag.zip\$" "$c/draft/SHA256SUMS" || fail "sums: SHA256SUMS lost $z-$tag.zip"
  done
  (cd "$c/draft" && sha256sum -c --quiet SHA256SUMS) || fail "sums: a checksum in SHA256SUMS is wrong"
else
  fail "sums: sign.sh failed -- $(tail -3 "$c/out")"
fi

# A mistyped code stops the run with the draft unpublished and nothing uploaded.
printf '000000\n' > "$tmp/bad-code"
c=$(plant typo "")
if run "$c" "$tmp/bad-code"; then fail "typo: sign.sh succeeded with a wrong code"; fi
[ ! -f "$c/published" ] || fail "typo: the draft was published"
[ ! -f "$c/uploaded" ] || fail "typo: sign.sh uploaded [$(paste -sd' ' "$c/uploaded")]"

# With CERTUM_EMAIL unset, sign.sh asks for the account e-mail before the code.
printf 'dev@example.com\n123456\n' > "$tmp/email-code"
c=$(plant email "")
if run "$c" "$tmp/email-code" CERTUM_EMAIL=; then
  [ -f "$c/published" ] || fail "email: the draft was not published"
else
  fail "email: sign.sh failed when asked for the e-mail -- $(tail -3 "$c/out")"
fi

# PATH with the gh and ssign stubs and every directory of $PATH except those holding osslsigncode, so the
# case's own nix stub in $1 decides where osslsigncode comes from.
no_ossl_path() {
  local dir=$1 d
  for s in gh ssign; do ln -sf "$tmp/stubs/$s" "$dir/$s"; done
  printf '%s' "$dir"
  IFS=: read -ra dirs <<< "$PATH"
  for d in "${dirs[@]}"; do [ -x "$d/osslsigncode" ] || printf ':%s' "$d"; done
}

# Without osslsigncode, and with a nix that cannot build it, sign.sh stops naming it before it touches the draft.
c=$(plant no-ossl "SIGNED-LEAF:$certum")
mkdir -p "$c/bin" && printf '#!/usr/bin/env bash\necho "nix: build failed" >&2\nexit 1\n' > "$c/bin/nix" && chmod +x "$c/bin/nix"
if env CASE="$c" PATH="$(no_ossl_path "$c/bin")" RELEASE_SIGN_KEY="$tmp/release-key" CERTUM_EMAIL=dev@example.com \
  bash "$c/tree/tools/release/sign.sh" "$tag" < /dev/null > "$c/out" 2>&1; then
  fail "no-ossl: sign.sh succeeded with no osslsigncode"
fi
grep -q osslsigncode "$c/out" || fail "no-ossl: the error does not name osslsigncode -- $(tail -2 "$c/out")"
[ ! -f "$c/uploaded" ] && [ ! -f "$c/published" ] || fail "no-ossl: sign.sh touched the draft"

# Without osslsigncode on PATH, sign.sh builds the flake's own and signs with it.
c=$(plant nix-ossl "SIGNED-LEAF:$certum")
mkdir -p "$c/bin" "$c/store/bin" && cp "$tmp/stubs/osslsigncode" "$c/store/bin/"
cat > "$c/bin/nix" << NIX
#!/usr/bin/env bash
case "\$*" in *"build --no-link --print-out-paths $c/tree#osslsigncode") echo "$c/store" ;; *) exit 1 ;; esac
NIX
chmod +x "$c/bin/nix"
if env CASE="$c" PATH="$(no_ossl_path "$c/bin")" RELEASE_SIGN_KEY="$tmp/release-key" CERTUM_EMAIL=dev@example.com \
  bash "$c/tree/tools/release/sign.sh" "$tag" < /dev/null > "$c/out" 2>&1; then
  [ -f "$c/published" ] || fail "nix-ossl: the draft was not published"
else
  fail "nix-ossl: sign.sh failed with osslsigncode from nix -- $(tail -3 "$c/out")"
fi

[ $rc -eq 0 ] && echo ">> sign.sh regression tests pass"
exit $rc
