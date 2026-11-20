using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Headless: a static light that changes after the set closes is baked as it is now, not as the close saw
/// it. Oracle: hand-written lights and colours driven through the walk's own record and close calls.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The close-then-change cases: a recolour or a move re-closes the set on the light as it is now, a
    /// light still blending or a hair off does not, one that starts moving leaves the set, and a light that never
    /// stops changing re-closes a bounded number of times.</summary>
    internal static bool DriftSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.DriftSelfCheck");
        ExtenderLog? live = _log; _log = null;   // the close logs as evidence: a headless one must not open every cx.log
        int rectWas = _rectN;
        if (_rectN == 0) _rectN = 1;   // Produces: a page can exist, so a static light is the bake's
        var lamp = (IntPtr)0x557;
        var at = new Vector3(40f, 30f, 2f);
        const float Reach = 5000f;
        var white = Vector3.One;
        var dim = new Vector3(0.05f, 0.04f, 0.03f);

        // the keep: the close sees the lamp white, its first reading; the area's own colour arrives after
        Area(); Walk(lamp, at, Reach, white, true);
        check.Expect(_closed && _setN == 1 && _setCol[0] == white, "the set closed on the walk past LoadArea with the lamp's first reading");
        ulong closedOn = SetHash;
        Walk(lamp, at, Reach, dim, true); Walk(lamp, at, Reach, dim, true);
        check.Expect(_setN == 1 && _setCol[0] == dim,
                     $"a static light recoloured after the close is baked in its new colour, not the close's ({_setCol[0]})");
        check.Expect(SetHash != closedOn && _rekeyPending, "...under a new set hash, so every rect it touches re-keys and re-bakes");

        // a light still blending to its colour is held as the close saw it, until it settles
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, at, Reach, dim, false); Walk(lamp, at, Reach, dim, false);
        check.Expect(_setCol[0] == white && RecordDb(lamp, at, Reach, dim, false, false, out _),
                     "a light still blending stays the set's, as the close saw it -- no re-close per frame of a blend");

        // the same light a hair off is the same light
        Area(); Walk(lamp, at, Reach, white, true);
        ulong same = SetHash;
        Walk(lamp, at + new Vector3(0.01f, 0f, 0f), Reach * 1.0001f, white * 0.999f, true); Walk(lamp, at, Reach, white, true);
        check.Expect(SetHash == same && !_rekeyPending, "a light within its bins of the close is the set's light: nothing re-keys");

        // a lantern bobbing 0.2 u across a 4 u bin edge of the set hash is still the set's light
        var bob = new Vector3(41.9f, 30f, 2f);
        Area(); Walk(lamp, bob, Reach, white, true);
        ulong bobbed = SetHash;
        for (int k = 0; k < 6; k++) Walk(lamp, bob + new Vector3(k % 2 == 0 ? 0.2f : 0f, 0f, 0f), Reach, white, true);
        check.Expect(SetHash == bobbed && _drift.Closes == 0, "a light bobbing inside the 1 u a static light may drift re-closed the set");

        // the same light by identity, a hair off its close-time reach, recoloured: the same re-close
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, at, Reach + 1f, dim, true); Walk(lamp, at, Reach + 1f, dim, true);
        check.Expect(_setN == 1 && _setCol[0] == dim, $"a recoloured light matched by identity rather than key is baked in its new colour ({_setCol[0]})");

        // a light of the set moved off its place: baked where it is now, not left behind where it was
        var moved = at + new Vector3(20f, 0f, 0f);
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, moved, Reach, white, true); Walk(lamp, moved, Reach, white, true);
        check.Expect(_setN == 1 && new Vector3(_setPosR[0].X, _setPosR[0].Y, _setPosR[0].Z) == moved,
                     $"a set light moved 20 u after the close is baked where it stands ({_setPosR[0]})");
        // ...and one moved 2 u, inside its set bin but past the 1 u a static light may drift, is not lit twice
        var nudged = at + new Vector3(2f, 0f, 0f);
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, nudged, Reach, white, true); Walk(lamp, nudged, Reach, white, true);
        check.Expect(RecordDb(lamp, nudged, Reach, white, false, true, out _),
                     "a set light moved 2 u is live AND baked: it never re-joined the set");

        // a light recoloured every other walk re-closes eight times in an area, then the set stands
        Area(); Walk(lamp, at, Reach, white, true);
        int reclosed = 0;
        for (int k = 0; k < 40; k++)
        {
            ulong was = SetHash;
            Walk(lamp, at, Reach, k / 2 % 2 == 0 ? dim : white, true);
            if (SetHash != was) reclosed++;
        }
        check.Expect(reclosed == 8, $"a light that never stops changing re-closed the set {reclosed} times, not 8");

        // a light of the set that starts moving leaves it: lit live, with no baked copy left where it stood
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, moved, Reach, white, false); Walk(lamp, moved, Reach, white, false);
        check.Expect(_setN == 0 && !RecordDb(lamp, moved, Reach, white, false, false, out _),
                     $"a set light that started moving is still baked where it stood ({_setN} set lights)");

        _clsArea = IntPtr.Zero; Reopen(); _drift = new(); _loadArmed = false; SetHash = 0; _setN = 0; _stN = 0;
        _seen.Clear(); _seenPtr.Clear(); _rekeyPending = false; _rectN = rectWas;
        _log = live;
        log.Info(check.Ok ? "LightmapBake.DriftSelfCheck: passed"
                          : "LightmapBake.DriftSelfCheck: FAILED -- a light that changed after the close is baked as it was");
        return check.Ok;

        static void Area()
        {
            // the area already under the walk: an area CHANGE would repack, and a repack forgets every rect
            _clsArea = 1; _edgePending = false; _areaChanged = false; _loadArmed = true; SetHash = 0; _setN = 0;
            _drift = new(); Reopen();
        }

        static void Walk(IntPtr id, Vector3 pos, float r, Vector3 c, bool settled)
        {
            BeginWalkDb(1, 0);
            _rekeyPending = false;
            RecordDb(id, pos, r, c, false, settled, out _);
            EndWalk();
        }
    }
}
