using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The TEXTURE and material-parameter half of the Aurora seam: the <see cref="SeamBridge"/> entries
/// standing in for <c>Gob::SetTextureAttributes</c> @0x43f9d0, <c>SetOverrideTextureFilter</c> @0x43b600 and
/// <c>SetMaterialParam</c> @0x43cb20, over <see cref="TexRegistry"/> and <see cref="DrawRecipe"/>. Each is
/// change-driven: it writes a column and marks the records that read it, never per frame.</summary>
internal static class TexSeam
{
    private static ExtenderLog? _log;

    /// <summary>Point every bridge entry at its implementation. Called from <c>SeamService.Initialize</c>,
    /// beside the other streams' assignments.</summary>
    internal static void Wire(ExtenderLog log)
    {
        _log = log;
        SeamBridge.SetTextureAttributes = TextureAttributes;
        SeamBridge.SetTextureFilterOverride = FilterOverride;
        SeamBridge.SetMaterialParam = MaterialParam;
    }

    // ---- textures ----
    //
    // ReplaceTexture / RestoreTexture / SetEnvironmentMap are NOT here. ObjectSlots reaches ObjectTex (and,
    // for the PLT form's layer rows, PltColors.SetRows) directly, so a bridge entry for them would be a
    // delegate nothing calls -- a refusal counter that can never fire and a second place to look. The
    // round-trip they have to satisfy is still checked below, against the real ObjectTex.

