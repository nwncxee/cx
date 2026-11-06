#!/usr/bin/env bash
# The route memo trusts its setters: a write of a route term that goes round SceneDb's Set* draws the old term until
# the slot's audit turn. Self-checks and fixtures plant such writes on purpose.
cd "$(dirname "$0")/../.."
hits=$(grep -rnE 'SceneDb\.(PartAlpha|PartFade|PartDissolve|ObjScene|ObjLodShown|ObjTint)\[[^]]*\](\.W)? *[-+*]?=[^=]' managed --include='*.cs' \
  | grep -vE '(SelfCheck|Fixture|ApprovedExamples)[^/]*\.cs:')
[ -z "$hits" ] && exit 0
echo "!! route-terms: write a route term through its SceneDb setter, or the route memo keeps the old value:" >&2
echo "$hits" >&2
exit 1
