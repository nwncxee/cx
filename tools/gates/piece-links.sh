#!/usr/bin/env bash
# The owned-piece list is keyed by record INDEX, so a second link of one index rings it and every walk over it
# then runs forever on the game thread. One file owns both columns: SceneDb.Pieces.cs links, unlinks and walks
# them with a cap and a ring check. A raw read or write anywhere else is a walk nothing bounds.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE 'ObjSeamAppend(Head|Next)' -- managed \
  ':!managed/Alloy/Db/SceneDb.Pieces.cs' ':!managed/Alloy/Db/SceneDb.Pieces.SelfCheck.cs' \
  | grep -vE ':[0-9]+: *///?' || true)
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! piece-links: a raw owned-piece link outside SceneDb.Pieces.cs (above); walk it with SceneDb.Pieces," >&2
echo "   write it with SceneDb.PieceLink/PieceUnlink/PieceDropAll/PieceForget" >&2
exit 1
