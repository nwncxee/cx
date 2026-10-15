using CX.Engine;
using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b><c>StagePart</c> for a database slot:</b> resolve this part's textures and draw arguments,
/// write its persistent record, its two spheres and its batch row, with every term a column or an interned
/// recipe read instead of a pointer chase. No <c>PartRef</c>, no <c>MaterialRef</c>, no <c>VdoRef</c>, no
/// texture poll -- a texture is a <see cref="TexId"/> and geometry an arena range of three draw integers.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int RecWhyModel = 0, RecWhyPrev = 1, RecWhyDissolve = 2, RecWhyAlpha = 3, RecWhyAmb = 4, RecWhyDif = 5,
                      RecWhyEmi = 6, RecWhySlots = 7, RecWhyFeat = 8, RecWhyStale = 9, RecWhyReveal = 10, RecWhyCount = 11;
    private static readonly string[] RecWhyName = { "model", "prev", "dissolve", "alpha", "amb", "dif", "emi", "slots", "feat", "stale", "reveal" };
    /// <summary>Per-period counts of the record fields that differed when a DB record was rewritten (one record
    /// can count under several), and one sample of a non-moving rewrite. Diagnostic for the per-frame churn.</summary>
    private static readonly long[] _recWhy = new long[RecWhyCount];
    private static int _recWhySampled;
    /// <summary>DB cast-admission flips this period, on and off.</summary>
    private static long _dbCastOn, _dbCastOff;
    private static string _recWhySample = "";

    /// <summary>Which of <see cref="WritePerFrame"/>'s inputs differ from what record <paramref name="r"/> holds.</summary>
    private static int RecWhy(float* r, in RecordFrameState f, bool modelMoved)
    {
        int m = modelMoved ? 1 << RecWhyModel : 0;
        if (!modelMoved && *(Matrix4x4*)(&r[PrevFloat]) != *(Matrix4x4*)r) m |= 1 << RecWhyPrev;
        if (r[18] != f.Dissolve) m |= 1 << RecWhyDissolve;
        if (r[19] != f.Alpha) m |= 1 << RecWhyAlpha;
        if (f.Lit)
        {
            if (r[20] != f.AmbR || r[21] != f.AmbG || r[22] != f.AmbB) m |= 1 << RecWhyAmb;
            if (r[24] != f.DifR || r[25] != f.DifG || r[26] != f.DifB) m |= 1 << RecWhyDif;
            if (r[28] != f.EmiR || r[29] != f.EmiG || r[30] != f.EmiB) m |= 1 << RecWhyEmi;
            if (r[35] != f.Slots) m |= 1 << RecWhySlots;
        }
        if (r[40] != f.FeatX || r[41] != f.FeatY) m |= 1 << RecWhyFeat;
        return m;
    }

    private static void NoteRecWhy(int why, int part, float* r, in RecordFrameState f)
    {
        for (int b = 0; b < RecWhyCount; b++) if ((why & (1 << b)) != 0) System.Threading.Interlocked.Increment(ref _recWhy[b]);
        if ((why & ((1 << RecWhyModel) | (1 << RecWhyStale))) != 0 || System.Threading.Interlocked.Exchange(ref _recWhySampled, 1) != 0) return;
        DbId o = SceneDb.PartObj[part];
        _recWhySample = $" sample part={part} obj={o.Index} flags={SceneDb.ObjFlags[o.Index]} why=0x{why:x}"
                      + $" alpha {r[19]:G6}->{f.Alpha:G6} amb ({r[20]:G6},{r[21]:G6},{r[22]:G6})->({f.AmbR:G6},{f.AmbG:G6},{f.AmbB:G6})"
                      + $" dif ({r[24]:G6})->({f.DifR:G6}) emi ({r[28]:G6})->({f.EmiR:G6}) slots {r[35]}->{f.Slots}";
    }

    /// <summary>The period's rewrite reasons and sample, then reset.</summary>
    internal static string RecWhyStats(int frames)
    {
        float fr = MathF.Max(frames, 1);
        var sb = new System.Text.StringBuilder(" [why/frame:");
        for (int b = 0; b < RecWhyCount; b++)
        {
            long n = System.Threading.Interlocked.Exchange(ref _recWhy[b], 0);
            if (n != 0) sb.Append(' ').Append(RecWhyName[b]).Append('=').Append((n / fr).ToString("F1"));
        }
        sb.Append("] cast admission flips on ").Append(System.Threading.Interlocked.Exchange(ref _dbCastOn, 0))
          .Append(" off ").Append(System.Threading.Interlocked.Exchange(ref _dbCastOff, 0)).Append(_recWhySample);
        _recWhySample = ""; _recWhySampled = 0;
        return sb.ToString();
    }

    /// <summary>One database part staged. Verdicts are <c>StagePart</c>'s: <c>Staged</c>, <c>Skipped</c> (this
    /// pass draws nothing for it) or <c>Deferred</c> (a worker could not take it; the main thread re-runs it).</summary>
    private static byte StageDbPart(int k, int i, float* data, bool lightingOn, ref StageCounters c, bool main)
    {
        int part = _oDbPart[i], recipe = _oDbRecipe[i];
        if (StaleAuditArmed(i)) _saEnter[i] = _frameSeq;
        if (part < 0 || recipe < 0) return Skipped;
        int cls = _passCls[k];
        if (cls == (int)AlloyClass.Water && !_waterArmed) return Skipped;
        bool lit = _oLit[i];
        if (lit && !lightingOn) { Inc(ref Counters._skipNoLight); return Skipped; }
        // The PLT stage is share-group work and a worker may not do it (ADR 0022). BINDLESS is not: this
        // refusal was inherited from the engine path, whose reason (#220, minting a handle on a raw engine
        // texture name) has no counterpart here -- CX-117's registry resolves handles in Alloy's own bracket
        // and TexRegistry.Handle is a plain array read. Refusing on `_bindless` therefore deferred EVERY
        // drawn DB part to the main thread and staged the frame twice: the forest measured 379 deferrals a
        // frame against 379 draws. What is still main-only is the per-slot work below that mutates shared
        // state -- a dependent registration for a texture that has not landed, and the tile diagnostic.
        // The override counts as much as the recipe: armour reaches its PLT through ReplaceTexture, and a slot
        // that WAS a PLT must be cleared on the main thread too -- PltColors' dirty range is unsynchronised, and
        // a lost update leaves the GPU drawing that record with a dead tenant's rows or no PLT flag at all.
        if (!main && (DbPartHasPlt(recipe) || TexRegistry.KindOf(DbTexOf(part, recipe, 0)) == TexKind.Plt
                      || PltColors.Flagged(i))) return Deferred;
        StageCounters snap = c;

        // ---- geometry: three integers off the arena, and the VAO its format shares ----
        // CX-50: the BASE run, always. The level this frame draws is the batch row's -- picked off the cull
        // sphere, which only this slot's own record block below makes current, and applied by the batch walk
        // (ArenaLevelRuns) on the main thread. A shadow view picks its own level per caster in the gather.
        if (!MeshArena.Draw(DrawRecipe.Range(recipe), 0, out uint vao, out int baseVertex,
                            out nint off, out int idxCount, out int level) || idxCount <= 0)
        { _dbNoRange++; Inc(ref Counters._skipRange); return Skipped; }
        _cOff[i] = off; _cBase[i] = baseVertex; _cIdx[i] = idxCount;
        // CX-120: the first few DB draws, once. "296 draws kept and 0 samples shaded" can only be answered by
        // the actual arguments and the actual placement, and a counter cannot carry them.
        if (_dbDumped < 6)
        {
            _dbDumped++;
            Matrix4x4 w0 = SceneDb.PartWorld[part];
            MeshArena.Bounds(DrawRecipe.Range(recipe), out Vector3 bmin, out Vector3 bmax, out _, out float brad);
            _log?.Info($"AlloyRenderer: [db] draw {_dbDumped} part={part} recipe={recipe} "
                       + $"range={DrawRecipe.Range(recipe)} vao={vao} count={idxCount} off={(long)off} base={baseVertex} "
                       + $"lvl={level} cls={_oCls[i]} lit={_oLit[i]} "
                       + $"pos=({w0.M41:F2},{w0.M42:F2},{w0.M43:F2}) "
                       + $"m11={w0.M11:F3} m22={w0.M22:F3} m33={w0.M33:F3} "
                       + $"meshbox=({bmin.X:F2},{bmin.Y:F2},{bmin.Z:F2})..({bmax.X:F2},{bmax.Y:F2},{bmax.Z:F2}) r={brad:F2} "
                       + $"eye=({_eyePos.X:F2},{_eyePos.Y:F2},{_eyePos.Z:F2})");
        }
        _oDbVao[i] = vao; _oDbLevel[i] = (byte)level;

        // ---- textures: a handle per slot, no re-fetch and no poll ----
        int slots = _oSlots[i];
        TexId diffuse = DbTexOf(part, recipe, 0);
        // CX-188: and on a frame no texture moved, not even that. Every answer the block below derives comes
        // from two places -- the registry's per-slot arrays (name, handle, kind, env bit, validity), which
        // only change under TexRegistry.TableSerial, and this part's own override column, which only changes
        // under a Dirty.Texture fold (DbTexDirty). A slot stamped with the current serial therefore holds
        // exactly what the block would recompute, so it is served from _recTid/_dTexH instead. Measured
        // before: 8 DbTexOf + 8 Handle + 8 TryResident per part per frame, 12,7k parts, nothing changing.
        int tid = _recTid[i];
        if (_recTexStamp[i] != _stageTexSerial)
        {
            bool env = DbEnvMapped(part, diffuse);
            if (env != ((slots & EnvSlot) != 0))
            {
                // #328: the bit rides _oSlots, which the caster pass reads as EnvAlpha -- so a record whose
                // answer changed is unstamped and the casters re-baked, exactly as the engine path does. A
                // recycled slot is why the clearing half exists: the env map is per instance.
                slots = env ? slots | EnvSlot : slots & ~EnvSlot;
                _oSlots[i] = slots;
                UnstampRecord(i);
                CasterGen++;
                if (env) _dbEnv++;
            }
            // main only: the route is shared, and a slot that is or was a PLT never stages on a worker (refused above)
            byte slotCls = main ? DbClsOf(part, recipe, _oWash[i] != 0) : _oCls[i];
            if (slotCls != _oCls[i]) { _oCls[i] = slotCls; UnstampRecord(i); DbSetGen++; }
            // The GL NAME, not the registry id. `tid` is two things at once in this pipeline: the batch sort key
            // AND what the non-bindless path actually binds (Issue: glBindTexture(GL_TEXTURE_2D, _gTex[g])).
            // Handing it a TexId made every bind-path draw -- every unlit part, and every part at all when
            // bindless is off -- bind a nonsense name and sample the white stand-in, which is what painted the
            // floors and walls of the first lit DB area a flat lavender while the one bindless-lit chest in the
            // room came out perfect.
            TexRegistry.TryDrawable(diffuse, out uint diffuseName, out _);
            tid = (int)diffuseName;
            // CX-188: is this resolve FINAL -- can its answer change without a TableSerial bump? Only a
            // final resolve is memoised; one still waiting on an arrival is re-asked next frame, which keeps
            // the arrival window live and leaves the skip to the steady state.
            // A resolve that is final the OTHER way counts too, or it re-resolves for ever: a part with no
            // diffuse at all (`bitmap null`) and one whose load FAILED both draw the white texel, and neither
            // can improve -- Fail bumps the serial once and the slot then answers the same for the session.
            // That is a real population, not a corner (junk material names on placeables: "2", NullTex,
            // Standardmaterial -- docs/missing-parts-triage.md), and leaving it unsettled put every one of
            // them back on the per-frame re-resolve the skip exists to end.
            bool settled = (diffuseName != 0 && TexRegistry.StateOf(diffuse) != TexState.Loading) || TexRegistry.Final(diffuse);
            // No diffuse (`bitmap null`) or one still in flight draws on the white texel, as the engine does
            // (OwnedTextures.WhiteTid): handle 0 and GL name 0 sample transparent/opaque BLACK, which hid a door's sam.
            if (tid == 0)
            {
                if (!main && OwnedTextures.WhiteName == 0) { c = snap; return Deferred; }
                tid = main ? OwnedTextures.WhiteTid() : OwnedTextures.WhiteName;
            }
            if (_bindless)
                fixed (ulong* th = _dTexH)
                {
                    ulong* hh = ((Stride8u*)th)[i].F;
                    bool moved = false;
                    for (int s = 0; s < 8; s++)
                    {
                        TexId id = DbTexOf(part, recipe, s);
                        ulong h = TexRegistry.Handle(id);
                        // 0 is the white stand-in: the record draws it and is told when the real one lands, which
                        // is what replaces the engine path's per-frame re-resolve of every slot of every part.
                        // ...and a slot still LOADING with a handle is drawing its preview: the full chain is on its way.
                        if (!id.IsNull && (h == 0 || TexRegistry.StateOf(id) == TexState.Loading))
                        {
                            // Registering a dependent mutates the registry's own list; the main thread re-runs
                            // this part. Steady state is every texture resident, so this is the arrival window
                            // only -- not every frame.
                            if (!main) { c = snap; return Deferred; }
                            TexRegistry.AddDependent(id, part);
                            // ...and a map slot that has FAILED is final too: AddDependent no-ops on a
                            // non-loading slot, so nothing would ever come to repair the memo.
                            if (!TexRegistry.Final(id)) settled = false;
                        }
                        if (s == 0 && h == 0)
                        {
                            h = Handle(tid, main);
                            if (h == 0 && !main) { c = snap; return Deferred; }
                            if (h == 0) settled = false;
                        }
                        if (hh[s] != h) { hh[s] = h; moved = true; }
                    }
                    // CX-188: ...and only a handle that actually MOVED dirties the table. The unconditional flag
                    // made every frame re-upload the whole handle block -- and the caster bake reads that flag to
                    // decide whether it has to re-upload before baking (BakeHandleUploads).
                    // ...and the SLOT goes on the dirty list: a partial UploadHandles sends only _recDirty's runs, and a
                    // static part whose record did not move is on no other list -- its GPU row kept the preview handle
                    // Land had just made non-resident, black until it left view or a whole upload swept it up.
                    if (moved) { _texHDirty = true; MarkRecordDirty(i, ref c); }
                    if (main)   // diagnostics: shared statics, main only
                    {
                        TileTexProbe(part, recipe, hh[0]);
                        DbBlackProbe(part, recipe, hh[0]);
                    }
                }
            // CX-188: the record's own texture memos, written beside the resolve that produced them (they used to
            // sit at the tail of the function, which is the same point in the frame -- nothing between reads them).
            // The batch audit compares row.Tid against _recTid[i], and leaving it unwritten made every DB draw
            // report a "diffuse tid" mismatch -- 3333 of them, a false alarm that looked exactly like the real
            // texture fault it was sitting next to.
            _recTid[i] = tid;
            _recSlotMask[i] = slots;
            for (int s = 0; s < MapSlots; s++)
            {
                TexRegistry.TryDrawable(DbTexOf(part, recipe, s + 1), out uint mapName, out _);
                _recTidS[i * MapSlots + s] = (int)mapName;
            }
            // CX-188: the staleness is REPAIRED here and nowhere earlier -- this is the last of the three things
            // a retire invalidates (the bindless handle block above, the bind-path tid, and these map-slot tids),
            // and no path between the resolve and this point returns. The clear lived in the deleted engine-path
            // StagePart, so in DB mode nothing ever cleared it: a slot a retire marked failed RouteWanted's gate
            // open for the rest of the session, and with a tileset texture on hundreds of parts that was ~13,6k
            // slots a frame bypassing the frustum gate entirely. (With bindless OFF there is no handle block to
            // repair and nothing samples one; `alloy.bindless` is Effect.Restart, so it cannot flip under us.)
            _gate[i].TexStale = false;
            // The memo's own invalidation edges, both named on _recTexStamp: a registry write (TableSerial) and
            // this part's override column (DbTexDirty, from the Dirty.Texture fold). A resolve that did not
            // settle stamps 0, i.e. "ask again".
            _recTexStamp[i] = settled ? _stageTexSerial : 0;
        }
        // The PLT resolve is per INSTANCE: the atlas is one RG8 upload shared by every creature wearing it,
        // and the ten palette rows that colour it live on the part (a per-part scheme) or its object's record. Staged per draw slot here;
        // cleared when this slot's diffuse is not a PLT, so a slot that stops being one stops resolving.
        if (TexRegistry.KindOf(diffuse) == TexKind.Plt) PltColors.Stage(i, SceneDb.PartObj[part], part);
        // CX-223: ...and for a palette-tint material the rows go up off an ORDINARY diffuse too -- its hosted
        // stage reads the wearer's scheme (the engine's PLTscheme feed) while its diffuse carries palette
        // intensity rather than a composite. The flag says "rows, not a resolve", so nothing starts resolving.
        else if (ShaderFeatures.ReadsPaletteSlots((int)_oFeat[i * 4]))
            PltColors.Stage(i, SceneDb.PartObj[part], part, rowsOnly: true);
        else PltColors.Clear(i);
        if (StaleAuditArmed(i)) _saPltAt[i] = _frameSeq;

        // ---- the record ----
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj)) { _dbOrphan++; return Skipped; }
        int o = obj.Index;
        bool dis = cls <= (int)AlloyClass.Cutout ? _passDis[k] : Dissolves(i, _oTri[i], ref c);
        if (dis) c.OwnDissolve++;

        var f = default(RecordFrameState);
        f.HasModel = true;
        f.Model = SceneDb.PartWorld[part];
        f.Dissolve = dis ? 1f : 0f;
        Vector4 tint = SceneDb.ObjTint[o];
        f.Alpha = SceneDb.PartAlpha[part] * SceneDb.PartFade[part] * tint.W;
        f.Lit = lit;
        if (lit)
        {
            Vector4 amb = DrawRecipe.Ambient(recipe), dif = DrawRecipe.Diffuse(recipe);
            f.AmbR = amb.X * tint.X; f.AmbG = amb.Y * tint.Y; f.AmbB = amb.Z * tint.Z;
            f.DifR = dif.X * tint.X; f.DifG = dif.Y * tint.Y; f.DifB = dif.Z * tint.Z;
            Vector3 emi = SceneDb.PartSelfIllum[part] + SceneDb.ObjEmissive[o];
            f.EmiR = emi.X; f.EmiG = emi.Y; f.EmiB = emi.Z;
            f.Slots = cls == (int)AlloyClass.Blended && DbUntextured(i) ? slots | NoShadowBit : slots;
        }
        int feat = (int)_oFeat[i * 4];
        f.FeatX = ShaderFeatures.WithPose(feat, _oBone[i] >= 0);
        f.FeatY = _oBone[i] >= 0 ? _oBone[i] * (float)BoneCount : _oFeat[i * 4 + 1];

        // CX-120 (integration 4): the tile's fog-of-war REVEAL, in the record's one spare lane. Vanilla fogs
        // an unexplored interior tile toward black (RenderDrawBucket @0x46d060: fog colour x f^2, fog end x f)
        // rather than fading its alpha, which is why this is a fog term and PartFade stays plain alpha.
        float* r = ((DrawRec*)data)[i].F;
        // HIDDEN, not reveal -- see RevealFloat: an unwritten lane is 0 and 0 must mean explored.
        float hidden = 1f - SceneDb.PartReveal[part];
        bool revealMoved = r[RevealFloat] != hidden;
        r[RevealFloat] = hidden;
        bool stale = _gate[i].RecGen != _recEpoch || _gate[i].RecEnroll != _gate[i].EnrollGen;
        if (stale) DbWriteStable(r, i);
        // #302, as the engine path: the mover level wants GEOMETRY motion compared before the write consumes it,
        // not "the record was re-sent" -- alpha, ambient and reveal rewrites charged as movers re-bake shadows.
        bool casterMoved = Moved(in *(Matrix4x4*)r, in f.Model, CasterStep);
        bool modelMoved = casterMoved || Moved(in *(Matrix4x4*)r, in f.Model, MoveStep);
        int why = RecWhy(r, in f, modelMoved) | (stale ? 1 << RecWhyStale : 0) | (revealMoved ? 1 << RecWhyReveal : 0);
        bool rec = WritePerFrame(r, in f, roll: _gate[i].RecFrame != _frameSeq, fresh: stale);
        rec |= stale | revealMoved;
        if (rec)
        {
            DbWriteWorldBox(i, recipe, in f.Model);
            DbWriteSphere(i, in f);
            NoteCasterTouched(i);
            MarkRecordDirty(i, ref c);
            if (casterMoved || stale) _recMovedAt[i] = _frameSeq;
            System.Threading.Interlocked.Increment(ref modelMoved ? ref _dbRecModel : ref _dbRecOther);
            NoteRecWhy(why, part, r, in f);
        }
        else c.RecStable++;
        _gate[i].RecGen = _recEpoch; _gate[i].RecFrame = _frameSeq; _gate[i].RecEnroll = _gate[i].EnrollGen;
        // R1-19: a DB part CASTS. The admission is the engine path's own rule -- a part that writes depth, or
        // a blended one that is not posed (it casts at its record alpha through the masked stage's dither).
        // R1-33 honoured ObjFlag.NoShadow (AurDisableShadowing) AND the mesh's own `shadow 0`. CX-120
        // withdraws the mesh flag, measured. `shadow 0` is the engine's STENCIL-VOLUME class and the
        // tileset's TERRAIN carries it, so reading it as a verdict about Alloy's depth maps left the GROUND
        // out of the cascades: 227 casters in this area where the engine's own caster set has 3138. A ground with no depth of its
        // own in the map has nothing to win the comparison at its own texels, so every trunk, rock and bank
        // beside it shadowed it instead -- a FALSE 1,5..3,4u gap over whole tiles, which is the stepped slab
        // that lay across the forest path.
        // What settled it was the receiver's own lookup, run on the CPU over a ground grid around the player
        // against a readback of the very texels the shader samples: with the flag read, the cells around the
        // player reported 1,6..2,7u of occlusion where a ground that casts reads -0,03u (its own surface);
        // with it withdrawn the two grids agree cell for cell over 9x9 at 6u, every real cliff gap (3..17u)
        // included.
        // The OBJECT flag stays: AurDisableShadowing is an explicit per-object opt-out, not a statement
        // about which shadow system is running.
        bool mayCast = (SceneDb.ObjFlags[o] & ObjFlag.NoShadow) == 0;
        // CX-120: the TILE FADE, which this path never set -- `!_oFading[i]` below was reading a flag only the
        // engine stage ever writes, so every DB part passed it unconditionally. The tile's ramp is
        // SceneDb.PartFade (the camera-occlusion fade TileBuild.Publish keeps apart from the fog-of-war
        // reveal) and it is the term the engine stage compares: a part fading by its OWN alpha still casts
        // (#328), a part its tile has faded out of the picture must not -- a `tilefade` mesh is the tileset's
        // own camera occluder, and one still casting after the camera dissolved it away is a shadow with
        // nothing above it.
        // Honest about its reach: no tile is mid-ramp anywhere in the forest area this was measured in, so it
        // changes no pixel there. It closes the hole, not a symptom; the roofed interiors the ramp actually
        // runs in are where it would have shown.
        _oFading[i] = SceneDb.PartFade[part] < 1f;
        // ...and never a hidden .lod alternate: a swap restages the WHOLE range, the hidden tree included, and
        // its shadow over the shown one is two silhouettes fighting in the cascades
        bool castOk = mayCast && !_oFading[i] && tint.W > 0f && !SceneDb.LodHidden(SceneDb.PartLodTree[part], SceneDb.ObjLodShown[o]) && (_oCls[i] <= (byte)AlloyClass.Cutout
                                      || (_oCls[i] == (byte)AlloyClass.Blended && _oBone[i] < 0));
        if (castOk != _castOk[i]) System.Threading.Interlocked.Increment(ref castOk ? ref _dbCastOn : ref _dbCastOff);
        SetCastOk(i, castOk, ref c);

        // CX-120: one TILE record and one OBJECT record, in full, once each. Tiles render near-black while
        // objects light normally through the same shader, so the answer is whichever record lane differs --
        // and no counter carries a lane.
        if (_dbRecDump < 2)
        {
            bool isTile = (SceneDb.ObjFlags[o] & ObjFlag.Tile) != 0;
            if (isTile != _dbDumpedTile || _dbRecDump == 0)
            {
                _dbDumpedTile = isTile;
                _dbRecDump++;
                _log?.Info($"AlloyRenderer: [db] {(isTile ? "TILE" : "OBJ ")} part={part} lit={lit} "
                           + $"amb=({r[20]:F3},{r[21]:F3},{r[22]:F3},{r[23]:F3}) "
                           + $"dif=({r[24]:F3},{r[25]:F3},{r[26]:F3},{r[27]:F3}) "
                           + $"emi=({r[28]:F3},{r[29]:F3},{r[30]:F3},w={r[31]:F1}) "
                           + $"slots={slots} feat=({r[40]:F1},{r[41]:F1},{r[42]:F1},{r[43]:F1}) "
                           + $"alpha={f.Alpha:F3} dissolve={f.Dissolve:F3} hidden={r[RevealFloat]:F3} "
                           + $"pbr=({r[32]:F2},{r[33]:F2},{r[34]:F2}) texH0={DrawRecipe.TexHandle(recipe, 0) != 0} "
                           + $"lmrect={LightmapBake.HasRecordForSelfCheck(i)}");
            }
        }
        _pkDis[k] = dis ? (byte)1 : (byte)0;
        WriteBatchRow(k, i, cls, lit, dis, tid, slots, idxCount, feat);
        return Staged;
    }

    /// <summary>How many [db tiletex] lines the probe prints before it is done.</summary>
    private const int TileTexLines = 4;
    private static int _tileTexProbed;
    private static int _tileStages;
    private static float _tileMaxR;
    private static int _tileMaxPart = -1, _tileMaxRecipe = -1;
    private static Vector3 _tileMaxLo, _tileMaxHi, _tileMaxWc;
    private static float _tileMaxD, _tileMaxRad;
    private static bool _saidTileMax;

    /// <summary>Why a TILE record carries the white stand-in while the census says its recipe resolved.
    /// Prints the whole chain once the records have settled: the recipe's diffuse id, the registry's state
    /// for it, whether a handle exists, the per-part override, and how many times this population has been
    /// staged at all -- a record that is staged ONCE and never again cannot pick up a late arrival.</summary>
    private static void TileTexProbe(int part, int recipe, ulong h0)
    {
        // Both one-shots have printed: there is nothing left to measure, and this runs per part per frame
        // for the rest of the session (review #5).
        if (_saidTileMax && _tileTexProbed >= TileTexLines) return;
        DbId po = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(po)) return;
        bool isTile = (SceneDb.ObjFlags[po.Index] & ObjFlag.Tile) != 0;
        if (isTile) _tileStages++;
        // The pale wedge is geometry and it is BIG: track the widest tile surface staged and name it once,
        // with everything needed to find it in the content.
        // Rank by PROJECTED size, not world radius: every tile is ~7u across, so world radius names nothing.
        // What covers a third of the frame is a surface that is large AND close.
        if (!_saidTileMax
            && MeshArena.Bounds(DrawRecipe.Range(recipe), out Vector3 blo, out Vector3 bhi, out Vector3 bc, out float brad))
        {
            Vector3 wc = Vector3.Transform(bc, SceneDb.PartWorld[part]);
            float d = (wc - _eyePos).Length();
            float screen = d > 0.01f ? brad / d : brad;
            if (screen > _tileMaxR)
            {
                _tileMaxR = screen;
                _tileMaxPart = part; _tileMaxRecipe = recipe;
                _tileMaxLo = blo; _tileMaxHi = bhi;
                _tileMaxWc = wc; _tileMaxD = d; _tileMaxRad = brad;
            }
        }
        if (_dbPasses < 600) { _tileMaxR = 0f; _tileMaxPart = -1; }
        if (_dbPasses >= 600 && !_saidTileMax && _tileMaxPart >= 0)
        {
            _saidTileMax = true;
            CX.Alloy.Mdl.MeshDef? bm = DrawRecipe.Mesh(_tileMaxRecipe);
            DbId bo = SceneDb.PartObj[_tileMaxPart];
            _log?.Info($"AlloyRenderer: [db tilemax] the widest DRAWN surface: part={_tileMaxPart} "
                       + $"model='{SceneDb.PartModel[_tileMaxPart]?.Name}' node={SceneDb.PartNode[_tileMaxPart]} "
                       + $"flags={(SceneDb.Objects.Valid(SceneDb.PartObj[_tileMaxPart]) ? SceneDb.ObjFlags[SceneDb.PartObj[_tileMaxPart].Index] : ObjFlag.None)} "
                       + $"kind={SceneDb.PartKindOf[_tileMaxPart]} cls={_oCls[SceneDb.PartSlot[_tileMaxPart] >= 0 ? SceneDb.PartSlot[_tileMaxPart] : 0]} "
                       + $"screenR={_tileMaxR:F3} worldR={_tileMaxRad:F1} dist={_tileMaxD:F1} "
                       + $"centre=({_tileMaxWc.X:F1},{_tileMaxWc.Y:F1},{_tileMaxWc.Z:F1}) lo=({_tileMaxLo.X:F1},{_tileMaxLo.Y:F1},{_tileMaxLo.Z:F1}) "
                       + $"hi=({_tileMaxHi.X:F1},{_tileMaxHi.Y:F1},{_tileMaxHi.Z:F1}) "
                       + $"objWorld.t=({(SceneDb.Objects.Valid(bo) ? SceneDb.ObjWorld[bo.Index].Translation : default)}) "
                       + $"| tex0='{(bm != null ? bm.Textures[0] : "?")}' verts={(bm != null ? bm.Positions.Length : 0)} "
                       + $"handle={(TexRegistry.Handle(DbTexOf(_tileMaxPart, _tileMaxRecipe, 0)) != 0 ? "yes" : "NO")}");
        }
        if (!isTile || _tileTexProbed >= TileTexLines || _dbPasses < 600) return;
        _tileTexProbed++;
        TexId id = DbTexOf(part, recipe, 0);
        CX.Alloy.Mdl.MeshDef? m = DrawRecipe.Mesh(recipe);
        _log?.Info($"AlloyRenderer: [db tiletex] part={part} recipe={recipe} tileStages={_tileStages} "
                   + $"| recipeTex0={DrawRecipe.Tex(recipe, 0).Value} override={SceneDb.PartTexId[part * 4]} "
                   + $"used={id.Value} state={TexRegistry.StateOf(id)} handle={(TexRegistry.Handle(id) != 0 ? "yes" : "NO")} "
                   + $"h0={(h0 != 0 ? "yes" : "NO")} resident={TexRegistry.TryResident(id, out uint nm, out _)} glname={nm} "
                   + $"| mesh tex0='{(m != null ? m.Textures[0] : "?")}' lm={(m != null && m.Lightmapped)} "
                   + $"| cov={TexRegistry.Coverage(id)} cls={(AlloyClass)DrawRecipe.Cls(recipe)} "
                   + $"lit={_oLit[SceneDb.PartSlot[part] >= 0 ? SceneDb.PartSlot[part] : 0]} "
                   + $"alpha={SceneDb.PartAlpha[part]:F2} fade={SceneDb.PartFade[part]:F2} "
                   + $"count={_cIdx[SceneDb.PartSlot[part] >= 0 ? SceneDb.PartSlot[part] : 0]}");
    }

    /// <summary>Does this DB slot's part declare no diffuse at all (<c>bitmap null</c>)?</summary>
    internal static bool DbUntextured(int slot)
    {
        int part = _oDbPart[slot];
        return DbTexOf(part, SceneDb.PartRecipe[part], 0).IsNull;
    }

    /// <summary>The texture this part draws in <paramref name="slot"/> -- <see cref="SceneDb.PartTex"/>, the
    /// one selection the sampler and <c>.txi</c> paths resolve too.</summary>
    private static TexId DbTexOf(int part, int recipe, int slot) => SceneDb.PartTex(part, recipe, slot);

    /// <summary>Is this part environment-mapped? The engine's own routing condition,
    /// <c>Engine.HasEnvMap(material) || GobHasEnvMap(gob)</c>: the INSTANCE was given one
    /// (<see cref="SceneDb.PartEnvMapped"/>) or the DIFFUSE declares one in its <c>.txi</c>
    /// (<c>envmaptexture</c>, <c>Material::ParseField</c> @0x5c5850).</summary>
    internal static bool DbEnvMapped(int part, TexId diffuse)
        => SceneDb.PartEnvMapped(part) || TexRegistry.EnvMapped(diffuse);

    /// <summary>Does any slot of this recipe resolve through the PLT shader? The staging decision a worker
    /// cannot make, because <c>PltColors.Stage</c> writes a shared upload buffer.</summary>
    private static bool DbPartHasPlt(int recipe)
    {
        for (int s = 0; s < DrawRecipe.TexSlots; s++) if (DrawRecipe.TexIsPlt(recipe, s)) return true;
        return false;
    }

    /// <summary>The slot's WORLD box, which is what the area index files on. The recipe's box is model space
    /// (that is what lets one range serve every instance), so it goes through the part's matrix here -- the
    /// engine path read a box <c>ComputePartBBox</c> had already filled in world space at tile spawn.</summary>
    internal static void DbWriteWorldBox(int i, int recipe, in Matrix4x4 m)
    {
        Span<float> b = stackalloc float[6];
        DrawRecipe.Box(recipe, b);
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var p = new Vector3(b[(corner & 1) != 0 ? 3 : 0], b[(corner & 2) != 0 ? 4 : 1], b[(corner & 4) != 0 ? 5 : 2]);
            Vector3 w = Vector3.Transform(p, m);
            lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
        }
        int o = i * 6;
        // CX-188: the gen counts a box that MOVED, not a call. The caller gates this on `rec`, which is also
        // true for a reveal change, a staleness re-send and the per-frame roll -- so a counter bumped per call
        // would move on a still frame and no reader could skip on it. Six compares against six stores.
        if (_oBox[o] != lo.X || _oBox[o + 1] != lo.Y || _oBox[o + 2] != lo.Z
            || _oBox[o + 3] != hi.X || _oBox[o + 4] != hi.Y || _oBox[o + 5] != hi.Z)
        {
            _oBox[o] = lo.X; _oBox[o + 1] = lo.Y; _oBox[o + 2] = lo.Z;
            _oBox[o + 3] = hi.X; _oBox[o + 4] = hi.Y; _oBox[o + 5] = hi.Z;
            BoxGen++;
        }
    }

    /// <summary>Bumped wherever a slot's <see cref="_oBox"/> actually CHANGES -- which is
    /// <see cref="DbWriteWorldBox"/> and nothing else -- so a reader whose answer is a function of those boxes
    /// (<c>DbKeyhole.Write</c>'s dissolve verdict) re-derives only when this moves. Not interlocked on purpose:
    /// the staging walk runs on worker threads and this is only ever COMPARED, never counted.</summary>
    internal static int BoxGen;

    /// <summary>This slot's batch VAO key -- the arena SECTION its range lives in, so the static and skinned
    /// formats cut apart and nothing else does. Taken from the RANGE, not the recipe's skin flag: the arena
    /// decides the section, and a key from a second opinion binds the batch to the wrong VAO when they
    /// disagree (<c>MeshArenaDraw.Vao</c> is the static section's only).</summary>
    private static int DbVaoKey(int i)
        => (int)MeshArena.FormatOf(DrawRecipe.Range(_oDbRecipe[i]));

    /// <summary>The VAO and mesh-LOD level the arena gave this slot's last stage. Per slot because the batch
    /// cut reads them back and a skinned range's VAO is not the static one's.</summary>
    internal static readonly uint[] _oDbVao = new uint[MaxOwned];
    internal static readonly byte[] _oDbLevel = new byte[MaxOwned];

    /// <summary>Chart offers left this frame. An area's whole tile set would otherwise be read off the arena
    /// in one pass -- each offer takes the arena lock and allocates the job's arrays -- so the enrolment is
    /// dripped over frames.</summary>
    internal static int _dbOfferBudget;
    /// <summary>Offers one frame may make. Small: the charter's own queue is the real throttle and a miss
    /// costs the next frame nothing.</summary>
    private const int DbOffersPerFrame = 8;
    /// <summary>Called once per world pass, before any part stages.</summary>
    internal static void DbChartFrame() => _dbOfferBudget = DbOffersPerFrame;
    /// <summary>DB slots staged whose object had already been freed -- a feed bug, counted rather than drawn
    /// against object 0.</summary>
    internal static int _dbOrphan;
    /// <summary>Records the DB stage has turned the env-map bit on for, for the <c>[db]</c> census.</summary>
    internal static int _dbEnv;
    private static int _dbDumped;
    private static int _dbRecDump;
    private static bool _dbDumpedTile;
}
