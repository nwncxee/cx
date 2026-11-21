using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Headless: a static light that changes after the set closes is baked as it is now, not as the close saw
/// it. Oracle: hand-written lights and colours driven through the walk's own record and close calls.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The close-then-change cases: a recolour or a move re-closes the set on the light as it is now, a
    /// light a hair off does not, one no longer static or gone from the walk leaves the set, and a light that
    /// never stops changing re-closes a bounded number of times.</summary>
    internal static bool DriftSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.DriftSelfCheck");
        var heard = new System.Collections.Generic.List<string>();
        ExtenderLog? live = _log; _log = new ExtenderLog { Tap = heard.Add };   // the close's lines are evidence, heard here and not in cx.log
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
        heard.Clear();
        Walk(lamp, at, Reach, dim, true); Walk(lamp, at, Reach, dim, true);
        check.Expect(_setN == 1 && _setCol[0] == dim,
                     $"a static light recoloured after the close is baked in its new colour, not the close's ({_setCol[0]})");
        check.Expect(Heard("db light 557", "(40.00,30.00,2.00) r 5000.00 col (1.00,1.00,1.00)", "(40.00,30.00,2.00) r 5000.00 col (0.05,0.04,0.03)") == 1,
                     "...and cx.log names the light once, with its close-time and current reach and colour");
        check.Expect(SetHash != closedOn && _rekeyPending, "...under a new set hash, so every rect it touches re-keys and re-bakes");

        // a set light a looping colour track starts driving in place is no longer static: lit live, not baked white
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, at, Reach, white, false); Walk(lamp, at, Reach, white, false);
        check.Expect(_setN == 0 && !RecordDb(lamp, at, Reach, white, false, false, out _),
                     $"a set light no longer static where it stands is still baked as the close saw it ({_setN} set lights)");
        int keyedCloses = _drift.Closes;
        Walk(lamp, at, Reach, white, false); Walk(lamp, at, Reach, dim, false);
        check.Expect(_drift.Closes == keyedCloses, "...and once out of the set its colour track re-closes nothing");

        // ...and so is one a hair off its close-time place, matched by identity rather than key
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, at, Reach + 1f, white, false); Walk(lamp, at, Reach + 1f, white, false);
        check.Expect(_setN == 0, $"a set light a hair off its place and no longer static is still baked ({_setN} set lights)");

        // two set lights recoloured in one walk spend one re-close, not two
        var lamp2 = (IntPtr)0x558;
        var at2 = at + new Vector3(30f, 0f, 0f);
        Area(); Walk2(white, white);
        heard.Clear();
        Walk2(dim, dim);
        check.Expect(_drift.Closes == 1, $"two lights recoloured in one walk spent {_drift.Closes} re-closes, not 1");
        check.Expect(Heard("db light 557") == 1 && Heard("db light 558") == 1, "...and cx.log names both, not only the first");

        // a light dimmed in one channel alone is a different light
        var redOnly = new Vector3(0.2f, 1f, 1f);
        Area(); Walk(lamp, at, Reach, white, true);
        Walk(lamp, at, Reach, redOnly, true); Walk(lamp, at, Reach, redOnly, true);
        check.Expect(_setN == 1 && _setCol[0] == redOnly, $"a light dimmed in one channel after the close is baked as it was ({_setCol[0]})");

        // two set lights stacked on one place and reach, in two colours, are each their own entry: nothing re-closes
        Area();
        for (int q = 0; q < 4; q++) { BeginWalkDb(1, 0); RecordDb(lamp, at, Reach, white, false, true, out _); RecordDb(lamp2, at, Reach, dim, false, true, out _); EndWalk(); }
        check.Expect(_drift.Closes == 0, $"two stacked set lights of two colours, neither changed, spent {_drift.Closes} re-closes");
        check.Expect(!RecordDb(lamp2, at, Reach, dim, false, true, out _), "...and the one with no entry of its own, never baked, is lit live");

        // a lamp nudged half a unit onto its neighbour's exact place and reach is still itself, in its own colour
        var beside = at + new Vector3(0.5f, 0f, 0f);
        Area();
        for (int q = 0; q < 2; q++) { BeginWalkDb(1, 0); RecordDb(lamp, at, Reach, white, false, true, out _); RecordDb(lamp2, beside, Reach, dim, false, true, out _); EndWalk(); }
        for (int q = 0; q < 2; q++) { BeginWalkDb(1, 0); RecordDb(lamp, at, Reach, white, false, true, out _); RecordDb(lamp2, at, Reach, dim, false, true, out _); EndWalk(); }
        check.Expect(_drift.Closes == 0, $"a lamp nudged onto its neighbour's key was compared with the neighbour's colour ({_drift.Closes} re-closes)");

        // ...and one that moved onto it from 30 u away left the set: named, not hidden behind the neighbour's key
        Area(); Walk2(white, dim); Walk2(white, dim);
        heard.Clear();
        for (int q = 0; q < 2; q++) { BeginWalkDb(1, 0); RecordDb(lamp, at, Reach, white, false, true, out _); RecordDb(lamp2, at, Reach, dim, false, true, out _); EndWalk(); }
        check.Expect(Heard("db light 558") == 1, "a lamp moved 30 u onto its neighbour's key is still baked where it stood");

        // one lamp recoloured and the other switched off in one walk: one re-close, and the dark one is named
        Area(); Walk2(white, white); Walk2(white, white);
        heard.Clear();
        Walk(lamp, at, Reach, dim, true); Walk(lamp, at, Reach, dim, true);
        check.Expect(_setN == 1 && _drift.Closes == 1 && Heard("db light 558") == 1,
                     $"a lamp switched off in the walk another was recoloured in left the set unnamed ({_setN} set lights, {_drift.Closes} re-closes)");

        // a set that can no longer bake (no rect, no chart offered) has nothing to re-close: every light is live anyway
        Area(); Walk(lamp, at, Reach, white, true);
        _rectN = 0;
        Walk(lamp, at, Reach, white, true); Walk(lamp, at, Reach, white, true);
        check.Expect(_drift.Closes == 0, $"a set with no page possible re-closed {_drift.Closes} times as its lights went live");
        _rectN = 1;

        // an area of 300 static lights: a closed walk meets them all, and nine switched off are named, eight and a count
        Area(); Many(300); Many(300);
        check.Expect(_setN == 300 && _drift.Closes == 0, $"a closed walk over 300 set lights re-closed {_drift.Closes} times ({_setN} set lights)");
        heard.Clear();
        Many(291);
        check.Expect(Heard("db light") == 8 && heard.Count == 9,
                     $"nine lights switched off are not named as eight and a count ({Heard("db light")} named in {heard.Count} lines)");

        // a set light switched off, hidden or blacked out never reaches the record: its baked copy goes all the same
        Area(); Walk(lamp, at, Reach, white, true);
        ulong lit = SetHash;
        Empty(); Empty();
        check.Expect(_setN == 0 && SetHash != lit, $"a set light gone from the walk is still baked ({_setN} set lights)");
        Area(); Walk2(white, white); Walk2(white, white);   // the second walk is a closed one, and meets both
        heard.Clear();
        Walk(lamp, at, Reach, white, true); Walk(lamp, at, Reach, white, true);
        check.Expect(_setN == 1 && Heard("db light 558") == 1 && Heard("db light 557") == 0,
                     $"a second lamp switched off leaves the set and cx.log names it, not the lamp still lit ({_setN} set lights)");

        // the same light a hair off is the same light
        Area(); Walk(lamp, at, Reach, white, true);
        ulong same = SetHash;
        Walk(lamp, at + new Vector3(0.01f, 0f, 0f), Reach * 1.0001f, white * 0.999f, true); Walk(lamp, at, Reach, white, true);
        check.Expect(SetHash == same && !_rekeyPending && _drift.Closes == 0, "a light within its bins of the close is the set's light: nothing re-keys");

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
        heard.Clear();
        int reclosed = 0;
        for (int k = 0; k < 40; k++)
        {
            ulong was = SetHash;
            Walk(lamp, at, Reach, k / 2 % 2 == 0 ? dim : white, true);
            if (SetHash != was) reclosed++;
        }
        check.Expect(reclosed == 8, $"a light that never stops changing re-closed the set {reclosed} times, not 8");
        int atEnd = heard.Count;
        Walk(lamp, at, Reach, dim, true); Walk(lamp, at, Reach, white, true);
        check.Expect(heard.Count == atEnd, $"past the cap a change still adds {heard.Count - atEnd} cx.log lines: the cap is said once");
        // the eight are per area: past an area edge a recolour re-closes again
        _edgePending = true; _loadArmed = true;
        Walk(lamp, at, Reach, white, true);
        if (_rectN == 0) _rectN = 1;
        Walk(lamp, at, Reach, dim, true); Walk(lamp, at, Reach, dim, true);
        check.Expect(_setN == 1 && _setCol[0] == dim, $"after an area edge a recolour no longer re-closes the set ({_setCol[0]})");

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
            if (_rectN == 0) _rectN = 1;   // a case that re-closed on no light repacked
        }

        int Heard(params string[] parts)
        {
            int n = 0;
            foreach (string line in heard) if (Array.TrueForAll(parts, t => line.Contains(t, StringComparison.Ordinal))) n++;
            return n;
        }

        void Walk2(Vector3 c1, Vector3 c2)
        {
            BeginWalkDb(1, 0);
            _rekeyPending = false;
            RecordDb(lamp, at, Reach, c1, false, true, out _);
            RecordDb(lamp2, at2, Reach, c2, false, true, out _);
            EndWalk();
        }

        static void Many(int n)
        {
            BeginWalkDb(1, 0);
            for (int i = 0; i < n; i++) RecordDb((IntPtr)(0x1000 + i), new Vector3(10f * i, 0f, 2f), 20f, Vector3.One, false, true, out _);
            EndWalk();
        }

        static void Empty() { BeginWalkDb(1, 0); _rekeyPending = false; EndWalk(); }

        static void Walk(IntPtr id, Vector3 pos, float r, Vector3 c, bool isStatic)
        {
            BeginWalkDb(1, 0);
            _rekeyPending = false;
            RecordDb(id, pos, r, c, false, isStatic, out _);
            EndWalk();
        }
    }
}
