#!/usr/bin/env bash
# Shared by the mutation chart's approved examples under tools/selfcheck/ (sourced, never run). Linux-only, like the rest
# of the developer tooling.
#   BASE_REV       the commit the "before" examples compare against: main before the chart's first change
#   src <File.cs>  the tracked path of a source file by its name, wherever under managed/ it lives now
#   review_list <out>  the 2026-11-21 review's mutants, from review_mutants.tsv, each file's path resolved: six fields
#   baseline       sets BASE_LOG and BASE_SECS from tools/selfcheck/baseline.sh, or exits 2
BASE_REV=9e89af8e

src() {
  local p
  p=$(git ls-files "managed/*/$1" "managed/$1" | head -1)
  [ -n "$p" ] || { echo ">> no $1 under managed/" >&2; return 2; }
  echo "$p"
}

review_list() {
  local name file old new word tag
  : > "$1"
  while IFS= read -r row; do
    case "$row" in ''|'#'*) continue ;; esac
    name=$(cut -f1 <<< "$row"); file=$(cut -f2 <<< "$row"); old=$(cut -f3 <<< "$row"); new=$(cut -f4 <<< "$row"); word=$(cut -f5 <<< "$row"); tag=$(cut -f6 <<< "$row")
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$(src "$file")" "$old" "$new" "$word" "$tag" >> "$1"
  done < "$(dirname "${BASH_SOURCE[0]}")/review_mutants.tsv"
}

baseline() {
  local out
  out=$(tools/selfcheck/baseline.sh "${1:-$BASE_REV}") || exit 2
  BASE_LOG=$(sed -n 1p <<< "$out"); BASE_SECS=$(sed -n 2p <<< "$out")
  [ -f "$BASE_LOG" ] && [ -n "$BASE_SECS" ] || { echo ">> the baseline gave no log or time" >&2; exit 2; }
}