    /// <summary><c>Gob::SetTextureAttributes(texName, txiName)</c>. <b><paramref name="txiName"/> is a RESREF,
    /// not inline text</b>: <c>CAurTextureBasic::Init</c> @0x488940 does <c>AurResGet(txiName, ".txi")</c> and
    /// parses it. Alloy honours the sampler keys <c>clamp</c>, <c>filter</c> and <c>mipmap</c>, names the rest
    /// once, returns non-zero when the object HAS that texture, and marks only the asking object's records.</summary>
    private static int TextureAttributes(DbId obj, string? texName, string? txiName)
    {
        if (!SceneDb.Objects.Valid(obj) || string.IsNullOrEmpty(txiName)) return 0;
        string? txi = Txi(txiName!);
        if (txi == null)
        {
            _txiMissing++;
            if (_unhandledOnce.Add("res:" + txiName))
                _log?.Always($"TexSeam: SetTextureAttributes(\"{texName}\", \"{txiName}\") names a .txi resource "
                           + "that is not in the resource tables -- nothing is applied");
            return 0;
        }
        _txiCalls++;
        Parse(txi, out bool? clamp, out bool? nearest, out bool? mipmap, out string? unknown);
        if (unknown != null)
        {
            // Counted per distinct KEY, not per call: one GUI texture pack is applied to every GUI texture,
            // and 282 copies of one unmodelled field is one gap, not 282.
            if (_unhandledOnce.Add(unknown))
            {
                _unhandled++;
                _log?.Always($"TexSeam: {txiName}.txi names \"{unknown}\", which is not sampler state Alloy's "
                           + "registry models -- that key is IGNORED (clamp, filter and mipmap are honoured). "
                           + "Nothing else about the texture changes");
            }
        }
        int hits = 0, changed = 0;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + count; p++)
            for (int slot = 0; slot < 4; slot++)
            {
                TexId id = SceneDb.PartTex(p, slot);
                if (id.IsNull) continue;
                if (!string.IsNullOrEmpty(texName)
                    && !string.Equals(TexRegistry.ResrefOf(id), texName, StringComparison.OrdinalIgnoreCase)) continue;
                hits++;
                if (!TexRegistry.RequestSampler(id, clamp, nearest, mipmap)) continue;
                SceneDb.TouchPart(p, Dirty.Texture);
                changed++;
            }
        if (changed > 0) SceneDb.Touch(obj, Dirty.Texture);
        _txiApplied += changed;
        return hits > 0 ? 1 : 0;   // the engine's own answer: it found textures, not that a field took
    }

    /// <summary>The text of the <c>.txi</c> resource named <paramref name="name"/>, or null. Game thread: it
    /// reads the engine's resource tables, which is where <c>AurResGet</c> read it too.</summary>
    private static string? Txi(string name)
    {
        if (_txiCache.TryGetValue(name, out string? had)) return had;
        string? text = TexRegistry.ReadResourceText(name, TxiResType);
        _txiCache[name] = text;
        return text;
    }
    /// <summary>The module edge: a server's <c>.txi</c> must not be shadowed by the previous module's answer.</summary>
    internal static void ModuleEdge() => _txiCache.Clear();
    /// <summary><c>CResHelper&lt;CResTXI, 2022&gt;</c>.</summary>
    private const ushort TxiResType = 2022;   // not-an-offset: restype id (TXI)
    private static readonly Dictionary<string, string?> _txiCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>Gob::SetOverrideTextureFilter</c>: nearest rather than linear on every texture of the object.
    /// Vanilla scoped it to the part's material; this sets the shared registry slot, so every user of the texture
    /// in either pass follows, and the first call is named with its caller chain.</summary>
    private static void FilterOverride(DbId obj, bool on)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        if (_unhandledOnce.Add("SetOverrideTextureFilter"))
            _log?.Always($"TexSeam: SetOverrideTextureFilter({on}) reached on o={obj.Value} model='{SceneDb.ObjSeamModelName[obj.Index]}' "
                       + $"by {SeamCallers.Chain(4)} -- applied to the SHARED texture, so every GUI and world user of it changes");
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + count; p++)
            for (int slot = 0; slot < 4; slot++)
            {
                TexId id = SceneDb.PartTex(p, slot);
                if (id.IsNull || !TexRegistry.RequestSampler(id, null, on, null)) continue;
                SceneDb.TouchPart(p, Dirty.Texture);
            }
    }

    /// <summary>The object record is going: drop the state kept beside it. A <see cref="DbId"/> index is
    /// RECYCLED, and neither the PLT colour rows nor a material-param override is derived from what the next
    /// tenant sets, so without this edge a new creature inherits the dead one's skin, hair and armour colours.
    /// Called from <c>ObjectSlots.ReleaseRecord</c>, beside the other per-record frees.</summary>
    internal static void ObjectDied(DbId obj)
    {
        PltColors.Free(obj);
        MaterialParams.Forget(obj);
        if ((uint)obj.Index < SceneDb.MaxObjects) SceneDb.ObjEnvMap[obj.Index] = null;
    }

    /// <summary>A <c>.txi</c> fragment, for the three keys that are sampler state. False with
    /// <paramref name="unknown"/> set when it names something else -- which is reported, not ignored.</summary>
    internal static bool Parse(string attrs, out bool? clamp, out bool? nearest, out bool? mipmap, out string? unknown)
    {
        clamp = nearest = mipmap = null; unknown = null;
        bool ok = true;
        foreach (string line in attrs.Split('\n', '\r'))
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            int sp = t.IndexOfAny(new[] { ' ', '\t' });
            string key = sp < 0 ? t : t[..sp];
            string val = sp < 0 ? "" : t[(sp + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                // The engine's clamp field is two shorts at CAurTextureBasic+0xc8 (one per axis); Alloy's
                // registry has one wrap mode for both, so a per-axis clamp becomes "clamped".
                case "clamp": clamp = Truthy(val); break;
                case "filter": nearest = Truthy(val) == false; break;
                case "mipmap": mipmap = Truthy(val); break;
                default: ok = false; unknown ??= key; break;
            }
        }
        return ok;
    }

    /// <summary>The <c>.txi</c> truth values the engine's <c>ParseField</c> takes: a non-zero number.</summary>
    private static bool Truthy(string v)
        => !(v.Length == 0 || v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));

    private static long _unhandled, _txiMissing, _txiApplied, _txiCalls;
    private static readonly HashSet<string> _unhandledOnce = new(StringComparer.OrdinalIgnoreCase);

    // ---- material parameters ----

    /// <summary><c>Gob::SetMaterialParam</c>. The arguments are the engine's, in the engine's order:
    /// <paramref name="paramName"/> is matched against each parameter's name, <paramref name="value"/> is the
    /// new value (<b>empty resets it</b> to the material's declared default), and
    /// <paramref name="materialFilter"/> restricts it to parts whose <c>.mtr</c> is named that.</summary>
    private static void MaterialParam(DbId obj, string? paramName, string? value, string? materialFilter)
        => MaterialParams.Set(obj, paramName, value, materialFilter, _log);

    internal static string Note()
        => (_txiCalls + _txiMissing == 0 ? ""
            : $"txi: {_txiCalls} call(s), {_txiApplied} sampler change(s), {_unhandled} distinct key(s) IGNORED"
              + (_txiMissing == 0 ? "" : $", {_txiMissing} resource(s) MISSING") + "; ")
         + MaterialParams.Note();

    /// <summary>Off-device: a replace followed by a restore round-trips a part's texture id, the PLT form
    /// carries its rows, and the <c>.txi</c> parse keeps what it can and names what it cannot.</summary>
    internal static unsafe bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexSeam.SelfCheck: {what}");
        }

        Expect(Parse("clamp 1\nfilter 0\nmipmap 1", out bool? c, out bool? n, out bool? m, out _)
            && c == true && n == true && m == true, "\"clamp 1 / filter 0 / mipmap 1\" did not parse");
        Expect(Parse("clamp 0", out c, out _, out _, out _) && c == false, "\"clamp 0\" is not false");
        Expect(!Parse("proceduretype cycle", out _, out _, out _, out string? un) && un == "proceduretype",
            "an unmodelled .txi key was not reported");
        Expect(Parse("", out c, out n, out m, out _) && c == null && n == null && m == null,
            "an empty fragment claimed a key");

        // A real object with one part, so the round-trip runs through the production columns.
        DbId obj = SceneDb.Objects.Alloc();
        Expect(!obj.IsNull, "the object table would not allocate");
        if (obj.IsNull) return ok;
        DbId part = SceneDb.Parts.Alloc();
        Expect(!part.IsNull, "the part table would not allocate");
        if (part.IsNull) { SceneDb.Objects.Free(obj); return ok; }
        int p = part.Index;
        int svFirst = SceneDb.ObjPartFirst[obj.Index], svCount = SceneDb.ObjPartCount[obj.Index];
        SceneDb.ObjPartFirst[obj.Index] = p;
        SceneDb.ObjPartCount[obj.Index] = 1;
        SceneDb.PartObj[p] = obj;
        for (int t = 0; t < 4; t++) SceneDb.PartTexId[p * 4 + t] = 0;
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();

        Expect(SceneDb.PartTexId[p * 4] == 0, "the part did not start with no override");
        Expect(ObjectTex.Replace(obj, null, "cx_selfcheck_swap") == 1, "a whole-object replace did not report a hit");
        int after = SceneDb.PartTexId[p * 4];
        Expect(after != 0, "the replace left no override on the part");
        Expect(string.Equals(TexRegistry.ResrefOf(new TexId((uint)after)), "cx_selfcheck_swap",
                             StringComparison.Ordinal),
            "the override does not name the texture that was asked for");
        Expect((SceneDb.Objects.DirtyAt(obj.Index) & Dirty.Texture) != 0, "the replace did not mark the object");

        // ...and a replace that names the WRONG current texture must not take.
        Expect(ObjectTex.Replace(obj, "cx_selfcheck_other", "cx_selfcheck_third") == 0,
            "a replace matched a part whose texture is named something else");
        Expect(SceneDb.PartTexId[p * 4] == after, "a refused replace changed the override anyway");
        // ...nor one that names the override: the engine matches the model's own name, never the swap's.
        Expect(ObjectTex.Replace(obj, "cx_selfcheck_swap", "cx_selfcheck_third") == 0,
            "a replace matched the override's name rather than the model's");

        Expect(ObjectTex.Restore(obj) == 1, "the restore did not report a hit");
        Expect(SceneDb.PartTexId[p * 4] == 0,
            "RESTORE did not round-trip the part's texture id back to the model's own");
        Expect(ObjectTex.Restore(obj) == 0, "a second restore found something to undo");

        // ...a replace carrying rows pins them on the part it swapped, and they win over the object's.
        Span<int> partRows = stackalloc int[SceneDb.PltLayers];
        for (int l = 0; l < SceneDb.PltLayers; l++) partRows[l] = (l << 8) | 5;
        Expect(ObjectTex.Replace(obj, null, "cx_selfcheck_plt", rows: partRows) == 1, "a replace with rows missed");
        Expect(SceneDb.PartPltOwn[p] && SceneDb.PartPltRow[p * SceneDb.PltLayers + 2] == (2 << 8 | 5),
            "the replace's rows did not land on the part it swapped");
        Expect(ObjectTex.Restore(obj) == 1 && !SceneDb.PartPltOwn[p], "a restore left the part's own rows in place");

        // ...and the death edge: a recycled object index must not inherit the dead tenant's PLT colours.
        Span<int> rows = stackalloc int[SceneDb.PltLayers];
        for (int l = 0; l < SceneDb.PltLayers; l++) rows[l] = l * 7 + 3;
        PltColors.SetRows(obj, rows);
        MaterialParams.Set(obj, "CxRoughness", "0.25", null, null);
        for (int l = 0; l < SceneDb.PltLayers; l++)
            Expect(PltColors.Row(obj, l) == l * 7 + 3, $"the PLT scheme's row {l} did not land");
        ObjectDied(obj);
        for (int l = 0; l < SceneDb.PltLayers; l++)
            Expect(PltColors.Row(obj, l) == 0,
                $"row {l} survived the object's death -- the next tenant of this index inherits its colours");
        var leftover = new ParamRow();
        MaterialParams.Apply(obj, ref leftover);
        Expect(leftover.Ext.Ext0.Y == 0f, "a material-param override survived the object's death");

        SceneDb.ObjPartFirst[obj.Index] = svFirst;
        SceneDb.ObjPartCount[obj.Index] = svCount;
        SceneDb.Parts.Free(part);
        SceneDb.Objects.Free(obj);
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        ok &= MaterialParams.SelfCheck(log);
        ok &= EnvMapCheck(log);
        log.Info(ok ? "TexSeam.SelfCheck: passed" : "TexSeam.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>Off-device: an env map named on the object does NOT take the material's slot-3 roughness map
    /// with it, and the env bit still reaches the stage. The oracle is the two hand-written resrefs below --
    /// the roughness one from the <c>MeshDef</c> this check builds, the env map's from the call -- read back
    /// through the selection the draw resolves and the stage's own env-bit test.</summary>
    private static bool EnvMapCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexSeam.SelfCheck: {what}");
        }

        var model = new ModelDef { Name = "cx_sc_env", ContentHash = 0xE0E0E0E0UL };
        model.Nodes = new[]
        {
            new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy },
            new NodeDef
            {
                Name = "body", Index = 1, Parent = 0, Kind = NodeKind.TriMesh,
                Mesh = new MeshDef
                {
                    Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
                    Indices = new uint[] { 0, 1, 2 },
                    BMin = -Vector3.One, BMax = Vector3.One,
                    Textures = new[] { "cx_sc_skin", "", "", "cx_sc_rough" },
                },
            },
        };
        model.NodeByName["root"] = 0;
        model.NodeByName["body"] = 1;

        DbId obj = SceneDb.Objects.Alloc();
        Expect(!obj.IsNull, "the object table would not allocate");
        if (obj.IsNull) return ok;
        DbId part = SceneDb.Parts.Alloc();
        Expect(!part.IsNull, "the part table would not allocate");
        if (part.IsNull) { SceneDb.Objects.Free(obj); return ok; }
        int p = part.Index;
        int svFirst = SceneDb.ObjPartFirst[obj.Index], svCount = SceneDb.ObjPartCount[obj.Index];
        SceneDb.ObjPartFirst[obj.Index] = p;
        SceneDb.ObjPartCount[obj.Index] = 1;
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = model;
        SceneDb.PartNode[p] = 1;
        SceneDb.PartMtr[p] = null;
        SceneDb.PartMeshDef[p] = model.Nodes[1].Mesh;
        for (int t = 0; t < 4; t++) SceneDb.PartTexId[p * 4 + t] = 0;
        int recipe = DrawRecipe.Intern(model, 1);
        Expect(recipe >= 0, "the recipe table would not intern the check's mesh");
        DrawRecipe.Assign(p, recipe);
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();

        TexId rough = SceneDb.PartTex(p, 3);
        Expect(string.Equals(TexRegistry.ResrefOf(rough), "cx_sc_rough", StringComparison.OrdinalIgnoreCase),
            "slot 3 does not start on the material's own roughness map");
        Expect(!SceneDb.PartEnvMapped(p), "the part is env-mapped before anything named an env map");

        Expect(ObjectTex.SetEnvMap(obj, "cx_sc_chrome") == 1, "SetEnvironmentMap did not answer the engine's 1");
        Expect(string.Equals(SceneDb.ObjEnvMap[obj.Index], "cx_sc_chrome", StringComparison.Ordinal),
            "the env map is not kept in the object's own column");
        Expect(SceneDb.PartTex(p, 3).Value == rough.Value,
            "the env map REPLACED the material's roughness map in slot 3");
        Expect(string.Equals(TexRegistry.ResrefOf(SceneDb.PartTex(p, 3)), "cx_sc_rough",
                             StringComparison.OrdinalIgnoreCase),
            "the texture slot 3 resolves to is no longer the roughness map");
        Expect(SceneDb.PartTex(p, 3).Value == rough.Value, "the sampler path and the stage disagree about slot 3");
        Expect(AlloyRenderer.DbEnvMapped(p, SceneDb.PartTex(p, 0)),
            "the env map named on the object does not reach the stage's env bit");
        Expect((DrawRecipe.Slots(recipe) & AlloyRenderer.EnvSlot) == 0,
            "the recipe declared the env bit itself, so the instance half proves nothing");
        Expect((SceneDb.Objects.DirtyAt(obj.Index) & Dirty.Texture) != 0,
            "the env map did not mark the object, so no record would re-derive the bit");

        Expect(ObjectTex.SetEnvMap(obj, "NULL") == 1, "clearing the env map did not answer the engine's 1");
        Expect(SceneDb.ObjEnvMap[obj.Index] == null && !SceneDb.PartEnvMapped(p),
            "\"NULL\" did not clear the env map");
        Expect(SceneDb.PartTex(p, 3).Value == rough.Value, "clearing the env map disturbed slot 3");

        DrawRecipe.Assign(p, -1);
        SceneDb.ObjPartFirst[obj.Index] = svFirst;
        SceneDb.ObjPartCount[obj.Index] = svCount;
        SceneDb.Parts.Free(part);
        SceneDb.Objects.Free(obj);
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        return ok;
    }
}

