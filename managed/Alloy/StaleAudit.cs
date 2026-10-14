using System;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The DB-mode staleness audit: does what the GPU is about to draw still say what the database says? Only a
/// record on the dirty list is uploaded, so a record that changes without being marked draws stale for good.
/// Two comparisons catch it -- every per-record buffer against a mirror taken as it was pushed, and the
/// once-per-seating columns recomputed from the live database. <c>alloy.staleaudit</c> = frames between audits.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    private static float[] _saRec = Array.Empty<float>();
    private static ulong[] _saTexH = Array.Empty<ulong>();
    private static bool[] _saSaid = Array.Empty<bool>();
    /// <summary>The frame each slot last ENTERED the DB staging walk, and the frame it last reached the PLT
    /// decision in it. A stale record that was never entered is a slot nothing draws; one that was entered
    /// and did not reach the decision bailed on the way (no resident geometry, a pass that skips it).</summary>
    private static long[] _saEnter = Array.Empty<long>(), _saPltAt = Array.Empty<long>();
    private static long _saFrame, _saAudits, _saMismatch, _saParts;

    /// <summary>Is the audit armed? Read on the upload path, so it is one static load on a cold knob.</summary>
    internal static bool StaleAuditOn => Renderer.StaleAudit.Boot > 0;

    /// <summary>Parts named in the log before it goes quiet. A systemic miss would otherwise print a line per
    /// part of every creature in the area.</summary>
    private const int StaleAuditNamed = 24;

    /// <summary>Armed AND this slot's stamp array already exists. The staging walk runs on workers and may
    /// reach a slot before the main thread has allocated the audit's arrays, so the length IS the guard --
    /// the arrays are never resized, only allocated once.</summary>
    private static bool StaleAuditArmed(int i) => StaleAuditOn && (uint)i < (uint)_saEnter.Length;

    private static void StaleAuditAlloc()
    {
        int cap = MaxRecords;
        if (_saRec.Length == 0) _saRec = new float[(long)cap * DrawFloats];
        if (_saTexH.Length == 0) _saTexH = new ulong[(long)cap * 8];
        if (_saSaid.Length == 0) _saSaid = new bool[cap];
        if (_saEnter.Length == 0) { _saEnter = new long[cap]; _saPltAt = new long[cap]; }
    }

    /// <summary>Mirror the record floats a record upload just pushed. <paramref name="whole"/> is the
    /// wholesale path; otherwise the sorted dirty slots are the runs that went up.</summary>
    internal static void StaleMirrorRecords(int cap, bool whole, int[] dirty, int dirtyN)
    {
        if (!StaleAuditOn) return;
        StaleAuditAlloc();
        if (whole) { Array.Copy(_dData, _saRec, Math.Min((long)cap * DrawFloats, _saRec.Length)); return; }
        for (int d = 0; d < dirtyN; d++)
        {
            int s = dirty[d];
            if ((uint)s >= (uint)cap) continue;
            Array.Copy(_dData, (long)s * DrawFloats, _saRec, (long)s * DrawFloats, DrawFloats);
        }
    }

    /// <summary>Mirror the bindless handle table a handle upload just pushed.</summary>
    internal static void StaleMirrorHandles(int cap, bool whole, int[] dirty, int dirtyN)
    {
        if (!StaleAuditOn) return;
        StaleAuditAlloc();
        if (whole) { Array.Copy(_dTexH, _saTexH, Math.Min((long)cap * 8, _saTexH.Length)); return; }
        for (int d = 0; d < dirtyN; d++)
        {
            int s = dirty[d];
            if ((uint)s >= (uint)cap) continue;
            Array.Copy(_dTexH, (long)s * 8, _saTexH, (long)s * 8, 8);
        }
    }

    /// <summary>One audit, if the period says this frame. Called with the frame's records staged AND
    /// uploaded, so a difference against a mirror is a record the upload did not carry -- not one that is
    /// merely about to be sent.</summary>
    internal static void StaleAuditTick()
    {
        int period = Renderer.StaleAudit.Boot;
        if (period <= 0) return;
        if (++_saFrame % period != 0) return;
        StaleAuditAlloc();
        _saAudits++;
        int bad = 0, parts = 0, unreached = 0, never = 0;
        for (int i = 0; i < _ownCount; i++)
        {
            int part = _oDbPart[i];
            if (part < 0) continue;
            parts++;
            // A slot the staging walk did not reach THIS frame is not drawn this frame -- the visible-set
            // query never returned its object -- so whatever its record says is not what the frame shows.
            // Counted on its own: a record that is never reached is a culling question, not a staleness one.
            if (_saEnter[i] != _frameSeq)
            {
                unreached++;
                if (_saEnter[i] == 0) never++;
                continue;
            }
            string? why = StaleAuditSlot(i, part);
            if (why == null) continue;
            bad++;
            _saMismatch++;
            if (_saSaid[i] || _saMismatch > StaleAuditNamed) continue;
            _saSaid[i] = true;
            DbId obj = SceneDb.PartObj[part];
            int o = obj.Index;
            string model = (uint)o < SceneDb.MaxObjects ? SceneDb.ObjSeamModelName[o] : "?";
            bool vis = (uint)o < SceneDb.MaxObjects && SceneDb.ObjVisible[o];
            // The visible-set query's own predicates, as columns: a record nothing stages failed one of them.
            string gate = $"scene={SceneDb.ObjScene[o].Value} objlive={(SceneDb.Objects.LiveAt(o) ? 1 : 0)} "
                        + $"partlive={(SceneDb.Parts.LiveAt(part) ? 1 : 0)} "
                        + $"enabled={(SceneDb.PartEnabled[part] ? 1 : 0)} merged={(SceneDb.PartMerged[part] ? 1 : 0)} "
                        + $"lodtree={SceneDb.PartLodTree[part]}/shown={SceneDb.ObjLodShown[o]} "
                        + $"partslot={SceneDb.PartSlot[part]} range={SceneDb.ObjPartFirst[o]}+{SceneDb.ObjPartCount[o]}";
            long age = _frameSeq - _saEnter[i], pltAge = _frameSeq - _saPltAt[i];
            _log?.Always($"StaleAudit: slot={i} part={part} obj={obj.Value} model='{model}' "
                       + $"node='{PartTree.NameOf(part)}' vis={(vis ? 1 : 0)} "
                       + $"staged={(_saEnter[i] == 0 ? "NEVER" : age + " frame(s) ago")} "
                       + $"pltstep={(_saPltAt[i] == 0 ? "NEVER" : pltAge + " frame(s) ago")} {gate} -- {why}");
        }
        _saParts = parts;
        _log?.Always($"StaleAudit: audit {_saAudits} over {parts} live records ({unreached} the stage did not "
                   + $"reach this frame, {never} it has never reached) -- "
                   + (bad == 0 ? "0 stale" : $"{bad} STALE (the GPU draws what the database no longer says)"));
    }

    /// <summary>What is stale about slot <paramref name="i"/>, or null. Order matters only for the report:
    /// the first difference names the field, and a slot with several names the first one found.</summary>
    private static string? StaleAuditSlot(int i, int part)
    {
        // The upload mirrors: a value the CPU holds and the GPU does not.
        long rb = (long)i * DrawFloats;
        for (int f = 0; f < DrawFloats; f++)
            if (_dData[rb + f] != _saRec[rb + f])
                return $"record float {f} is {_dData[rb + f]:R} on the CPU and {_saRec[rb + f]:R} on the GPU "
                     + "(the record changed without MarkRecordDirty)";
        if (_bindless)
        {
            long tb = (long)i * 8;
            for (int s = 0; s < 8; s++)
                if (_dTexH[tb + s] != _saTexH[tb + s])
                    return $"texture handle {s} is 0x{_dTexH[tb + s]:x} on the CPU and 0x{_saTexH[tb + s]:x} "
                         + "on the GPU (the handle table changed without a dirty slot)";
        }
        string? plt = PltColors.MirrorDiff(i);
        if (plt != null) return plt;

        // The seat columns: a part whose recipe moved under a slot nobody re-seated.
        int recipe = _oDbRecipe[i], live = SceneDb.PartRecipe[part];
        if (live >= 0 && live != recipe)
            return $"recipe {recipe} is seated but the part's is {live} (the slot was not re-seated)";
        if (recipe >= 0)
        {
            byte cls = DbClsOf(part, recipe, _oWash[i] != 0);
            if (_oCls[i] != cls)
                return $"class {(AlloyClass)_oCls[i]} is seated, the recipe says {(AlloyClass)cls}";
            // The env bit is the one slot flag StageDbPart re-decides per frame; the rest ride the seat.
            bool env = DbEnvMapped(part, DbTexOf(part, recipe, 0));
            if (env != ((_oSlots[i] & EnvSlot) != 0))
                return $"env-map bit is {((_oSlots[i] & EnvSlot) != 0 ? 1 : 0)}, the database says {(env ? 1 : 0)}";
            // What the PLT resolve is keyed on: the flag the record carries against the diffuse it draws.
            bool isPlt = TexRegistry.KindOf(DbTexOf(part, recipe, 0)) == TexKind.Plt;
            if (isPlt != PltColors.Flagged(i))
                return $"PLT flag is {(PltColors.Flagged(i) ? 1 : 0)}, the diffuse "
                     + $"'{TexRegistry.ResrefOf(DbTexOf(part, recipe, 0))}' says {(isPlt ? 1 : 0)}";
            if (isPlt && PltColors.ExpectedDiff(i, SceneDb.PartObj[part], part) is string rows) return rows;
        }
        return null;
    }

    /// <summary>The audit's standing, for <c>/alloy</c>.</summary>
    internal static string StaleAuditNote()
        => Renderer.StaleAudit.Boot <= 0 ? ""
         : $"staleaudit: {_saAudits} audits over {_saParts} records, {_saMismatch} stale";
}
