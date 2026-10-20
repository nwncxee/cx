using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.IO;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>A one-shot dump of what the Alloy colour pass submits, as a file two runs of the same area from
/// the same camera can be diffed. Armed by <c>/framedump</c>, consumed by the very next <c>BuildBatches</c>,
/// written on the render thread and then disarmed. CANONICAL, not chronological: enrollment hashes POINTERS,
/// so every row is sorted on its own content and every runtime identity is replaced by a derived one.</summary>
internal static unsafe class FrameDump
{
    /// <summary>Set by <c>/alloy framedump</c>, cleared by the frame that serves it. Not volatile-guarded beyond
    /// this: a missed pickup costs one more frame, and the command is idempotent.</summary>
    internal static bool Armed;

    /// <summary>The <c>alloy.framedump</c> button -- a one-shot ACTION. The next colour pass serves it; the
    /// log line names what the PREVIOUS one wrote, because the render thread is the only place the answer
    /// exists, and that is what turns "run it twice and diff" into a loop a human can drive.</summary>
    internal static void Arm()
    {
        Armed = true;
        string prev = LastDraws == 0 ? "" : $" (last: {LastDraws} draws, {LastDistinct} distinct keys, {LastGroups} groups minted)";
        KnobGroup.Log?.Info($"[framedump] armed -- writing to {Dir} on the next Alloy pass{prev}");
    }

    /// <summary>The number the batching walk cuts groups on run-length: rows carry their FULL key, so the
    /// distinct-key count falls out of the same sort that makes the dump diffable. Kept after a dump so the
    /// command's reply can report it.</summary>
    internal static int LastGroups, LastDistinct, LastDraws;

    private struct Row
    {
        public string Key;    // the batch key, minus the VAO -- what a group is allowed to merge on
        public string Draw;   // this draw's geometry + its SSBO record
        public int Vao;       // raw GL name; replaced by an ordinal before writing
        /// <summary>The item's byte offset and base vertex inside the VDO's SHARED pools. Held so the rows
        /// carry their full identity in memory, and DELIBERATELY NEVER WRITTEN: they are allocation offsets,
        /// so they move between runs the way a pointer does, and ordinalising them is worse -- a dense counter
        /// renumbers the whole tail on any scene drift. What is lost is telling two same-sized items apart.</summary>
        public long Off;
        public int Base;
    }

    private static readonly List<Row> _rows = new();
    private static bool _capturing;

    internal static string Dir => Native.ConfigPath("cx_frame_dump");

    /// <summary>Open the capture for one <c>BuildBatches</c>. False = not armed, and the hot loop's row call
    /// stays a predicted-not-taken branch.</summary>
    internal static bool Begin()
    {
        if (!Armed) return false;
        Armed = false;
        _rows.Clear();
        _capturing = true;
        return true;
    }

    internal static bool Capturing => _capturing;

