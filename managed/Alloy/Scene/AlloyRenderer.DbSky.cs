using CX.Engine;
using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b><c>StageSky</c> for the scene database.</b> A sky object carries <see cref="ObjFlag.Sky"/> and
/// so is never in the ordinary draw; this stages it out of <see cref="DbSky.Roles"/> and the sky objects' own
/// part ranges -- one draw record per part with the composed model matrix and the role's alpha in
/// <c>rot.w</c>, the fog tint branch, and all-owned-or-none so a refusal leaves no hole in the sky.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Sky parts refused for want of a resident arena range, and roles refused whole. Counted
    /// rather than logged per frame: the sky restages every pass.</summary>
    private static int _dbSkyNoRange, _dbSkyRefused, _dbSkyRoles, _dbSkyStaged;

    /// <summary>The sky, staged from the scene database. Returns with <see cref="_skyN"/> at 0 (and the draw
    /// cursor rewound) for a scene that draws no sky, which is an interior and not a failure.</summary>
    private static void StageDbSky(DbId scene)
    {
        _skyN = 0; _skyFirst = _drawN;
        if (SkyBox.Disabled || !SceneDb.Scenes.Valid(scene)) { SceneFog.EndSky(); return; }
        Span<DbSky.Role> roles = stackalloc DbSky.Role[DbSky.MaxRoles];
        int nr = DbSky.Roles(scene, roles);
        _dbSkyRoles = nr;
        NoteSkyState(scene, nr);
        // The dome is parked at the scene focus, exactly as Scene::RenderSkyBoxes @0x466910 parks it: the
        // object's own placement is wherever it was created, and the engine path corrects for that the same
        // way (SkyBox.Capture: pos += SkyPos - Gob.Position).
        Vector3 focus = SceneDb.SceneFocus[scene.Index];
        for (int r = 0; r < nr; r++)
        {
            DbId obj = roles[r].Obj;
            if (!SceneDb.Objects.Valid(obj) || !SceneDb.ObjVisible[obj.Index]) { _dbSkyRefused++; continue; }
            int o = obj.Index;
            Vector3 park = focus - SceneDb.ObjPos[o];
            int first = SceneDb.ObjPartFirst[o], count = SceneDb.ObjPartCount[o];
            int staged = 0;
            for (int p = first; p < first + count; p++)
            {
                if ((uint)p >= SceneDb.MaxParts || !SceneDb.Parts.LiveAt(p)) continue;
                if (!SceneDb.PartEnabled[p]) continue;
                int recipe = SceneDb.PartRecipe[p];
                if (recipe < 0) continue;
                if (_drawN >= MaxOwned || _skyN >= MaxSkyDraws) { staged = -1; break; }
                if (!MeshArena.Draw(DrawRecipe.Range(recipe), 0, out uint vao, out int baseVertex,
                                    out nint off, out int idxCount, out _)
                    || idxCount <= 0 || vao == 0)
                { _dbSkyNoRange++; staged = -1; break; }
                // A material naming its own fragment stage (a content sky) is that stage, hosted; a
                // stage that will not build refuses the sky rather than drawing the bare dome texture.
                uint prog = SkyProgram(SceneDb.PartMtr[p]);
                if (prog == uint.MaxValue) { staged = -1; break; }
                TexRegistry.TryResident(DbTexOf(p, recipe, 0), out uint texName, out _);
                if (texName == 0) { staged = -1; break; }

                int n = _drawN;
                _dCount[n] = idxCount;
                _dOff[n] = off;
                _dBase[n] = baseVertex;
                // The sky's records live in the reserved block above the ownership slots, restaged every pass.
                int srec = _recOwnCap + _skyN;
                _dSlot[n] = srec;
                MarkRecordDirty(srec);
                ref DrawRecordBlock rec = ref _dData[srec];
                Matrix4x4 m = SceneDb.PartWorld[p];
                m.M41 += park.X; m.M42 += park.Y; m.M43 += park.Z;
                rec.Model = m;
                // identity texture rotation, no dissolve, the role's alpha; no feature, and the lit block is
                // never read because the sky takes an unlit stage (the engine draws it Disable(5)).
                bool tinted = (SceneDb.ObjFlags[o] & ObjFlag.FogTint) != 0;
                ref Vector3 tint = ref _skTint[_skyN];
                if (tinted) { fixed (float* t = &tint.X) SceneFog.SkyfadeTint(t); }
                else
                {
                    tint = Vector3.One;
                    // the dome feeds the fog its sky colour; the fog-tinted band must not (it is the fog), and
                    // neither may a hosted stage's diffuse -- fst_sky's is a night moon mask
                    if (prog == 0) SceneFog.NoteSkyDb(DbTexOf(p, recipe, 0), roles[r].Alpha);
                }
                rec.Cos = 1f; rec.Sin = 0f; rec.Dissolve = 0f;
                rec.Alpha = tinted ? 1f : roles[r].Alpha;
                rec.FeatX = 0f; rec.FeatY = 0f; rec.FeatZ = 0f; rec.FeatW = 0f;
                ref SkyTexUnits tex = ref _skTex[_skyN];
                tex[0] = (int)texName;
                // Every unit the stage could declare, 0 included: an unbound unit samples black under the
                // engine, never a neighbour's texture.
                for (int k = 1; k < MaxEngTex; k++)
                {
                    TexId id = prog != 0 ? DbTexOf(p, recipe, k) : default;
                    uint name = 0;
                    if (id.Value != 0) TexRegistry.TryResident(id, out name, out _);
                    if (name == 0 && TexRegistry.StateOf(id) == TexState.Loading) { staged = -1; break; }
                    tex[k] = (int)name;
                }
                if (staged < 0) break;
                _skFmt[_skyN] = (int)MeshArena.FormatOf(DrawRecipe.Range(recipe)); _skVaoName[_skyN] = vao;
                _skMode[_skyN] = Gl.TRIANGLES;   // gl-ok: an enum value staged for the pass, no call
                _skSrc[_skyN] = roles[r].Src; _skDst[_skyN] = roles[r].Dst;
                _skAlpha[_skyN] = rec.Alpha;
                _skProg[_skyN] = prog;
                _skyN++; _drawN = n + 1; staged++;
            }
            // All-owned-or-none, as the engine path is: a role that cannot stage every one of its
            // parts would leave a permanent hole in the sky, so the whole sky sits the frame out.
            if (staged <= 0)
            { _dbSkyRefused++; _drawN = _skyFirst; _skyN = 0; SceneFog.EndSky(); return; }
        }
        _dbSkyStaged = _skyN;
        SceneFog.EndSky();
    }

    /// <summary>The hosted program for a sky part's material: 0 for the plain unlit stage (no custom stage),
    /// <see cref="uint.MaxValue"/> for a custom fragment stage that did not build.</summary>
    private static uint SkyProgram(MtrDef? mtr)
    {
        if (mtr == null || mtr.ShaderFs.Length == 0) return 0;
        uint prog = SkyStage.Program(mtr.ShaderFs);
        return prog != 0 ? prog : uint.MaxValue;
    }

    /// <summary>What the scene's four sky slots hold, once per scene. A sky that never draws is either a
    /// scene the game gave no sky (an interior) or one whose slots are set and whose roles refuse, and the
    /// two are indistinguishable from the count alone.</summary>
    private static void NoteSkyState(DbId scene, int roles)
    {
        int i = scene.Index;
        int valid = 0;
        for (int k = 0; k < 4; k++) if (SceneDb.Objects.Valid(SceneDb.SceneSky(scene, k))) valid++;
        // Latched on the STATE, not on the scene: a scene is handed its sky after the first frame it draws,
        // and a line said once per scene would have reported every area as skyless.
        long state = ((long)scene.Value << 8) | (long)(valid << 4) | (long)roles
                   | (SceneDb.SceneSkyOff[i] ? 1L << 40 : 0L);
        if (state == _dbSkySaid) return;
        _dbSkySaid = state;
        _log?.Info($"AlloyRenderer: [db sky] scene {scene.Value}: {roles} role(s), {valid} of 4 slots set, "
                 + $"off={SceneDb.SceneSkyOff[i]} cur={SceneDb.SceneSkyCur[i]} want={SceneDb.SceneSkyDesired[i]} "
                 + $"t={SceneDb.SceneSkyT[i]:F2} fade={SceneDb.Objects.Valid(SceneDb.SceneSkyFade[i])}");
    }

    private static long _dbSkySaid = -1;

    /// <summary>The DB sky's clause on the period line: what the scene offered and what reached the pass.
    /// "0 roles" is an interior; roles with nothing staged is a refusal with a reason beside it.</summary>
    internal static string DbSkyNote()
        => $"dbsky: {_dbSkyRoles} role(s), {_dbSkyStaged} draw(s)"
         + (_dbSkyNoRange > 0 ? $", {_dbSkyNoRange} with no arena range" : "")
         + (_dbSkyRefused > 0 ? $", {_dbSkyRefused} refused" : "");
}