/// <summary>A <c>Gob::SetMaterialParam</c> override, which cannot live on the recipe:
/// <see cref="DrawRecipe"/> is interned per <c>(ModelDef, node)</c> and shared by every instance, so a
/// parameter written there would repaint every creature wearing the model. The overrides are keyed on the
/// OBJECT as a sparse list, and the stage consults it only for an object that has any.</summary>
internal static class MaterialParams
{
    /// <summary>One override: which parameter of the record, and the value. The parameter is resolved from the
    /// engine's name once, here -- there is no second vocabulary.</summary>
    private readonly record struct Over(ParamName Param, float Value);

    private static readonly Dictionary<uint, List<Over>> _byObj = new();
    private static long _sets, _resets, _unknown, _applied;
    private static readonly HashSet<string> _unknownOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Does any object carry an override? The stage's gate, so a scene that never calls this pays
    /// one bool.</summary>
    internal static bool Any => _byObj.Count > 0;

    /// <summary>Set (or, with an empty <paramref name="value"/>, reset) one material parameter on
    /// <paramref name="obj"/>. Game thread, cold.</summary>
    internal static void Set(DbId obj, string? paramName, string? value, string? materialFilter, ExtenderLog? log)
    {
        if (!SceneDb.Objects.Valid(obj) || string.IsNullOrEmpty(paramName)) return;
        ParamName lane = ParamOf(paramName!);
        if (lane == ParamName.None)
        {
            _unknown++;
            if (_unknownOnce.Add(paramName!))
                log?.Always($"MaterialParams: SetMaterialParam names \"{paramName}\", which is not a parameter "
                          + "Alloy's record carries (the stock four and ADR 0026's Cx vocabulary) -- IGNORED");
            return;
        }
        // The engine's third argument filters on the SharedMaterial's name to 16 characters. CX keeps the
        // .mtr per part, so the filter picks the parts rather than the materials -- and an override that
        // matches no part is still recorded, because the engine recorded it on the material either way and a
        // later part of the same object may arrive with that .mtr.
        bool reset = string.IsNullOrEmpty(value);
        if (reset)
        {
            _resets++;
            if (_byObj.TryGetValue(obj.Value, out List<Over>? had))
            {
                had.RemoveAll(o => o.Param == lane);
                if (had.Count == 0) _byObj.Remove(obj.Value);
            }
        }
        else
        {
            if (!float.TryParse(FirstWord(value!), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                _unknown++;
                if (_unknownOnce.Add(value!))
                    log?.Always($"MaterialParams: SetMaterialParam(\"{paramName}\") was given \"{value}\", which is "
                              + "not a number -- IGNORED (the engine's own path sscanf's one token out of it)");
                return;
            }
            _sets++;
            if (!_byObj.TryGetValue(obj.Value, out List<Over>? list)) _byObj[obj.Value] = list = new List<Over>(4);
            list.RemoveAll(o => o.Param == lane);
            list.Add(new Over(lane, v));
        }
        if (!string.IsNullOrEmpty(materialFilter)) _filtered++;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + count; p++) SceneDb.TouchPart(p, Dirty.Material);
        SceneDb.Touch(obj, Dirty.Material);
    }
    private static long _filtered;