    /// <summary>One submitted draw, called from the staging walk with everything in hand.
    /// <para>TWO strings, not one. <b>Key</b> is the batcher's OWN cut key reproduced exactly, including the
    /// bindless merge and the normalised blend pair, so counting distinct Keys counts the groups it would
    /// mint. <b>Show</b> is the human column: the texture NAMES, which under merge belong on the draw.</para></summary>
    internal static void Row_(int cls, bool lit, int vao, int recipe, uint mode, int feat, bool dis,
                              int blend, bool noCull, int slots, bool merge, int idxCount, nint off,
                              int baseVertex, in DrawRecordBlock r)
    {
        var k = new StringBuilder(128);
        k.Append("cls=").Append(((AlloyClass)cls).ToString())
         .Append(" lit=").Append(lit ? 1 : 0)
         .Append(" mode=0x").Append(mode.ToString("x", CultureInfo.InvariantCulture))
         .Append(" feat=").Append(feat)
         .Append(" dis=").Append(dis ? 1 : 0)
         .Append(" blend=").Append(blend)
         // #292: the material's `twosided`. In the dump because it is now a batch-key term AND the one piece
         // of a part's state that can make it vanish outright rather than look wrong -- a mesh drawn under
         // the culling it did not ask for shows nothing at all, and no other column would say so.
         .Append(" nocull=").Append(noCull ? 1 : 0);
        // Under merge the batcher compares neither the diffuse id nor the seven slot ids, so a key that
        // carried them would report fragmentation the cut does not have.
        // The mask is in the key either way -- it is what keeps a material declaring the EnvSlot bit (64,
        // which has no texture behind it) from merging with one that does not.
        k.Append(" mask=").Append(slots);
        if (merge) k.Append(" tex=<merged>");
        else
        {
            // ...and the NON-merge cut compares all seven slot ids against the group's, so they are key
            // material here exactly as much as they are not key material above.
            k.Append(" tex=").Append(SlotName(recipe, -1, true));
            AppendSlots(k, recipe, slots);
        }

        var d = new StringBuilder(320);
        d.Append("n=").Append(idxCount);
        // Only under merge: without it the names are already on the G line, and repeating them per draw
        // would triple the file for nothing.
        if (merge)
        {
            d.Append(" tex=").Append(SlotName(recipe, -1, true));
            AppendSlots(d, recipe, slots);
        }
        d.Append(" m=");
        Matrix4x4 mdl = r.Model;
        F(d, mdl.M11); d.Append(','); F(d, mdl.M12); d.Append(','); F(d, mdl.M13); d.Append(','); F(d, mdl.M14); d.Append(',');
        F(d, mdl.M21); d.Append(','); F(d, mdl.M22); d.Append(','); F(d, mdl.M23); d.Append(','); F(d, mdl.M24); d.Append(',');
        F(d, mdl.M31); d.Append(','); F(d, mdl.M32); d.Append(','); F(d, mdl.M33); d.Append(','); F(d, mdl.M34); d.Append(',');
        F(d, mdl.M41); d.Append(','); F(d, mdl.M42); d.Append(','); F(d, mdl.M43); d.Append(','); F(d, mdl.M44);
        d.Append(" rot="); F(d, r.Cos); d.Append(','); F(d, r.Sin);
        // the lit lanes are written by the LIT branch alone, and _dData is allocated once and never cleared --
        // so printing them for an unlit draw serialises whatever the previous frame left in that slot, and
        // slot assignment follows enrollment order, which is pointer-hash order. That is a diff that changes
        // between runs for no reason connected to the code under test, which is the one thing this file
        // exists not to do.
        if (lit)
        {
            d.Append(" amb="); F(d, r.AmbR); d.Append(','); F(d, r.AmbG); d.Append(','); F(d, r.AmbB);
            if (cls != (int)AlloyClass.Water)
            { d.Append(" dif="); F(d, r.DifR); d.Append(','); F(d, r.DifG); d.Append(','); F(d, r.DifB); }
            d.Append(" emi="); F(d, r.EmiR); d.Append(','); F(d, r.EmiG); d.Append(','); F(d, r.EmiB);
            d.Append(" par=");
            F(d, r.PbrR); d.Append(','); F(d, r.PbrG); d.Append(','); F(d, r.PbrB); d.Append(','); F(d, r.Slots); d.Append(',');
            F(d, r.PbrX); d.Append(','); F(d, r.PbrY); d.Append(','); F(d, r.PbrZ); d.Append(','); F(d, r.PbrW);
        }
        // #147's water body overwrites the diffuse slot whether or not the batch was lit, so it is the one
        // group of four an unlit draw does have written.
        if (cls == (int)AlloyClass.Water)
        {
            d.Append(" body=");
            F(d, r.DifR); d.Append(','); F(d, r.DifG); d.Append(','); F(d, r.DifB); d.Append(','); F(d, r.DifW);
        }
        d.Append(" f4=");
        F(d, r.FeatX); d.Append(','); F(d, r.FeatY); d.Append(','); F(d, r.FeatZ); d.Append(','); F(d, r.FeatW);
        // Everything past here varies with time rather than with the code, so a diff of an animated scene
        // can cut the line at the bar instead of reading around it.
        d.Append(" | dissolve="); F(d, r.Dissolve);
        d.Append(" alpha="); F(d, r.Alpha);

        _rows.Add(new Row { Key = k.ToString(), Draw = d.ToString(), Vao = vao, Off = off, Base = baseVertex });
    }

    private static void AppendSlots(StringBuilder b, int recipe, int slots)
    {
        b.Append(" slots=");
        for (int s = 0; s < AlloyRenderer.MapSlots; s++)
        {
            if (s != 0) b.Append('|');
            b.Append(SlotName(recipe, s, (slots & AlloyRenderer.SlotBit(s)) != 0));
        }
    }

    /// <summary>Put the rows in an order derived from their content alone, fold the run-assigned VAO name into
    /// an ordinal, and return the count of DISTINCT full keys. Two sorts, not one: the ordinal can only be
    /// assigned once an order exists, and it has to be in the key afterwards or two groups differing only by
    /// VAO interleave. ponytail: add the VDO's item count as a tiebreak if a diff shows a pure vao= flip.</summary>
    private static int Canonicalize()
    {
        _rows.Sort(Compare);
        // The VAO ordinal is assigned in THIS order -- derived from content alone -- which is what makes it
        // reproducible. It works because there are a handful of VAOs and both runs see all of them; the same
        // trick does NOT extend to per-item ordinals, see Row.Off.
        var vaoOrd = new Dictionary<int, int>();
        for (int i = 0; i < _rows.Count; i++)
            if (!vaoOrd.ContainsKey(_rows[i].Vao)) vaoOrd[_rows[i].Vao] = vaoOrd.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            r.Key = $"vao={vaoOrd[r.Vao]} {r.Key}";
            _rows[i] = r;
        }
        _rows.Sort(Compare);
        int distinct = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (i == 0 || _rows[i].Key != _rows[i - 1].Key) distinct++;
        return distinct;
    }

    private static int Compare(Row a, Row b)
    {
        int c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : string.CompareOrdinal(a.Draw, b.Draw);
    }

    /// <summary>Off-device: the canonical form really is independent of the order the rows arrived in, which
    /// is the ONE property the whole harness rests on -- a dump that reorders between runs is not a diff
    /// surface, and enrollment order is pointer-hash order, so it does reorder. Two shuffles of the same
    /// three rows on two VAOs must produce byte-identical output and the same distinct-key count.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        // The pool offsets differ between the two shuffles exactly as they differ between two runs of the
        // client -- same geometry, repacked. If they reach the output, this fails.
        string A = Render(new[] { (1, "b", "y", 900L), (2, "a", "x", 100L), (1, "b", "x", 500L) });
        string B = Render(new[] { (1, "b", "x", 41L), (1, "b", "y", 77L), (2, "a", "x", 12L) });
        bool ok = A == B && LastDistinct == 2;
        if (!ok) log.Info($"FrameDump.SelfCheck: FAILED -- canonical form depends on arrival order ({LastDistinct} keys)");
        ok &= DbNames(log);
        if (ok) log.Info("FrameDump.SelfCheck: passed");
        _rows.Clear();
        return ok;

        static string Render((int vao, string key, string draw, long off)[] rows)
        {
            _rows.Clear();
            foreach (var r in rows)
                _rows.Add(new Row { Vao = r.vao, Key = r.key, Draw = r.draw, Off = r.off, Base = (int)r.off });
            LastDistinct = Canonicalize();
            var sb = new StringBuilder();
            foreach (Row r in _rows) sb.Append(r.Key).Append('/').Append(r.Draw).Append(';');
            return sb.ToString();
        }
    }

    /// <summary>Off-device: a DB draw's row NAMES its textures. A database part has no engine
    /// <c>Material</c>, so the names come off its RECIPE -- without which every name column in the file reads
    /// "-" in the mode that is now the only one. Oracle: the two resrefs are written into the mesh by hand
    /// here, and the row must carry both.</summary>
    private static bool DbNames(ExtenderLog log)
    {
        var mesh = new Mdl.MeshDef
        {
            Positions = new[] { System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY },
            Indices = new uint[] { 0, 1, 2 },
            BMin = -System.Numerics.Vector3.One, BMax = System.Numerics.Vector3.One,
            Textures = new[] { "cx_fd_diffuse", "cx_fd_normal" },
            RenderHintId = 2,   // without a tangent-frame hint a model's texture1 is no map slot
        };
        var model = new Mdl.ModelDef { Name = "cx_fd_dump", ContentHash = 0xFD00FD00UL };
        model.Nodes = new[]
        {
            new Mdl.NodeDef { Name = "root", Index = 0, Kind = Mdl.NodeKind.Dummy },
            new Mdl.NodeDef { Name = "body", Index = 1, Parent = 0, Kind = Mdl.NodeKind.TriMesh, Mesh = mesh },
        };
        model.NodeByName["root"] = 0;
        model.NodeByName["body"] = 1;
        int recipe = Db.DrawRecipe.Intern(model, 1, plan: false);
        if (recipe < 0) { log.Info("FrameDump.SelfCheck: FAILED -- the recipe table would not intern the check's mesh"); return false; }
        var r = new DrawRecordBlock();
        _rows.Clear();
        Row_((int)AlloyClass.Cutout, lit: true, vao: 1, recipe: recipe, mode: 4, feat: 0,
             dis: false, blend: 0, noCull: false, slots: AlloyRenderer.SlotBit(0), merge: true, idxCount: 3,
             off: 0, baseVertex: 0, in r);
        bool ok = _rows.Count == 1 && _rows[0].Draw.Contains("tex=cx_fd_diffuse", StringComparison.Ordinal)
                                   && _rows[0].Draw.Contains("cx_fd_normal", StringComparison.Ordinal);
        if (!ok)
            log.Info("FrameDump.SelfCheck: FAILED -- a DB draw's row does not name its textures: "
                     + (_rows.Count == 1 ? _rows[0].Draw : $"{_rows.Count} rows"));
        _rows.Clear();
        Db.DrawRecipe.Forget(model);
        Db.DrawRecipe.Pump();
        Db.DrawRecipe.Pump();
        return ok;
    }

    private static void F(StringBuilder b, float v) => b.Append(v.ToString("F4", CultureInfo.InvariantCulture));

    /// <summary>One slot's texture name off the part's RECIPE: <paramref name="s"/> -1 is the diffuse,
    /// 0.. a map slot. <paramref name="declared"/> is the material's own slot mask, so a slot the draw does
    /// not declare reads as absent rather than as whatever the recipe still interned there.</summary>
    private static string SlotName(int recipe, int s, bool declared)
    {
        if (!declared || recipe < 0) return "-";
        string n = Db.TexRegistry.ResrefOf(Db.DrawRecipe.Tex(recipe, s < 0 ? 0 : s + 1));
        return n.Length == 0 ? "-" : n;
    }

    /// <summary>Close the capture: canonicalise, count the distinct full keys against the groups the
    /// run-length cut actually minted, and write the file.</summary>
    internal static void Finish(int groups, ExtenderLog? log)
    {
        if (!_capturing) return;
        _capturing = false;

        int distinct = Canonicalize();
        LastGroups = groups; LastDistinct = distinct; LastDraws = _rows.Count;

        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, "frame.txt");
            using var w = new StreamWriter(path, false);
            w.WriteLine("# NWNCX:EE frame dump (#192) -- one G line per distinct BATCH KEY (the");
            w.WriteLine("# batcher's own cut key, merge and blend normalisation included), one D line per");
            w.WriteLine("# submitted draw. Canonical order, not submission order: see FrameDump's summary.");
            w.WriteLine("# Under #153's bindless merge the texture names sit on the D lines, because that is");
            w.WriteLine("# where they belong -- a merged group does not split on them.");
            w.WriteLine("# Everything right of the | is time-varying; cut it before diffing an animated scene.");
            w.WriteLine("# The diff is only as reproducible as the SCENE. Creatures animate, so their rows");
            w.WriteLine("# differ every frame; a camera that is not identical to the pixel changes which");
            w.WriteLine("# tiles are in view. Dump from a fixed vantage with nothing alive in shot, and read");
            w.WriteLine("# a residual of moving rows as the scene, not as the renderer.");
            w.WriteLine($"# draws={_rows.Count} distinct-keys={distinct} groups-minted={groups} " +
                        $"fragmentation={groups - distinct}");
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i == 0 || _rows[i].Key != _rows[i - 1].Key)
                {
                    int n = 1;
                    for (int j = i + 1; j < _rows.Count && _rows[j].Key == _rows[i].Key; j++) n++;
                    w.WriteLine($"G count={n} {_rows[i].Key}");
                }
                w.WriteLine("  D " + _rows[i].Draw);
            }
            log?.Info($"[framedump] {_rows.Count} draws, {distinct} distinct keys, {groups} groups minted " +
                      $"-> {path}");
        }
        catch (Exception e)
        {
            log?.Info($"[framedump] write to {Dir} failed -- {e.Message}");
        }
        _rows.Clear();
    }
}