    /// <summary>Drop every override on an object -- what <c>RestoreTexture</c> and a record's death do.</summary>
    internal static int Forget(DbId obj)
    {
        if (!_byObj.Remove(obj.Value)) return 0;
        if (!SceneDb.Objects.Valid(obj)) return 1;
        int first = SceneDb.ObjPartFirst[obj.Index], count = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + count; p++) SceneDb.TouchPart(p, Dirty.Material);
        SceneDb.Touch(obj, Dirty.Material);
        return 1;
    }

    /// <summary>Lay this object's overrides over the parameter row the recipe just wrote into the
    /// record. Called from the DB stage, and only for an object that has any.</summary>
    internal static void Apply(DbId obj, ref ParamRow param)
    {
        if (_byObj.Count == 0 || !_byObj.TryGetValue(obj.Value, out List<Over>? list)) return;
        for (int i = 0; i < list.Count; i++) { param.Set(list[i].Param, list[i].Value); _applied++; }
    }

    /// <summary>The engine's parameter name -> the record's parameter. A multi-component parameter names its
    /// first component, and the engine's own setter writes one scalar too.</summary>
    internal static ParamName ParamOf(string name)
        => name.ToLowerInvariant() switch
        {
            "specularity" => ParamName.Specularity,
            "roughness" => ParamName.Roughness,
            "metallicness" => ParamName.Metallicness,
            "customspecularcolor" => ParamName.CustomSpecularColor,
            "cxmetallic" => ParamName.CxMetallic,
            "cxroughness" => ParamName.CxRoughness,
            "cxclearcoat" => ParamName.CxClearcoat,
            "cxclearcoatroughness" => ParamName.CxClearcoatRoughness,
            "cxsheencolor" => ParamName.CxSheenColor,
            "cxsheenroughness" => ParamName.CxSheenRoughness,
            "cxemissivestrength" => ParamName.CxEmissiveStrength,
            "cxorm" => ParamName.CxOrm,
            _ => ParamName.None,
        };

    /// <summary>The engine reads one token out of the value (<c>sscanf(value, "%s %*s", ...)</c> on a
    /// single-component parameter), so "0.4 junk" is 0.4 there and here.</summary>
    private static string FirstWord(string v)
    {
        int i = v.IndexOfAny(new[] { ' ', '\t' });
        return i < 0 ? v : v[..i];
    }

    internal static string Note()
        => _sets + _resets + _unknown == 0 ? ""
           : $"matparam: {_sets} set, {_resets} reset, {_applied} applied, {_byObj.Count} object(s) hold one"
             + (_filtered == 0 ? "" : $", {_filtered} carried a material filter")
             + (_unknown == 0 ? "" : $", {_unknown} IGNORED");

    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"MaterialParams.SelfCheck: {what}");
        }
        Expect(ParamOf("Roughness") == ParamName.Roughness && ParamOf("roughness") == ParamName.Roughness, "Roughness is not the roughness parameter, case-insensitively");
        Expect(ParamOf("CxRoughness") == ParamName.CxRoughness, "CxRoughness is not the extension roughness");
        Expect(ParamOf("NotAParam") == ParamName.None, "an unknown parameter name resolved to a parameter");

        (long sv_s, long sv_r, long sv_u, long sv_a, long sv_f) = (_sets, _resets, _unknown, _applied, _filtered);
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { Expect(false, "the object table would not allocate"); return ok; }
        SceneDb.ObjPartFirst[obj.Index] = 0; SceneDb.ObjPartCount[obj.Index] = 0;
        SceneDb.Objects.TakeDirty();
        var p = new ParamRow();

        Set(obj, "CxRoughness", "0.25", null, null);
        Expect((SceneDb.Objects.DirtyAt(obj.Index) & Dirty.Material) != 0, "a set did not mark the object");
        Apply(obj, ref p);
        Expect(Math.Abs(p.Ext.Ext0.Y - 0.25f) < 1e-6f, "the override did not reach its lane");
        Expect(p.Specularity == 0f, "the override touched another lane");

        Set(obj, "CxRoughness", "0.5 trailing junk", null, null);
        p = default;
        Apply(obj, ref p);
        Expect(Math.Abs(p.Ext.Ext0.Y - 0.5f) < 1e-6f,
            "a second set did not replace the first, or the value was not read as one token");

        Set(obj, "CxRoughness", "", null, null);     // the engine's reset-to-default form
        p = default;
        Apply(obj, ref p);
        Expect(p.Ext.Ext0.Y == 0f, "an empty value did not RESET the override");
        Expect(!Any, "the table kept an entry for an object with no overrides left");

        Set(obj, "Specularity", "0.9", null, null);
        Expect(Forget(obj) == 1 && !Any, "Forget did not drop the object's overrides");
        Set(obj, "NotAParam", "1", null, null);
        Expect(!Any, "an unknown parameter was recorded anyway");
        Set(obj, "Roughness", "not-a-number", null, null);
        Expect(!Any, "an unparseable value was recorded anyway");

        SceneDb.Objects.Free(obj);
        SceneDb.Objects.TakeDirty();
        (_sets, _resets, _unknown, _applied, _filtered) = (sv_s, sv_r, sv_u, sv_a, sv_f);
        _byObj.Clear();
        return ok;
    }
}
