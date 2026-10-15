using System;
using System.Numerics;
using CX.Alloy.Seam;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>One frame, dumped as <c>key=value</c> lines <c>diff</c> can line up: every input the picture is a
/// function of -- camera, frame uniforms, the light list as the cluster pass receives it, the full draw record
/// of a handful of parts picked by WORLD POSITION rather than by slot, so two dumps name the same tile. Armed
/// by <c>alloy.framediff</c> = the pass to dump at, counted from the area edge (0 = off).</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary><c>CxKeyhole</c>'s inputs and its verdict, re-evaluated ON THE CPU from the very uniform values
    /// the frame uploads -- a GPU readback is not an option on this driver, where any readback waits out the
    /// whole queued frame. Taken at a representative ROOF texel directly above the player; the screen-space
    /// aperture disc is a function of gl_FragCoord, so what prints is the uniforms and the FLOOR clamp.</summary>
    private static void KeyholeTerms(Action<string> P)
    {
        ShaderExposedRuntimeData rt = Rt;
        if (rt.Ptr == 0 || _shaderConstF == null) { P("keyhole: no runtime block or shader constants"); return; }
        float diaA = _shaderConstF[KeyholeDiaA], diaB = _shaderConstF[KeyholeDiaB];
        float camDist = rt.CamDist, camPitch = rt.CamPitch;
        Vector3 player = rt.PlayerPos;
        // off = depth - uKeyholeCam.x + (90 - uKeyholeCam.y) / 45, at a roof texel half the camera range away:
        // a roof between the camera and the player is the case the aperture exists for.
        float depth = camDist * 0.5f;
        float off = depth - camDist + (90f - camPitch) / 45f;
        // ...and the floor clamp, which multiplies the whole level: clamp(world.z - 1 - uPlayerPos.z, 0, 1).
        // CX_KH_FLOOR is 1.0. A roof three metres over the player's feet is the generous case.
        float roofZ = player.Z + 3f;
        float floorTerm = Math.Clamp(roofZ - 1f - player.Z, 0f, 1f);
        P($"keyhole: dia=({diaA:F3},{diaB:F3}) camDist={camDist:F2} camPitch={camPitch:F2} "
        + $"player=({player.X:F2},{player.Y:F2},{player.Z:F2})");
        P($"keyhole: off={off:F3} ({(off >= 0f ? "CUT REFUSED -- off >= 0" : "ok")}) "
        + $"floorTerm={floorTerm:F3} ({(floorTerm <= 0f ? "CUT REFUSED -- level zeroed" : "ok")}) "
        + $"aperture={(diaB > 0f ? "ok" : "CUT REFUSED -- zero diameter")}");
    }

    private static bool _diffDone;
    /// <summary>World passes since the last area edge, reset by <see cref="AlloyRenderer.AreaEdge"/>, so the
    /// knob means "N world passes after this area came up". <c>Counters._frames</c> cannot serve: it is the
    /// PERIOD counter, reset every reporting period, so it never reaches a value past one period's length.</summary>
    private static long _diffPass;

    /// <summary>Everything the camera-follow object (the PC) carries, so a wrong focus Z names the column that
    /// holds it.</summary>
    private static string DbFollowDump()
    {
        DbId cam = SceneInputs.DbWorldCamera;
        if (!SceneDb.Cameras.Valid(cam)) return "no camera";
        DbId o = SceneDb.CamFollow[cam.Index];
        if (!SceneDb.Objects.Valid(o)) return $"cam={cam.Value} follows nothing";
        int i = o.Index;
        nint shell = Facade.ObjectShell(o);
        Vector3 sp = shell != 0 ? Facade.ShellPos(shell) : new Vector3(float.NaN);
        return $"obj={o.Value} model='{SceneDb.ObjSeamModelName[i]}' flags={SceneDb.ObjFlags[i]} "
             + $"scene={SceneDb.ObjScene[i].Value} attachTo={SceneDb.ObjAttachTo[i].Value} "
             + $"pos={F3(SceneDb.ObjPos[i])} world.t={F3(SceneDb.ObjWorld[i].Translation)} "
             + $"shell={(shell != 0 ? "yes" : "NO")} shellPos={F3(sp)}";
    }

    /// <summary>Restart the count: a new area is a new settling.</summary>
    internal static void FrameDiffAreaEdge() { _diffPass = 0; _diffDone = false; }

    /// <summary>Dump this frame if the knob names this pass. Called once per world pass, after the records are
    /// staged and the frame's uniforms are final.</summary>
    internal static void FrameDiffDump()
    {
        int want = Renderer.FrameDiff.Boot;
        if (want <= 0 || _diffDone) return;
        if (++_diffPass < want) return;
        _diffDone = true;
        void P(string s) => _log?.Always($"[framediff] {s}");

        // ---- (1) camera ----
        ref readonly AlloyView v = ref AlloyCamera.WorldView;
        P($"cam.valid={v.Valid} cam.persp={v.Perspective} cam.fovy={v.Fovy:F4} cam.aspect={v.Aspect:F4}");
        P($"cam.near={v.Near:F4} cam.far={v.Far:F4} cam.depth={v.DepthNear:F4}..{v.DepthFar:F4}");
        P($"cam.viewport={v.X},{v.Y},{v.W},{v.H}");
        P($"cam.eye={F3(_eyePos)}");
        Mat("cam.view", v.View);
        Mat("cam.proj", v.Proj);

        // ---- (2) frame uniforms ----
        P($"fi.areaAmb={F3(FrameInputs.AreaAmbient)} has={FrameInputs.HasAreaAmbient}");
        P($"fi.areaDif={F3(FrameInputs.AreaDiffuse)} has={FrameInputs.HasAreaDiffuse} dir={F3(FrameInputs.AreaDiffuseDir)}");
        P($"fi.sun={F3(FrameInputs.SunDir)} has={FrameInputs.HasSun}");
        // The engine's OWN area uniforms: renderer state rather than scene memory.
        P($"engine.area {FrameRead.EngineAreaTerms()}");
        KeyholeTerms(P);
        // The AREA's own palette, the game-layer source of the diffuse term: what the derived value above is
        // checked against.
        CNWCArea dbg = SceneInputs.CurrentArea();
        // A null area scene and a live facade are different faults, so both fog words are printed.
        if (!dbg.IsNull)
        {
            Scene asc = dbg.Scene;
            P($"area.scene={dbg.Scene.Ptr:x} fogEnable={(asc.IsNull ? -1 : asc.FogEnable)} "
            + $"start={(asc.IsNull ? 0f : asc.FogStart):F3} end={(asc.IsNull ? 0f : asc.FogEnd):F3}");
        }
        if (!dbg.IsNull)
            P($"area.palette night={dbg.IsNight} dayDif={dbg.DayDiffuse[0]:F3},{dbg.DayDiffuse[1]:F3},{dbg.DayDiffuse[2]:F3} "
            + $"nightDif={dbg.NightDiffuse[0]:F3},{dbg.NightDiffuse[1]:F3},{dbg.NightDiffuse[2]:F3} "
            + $"dayAmb={dbg.DayAmbient[0]:F3},{dbg.DayAmbient[1]:F3},{dbg.DayAmbient[2]:F3} "
            + $"nightAmb={dbg.NightAmbient[0]:F3},{dbg.NightAmbient[1]:F3},{dbg.NightAmbient[2]:F3} "
            + $"nightFog={dbg.NightFog[0]:F3},{dbg.NightFog[1]:F3},{dbg.NightFog[2]:F3}/{dbg.NightFogAmount} "
            + $"dayFog={dbg.DayFog[0]:F3},{dbg.DayFog[1]:F3},{dbg.DayFog[2]:F3}/{dbg.DayFogAmount}");
        P($"fi.fog={FrameInputs.FogOn} start={FrameInputs.FogStart:F3} end={FrameInputs.FogEnd:F3} col={F3(FrameInputs.FogColor)}");
        P($"fi.interior={FrameInputs.Interior} focus={F3(FrameInputs.Focus)} has={FrameInputs.HasFocus}");
        P("follow." + DbFollowDump());
        ref Target t = ref _passTarget;
        P($"target.clear={t.Clear0:F4},{t.Clear1:F4},{t.Clear2:F4},{t.Clear3:F4} depth={t.ClearDepth:F3} srgb={t.Srgb}");
        P($"target.vp={t.Vp0},{t.Vp1},{t.Vp2},{t.Vp3}");

        // ---- (3) the light list, as the cluster pass receives it ----
        P($"lights.area={FrameInputs.LightCount} local={FrameInputs.LocalLightCount} withCube={FrameInputs.LightsWithCube}");
        int ln = Math.Min(FrameInputs.LightCount, 12);
        for (int i = 0; i < ln; i++)
        {
            Vector4 pr = FrameInputs.AreaLightPosRange[i];
            Vector3 c = FrameInputs.AreaLightColor[i];
            P($"light[{i}] pos={pr.X:F2},{pr.Y:F2},{pr.Z:F2} r={pr.W:F2} rgb={c.X:F3},{c.Y:F3},{c.Z:F3} "
              + $"amb={FrameInputs.AreaLightAmbient[i]} static={FrameInputs.AreaLightStatic[i]}");
        }

        // ---- (4) the five parts NEAREST THE PLAYER, so two dumps line up on the region in question ----
        Vector3 anchor = Rt.Ptr != 0 ? Rt.PlayerPos : _eyePos;
        int n = 0;
        Span<int> pick = stackalloc int[SampleParts];
        Span<float> key = stackalloc float[SampleParts];
        fixed (float* data = _dData)
            for (int k = 0; k < _orderCount; k++)
            {
                int i = _order[k];
                if (_oTri[i] == IntPtr.Zero) continue;
                // NEAREST THE PLAYER, not lowest world coordinate: sorting on the model translation samples
                // border-ring tiles at the far corner of the area, which say nothing about the room in question.
                float dx = _oBx[i] - anchor.X, dy = _oBy[i] - anchor.Y, dz = _oBz[i] - anchor.Z;
                float kq = dx * dx + dy * dy + dz * dz;
                if (n < SampleParts) { pick[n] = i; key[n] = kq; n++; }
                else
                {
                    int worst = 0;
                    for (int q = 1; q < SampleParts; q++) if (key[q] > key[worst]) worst = q;
                    if (kq < key[worst]) { pick[worst] = i; key[worst] = kq; }
                }
            }
        // insertion sort the sample so every dump prints it in the same order
        for (int a = 1; a < n; a++)
        {
            int pi = pick[a]; float pk = key[a]; int b = a - 1;
            for (; b >= 0 && key[b] > pk; b--) { pick[b + 1] = pick[b]; key[b + 1] = key[b]; }
            pick[b + 1] = pi; key[b + 1] = pk;
        }
        P($"parts.ordered={_orderCount} sampled={n}");
        fixed (float* data = _dData)
            for (int s = 0; s < n; s++)
            {
                int i = pick[s];
                float* r = &data[(long)i * DrawFloats];
                Matrix4x4 mm = *(Matrix4x4*)r;
                P($"part[{s}] at={mm.M41:F2},{mm.M42:F2},{mm.M43:F2} cls={_oCls[i]} lit={_oLit[i]} "
                  + $"slots={_oSlots[i]} feat={r[40]:F1},{r[41]:F1},{r[42]:F1},{r[43]:F1}");
                P($"part[{s}] amb={r[20]:F3},{r[21]:F3},{r[22]:F3},{r[23]:F3} dif={r[24]:F3},{r[25]:F3},{r[26]:F3},{r[27]:F3}");
                P($"part[{s}] emi={r[28]:F3},{r[29]:F3},{r[30]:F3},{r[31]:F1} rot={r[16]:F3},{r[17]:F3},{r[18]:F3},{r[19]:F3}");
                P($"part[{s}] pbr={r[32]:F3},{r[33]:F3},{r[34]:F3},{r[35]:F1} spc={r[36]:F3},{r[37]:F3},{r[38]:F3},{r[39]:F3}");
                P($"part[{s}] ext={r[ExtFloat]:F3},{r[ExtFloat + 1]:F3},{r[ExtFloat + 2]:F3},{r[ExtFloat + 3]:F3} "
                  + $"hidden={r[RevealFloat]:F3} tid={_recTid[i]} slotmask={_recSlotMask[i]}");
                fixed (float* sp = _dSph)
                    P($"part[{s}] sphere={sp[i * 4]:F2},{sp[i * 4 + 1]:F2},{sp[i * 4 + 2]:F2},r={sp[i * 4 + 3]:F2} "
                      + $"count={_dCount[i]} base={_dBase[i]} off={(long)_dOff[i]}");
            }
        // ---- (5) the BLENDED population, named ----
        // Picked by NODE NAME off the part's model, which settles WHICH record a complaint is about: the
        // invisible-helper population is full of same-shaped planes. Walked over the OWNERSHIP TABLE, not
        // _order, and nearest the player, since the table's first N is never what is on screen in a big area.
        int bn = 0;
        Span<int> bpick = stackalloc int[BlendSample];
        Span<float> bkey = stackalloc float[BlendSample];
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero || _oCls[i] != (int)AlloyClass.Blended || _dCount[i] <= 0) continue;
            float dx = _oBx[i] - anchor.X, dy = _oBy[i] - anchor.Y, dz = _oBz[i] - anchor.Z;
            float kq = dx * dx + dy * dy + dz * dz;
            if (bn < BlendSample) { bpick[bn] = i; bkey[bn] = kq; bn++; }
            else
            {
                int worst = 0;
                for (int q = 1; q < BlendSample; q++) if (bkey[q] > bkey[worst]) worst = q;
                if (kq < bkey[worst]) { bpick[worst] = i; bkey[worst] = kq; }
            }
        }
        for (int a = 1; a < bn; a++)
        {
            int pi = bpick[a]; float pk = bkey[a]; int b = a - 1;
            for (; b >= 0 && bkey[b] > pk; b--) { bpick[b + 1] = bpick[b]; bkey[b + 1] = bkey[b]; }
            bpick[b + 1] = pi; bkey[b + 1] = pk;
        }
        fixed (float* data = _dData)
            for (int s = 0; s < bn; s++)
            {
                int i = bpick[s];
                int dbp = _oDbPart[i];
                string node = dbp >= 0
                    ? Db.PartTree.NameOf(dbp)
                    : (_oNode[i] == IntPtr.Zero ? "" : new MdlNode(_oNode[i]).NameText ?? "");
                // The model and the texture it asked for: a record compared lane for lane is only a diff
                // once both dumps agree on WHICH node they are printing.
                string model = "", tex = "";
                if (dbp >= 0)
                {
                    Db.DbId po = Db.SceneDb.PartObj[dbp];
                    if (Db.SceneDb.Objects.Valid(po)) model = Db.SceneDb.ObjSeamModelName[po.Index];
                    int rec = Db.SceneDb.PartRecipe[dbp];
                    Mdl.MeshDef? md = rec >= 0 ? Db.DrawRecipe.Mesh(rec) : null;
                    if (md != null) tex = md.Textures[0];
                }
                float* r = &data[(long)i * DrawFloats];
                Matrix4x4 mm = *(Matrix4x4*)r;
                P($"blend[{s}] node='{node}' model='{model}' tex0='{tex}' at={mm.M41:F2},{mm.M42:F2},{mm.M43:F2} "
                  + $"cls={_oCls[i]} lit={_oLit[i]} nocull={_oNoCull[i]} src={_oSrc[i]} dst={_oDst[i]} wash={_oWash[i]}");
                P($"blend[{s}] amb={r[20]:F3},{r[21]:F3},{r[22]:F3},{r[23]:F3} dif={r[24]:F3},{r[25]:F3},{r[26]:F3},{r[27]:F3}");
                P($"blend[{s}] emi={r[28]:F3},{r[29]:F3},{r[30]:F3},{r[31]:F1} rot={r[16]:F3},{r[17]:F3},{r[18]:F3},{r[19]:F3} "
                  + $"feat={r[40]:F1},{r[41]:F1},{r[42]:F1},{r[43]:F1}");
                P($"blend[{s}] tid={_recTid[i]} slotmask={_recSlotMask[i]} count={_dCount[i]}");
            }
        P($"blend.count={bn}");
        KeyholeColumn(P);
        P("end");
    }

    /// <summary>Parts sampled per dump. Enough to see a pattern, few enough to read.</summary>
    private const int SampleParts = 5;

    /// <summary>Blended parts sampled per dump, nearest the player. The population the invisible-helper
    /// questions live in is larger than the opaque sample needs to be.</summary>
    private const int BlendSample = 16;

    /// <summary>Parts reported down the centre ray. The roof is one of the first few.</summary>
    private const int KhProbe = 6;

    /// <summary>What the screen centre is looking at, and whether the keyhole would cut it: the staged parts
    /// the centre ray pierces, nearest first, each with the enable bit the stage wrote, the record's dissolve
    /// lane, and <c>CxKeyhole</c> re-evaluated at that fragment on the CPU. The noise term is taken at its mean
    /// (0,5) -- its own 0,35 weight bounds how far the simplex field could shift a verdict.</summary>
    private static void KeyholeColumn(Action<string> P)
    {
        ShaderExposedRuntimeData rt = Rt;
        if (rt.Ptr == 0 || _shaderConstF == null) { P("khcol: no runtime block or shader constants"); return; }
        ref readonly AlloyView v = ref AlloyCamera.WorldView;
        if (!Matrix4x4.Invert(v.View, out Matrix4x4 camToWorld)) { P("khcol: the view is not invertible"); return; }
        // The centre pixel's ray: view -Z through the inverted view matrix, which is the same reconstruction
        // _eyePos itself comes from.
        var fwd = Vector3.Normalize(new Vector3(-camToWorld.M31, -camToWorld.M32, -camToWorld.M33));
        Vector3 eye = _eyePos;
        float dlo = MathF.Min(_shaderConstF[KeyholeDiaA], _shaderConstF[KeyholeDiaB]);
        float dhi = MathF.Max(_shaderConstF[KeyholeDiaA], _shaderConstF[KeyholeDiaB]);
        Span<int> pick = stackalloc int[KhProbe];
        Span<float> key = stackalloc float[KhProbe];
        int n = 0;
        for (int k = 0; k < _stageRouted; k++)
        {
            int i = _pass[k];
            if (_pkVerdict[k] != Staged || _dCount[i] <= 0) continue;
            if (!CentreRayHits(eye, fwd, i, out float t)) continue;
            if (n < KhProbe) { pick[n] = k; key[n] = t; n++; }
            else
            {
                int worst = 0;
                for (int q = 1; q < KhProbe; q++) if (key[q] > key[worst]) worst = q;
                if (t < key[worst]) { pick[worst] = k; key[worst] = t; }
            }
        }
        for (int a = 1; a < n; a++)
        {
            int pk = pick[a]; float kq = key[a]; int b = a - 1;
            for (; b >= 0 && key[b] > kq; b--) { pick[b + 1] = pick[b]; key[b + 1] = key[b]; }
            pick[b + 1] = pk; key[b + 1] = kq;
        }
        P($"khcol: eye={F3(eye)} fwd={F3(fwd)} hits={n} of {_stageRouted} routed");
        fixed (float* data = _dData)
        fixed (float* bx = _oBox)
            for (int s = 0; s < n; s++)
            {
                int k = pick[s], i = _pass[k];
                float t = key[s];
                float* b = &bx[(long)i * 6];
                float* r = &data[(long)i * DrawFloats];
                string node = _oDbPart[i] >= 0
                    ? Db.PartTree.NameOf(_oDbPart[i])
                    : (_oNode[i] == IntPtr.Zero ? "" : new MdlNode(_oNode[i]).NameText ?? "");
                // CxKeyhole at the centre pixel: d2 is 0 there by construction, so only off, the radius and
                // the floor clamp can refuse the cut -- and each is printed beside its verdict.
                float off = t - rt.CamDist + (90f - rt.CamPitch) / 45f;
                float rad = Math.Clamp((dhi - dlo) * dlo * 12.5f / t + dlo, dlo, dhi);
                float worldZ = eye.Z + fwd.Z * t;
                float floorTerm = Math.Clamp(worldZ - 1f - rt.PlayerPos.Z, 0f, 1f);
                float lvl = (1f - 0.5f * 0.35f + (off < 0f ? 0.5f / off : 0f)) * floorTerm;
                bool punch = r[18] > 0f && off < 0f && rad > 0f && lvl >= 0.3f;
                P($"khcol[{s}] depth={t:F2} slot={i} dbpart={_oDbPart[i]} node='{node}' cls={_oCls[i]} "
                + $"lit={_oLit[i]} dis={_pkDis[k]} rec.dissolve={r[18]:F2} tid={_recTid[i]} "
                + $"feat={r[40]:F1},{r[41]:F1} box=({b[0]:F1},{b[1]:F1},{b[2]:F1})..({b[3]:F1},{b[4]:F1},{b[5]:F1})");
                P($"khcol[{s}] off={off:F2} r={rad:F3} worldZ={worldZ:F2} floorTerm={floorTerm:F2} "
                + $"lvl={lvl:F3} -> {(punch ? "PUNCH" : r[18] <= 0f ? "not enabled" : off >= 0f ? "off >= 0" : floorTerm <= 0f ? "floor clamp" : "level below 0,3")}");
            }
    }

    /// <summary>Does the centre ray pierce this slot's world box, and at what distance? The slab test, with
    /// the entry distance clamped at the eye so a box the camera is inside reports 0.</summary>
    private static bool CentreRayHits(Vector3 eye, Vector3 fwd, int i, out float t)
    {
        t = 0f;
        fixed (float* bx = _oBox)
        {
            float* b = &bx[(long)i * 6];
            if (!(b[3] > b[0]) && !(b[4] > b[1]) && !(b[5] > b[2])) return false;   // never filled
            float lo = 0f, hi = float.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                float o = a == 0 ? eye.X : a == 1 ? eye.Y : eye.Z;
                float d = a == 0 ? fwd.X : a == 1 ? fwd.Y : fwd.Z;
                float mn = b[a], mx = b[a + 3];
                if (MathF.Abs(d) < 1e-6f) { if (o < mn || o > mx) return false; continue; }
                float t0 = (mn - o) / d, t1 = (mx - o) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                if (t0 > lo) lo = t0;
                if (t1 < hi) hi = t1;
                if (lo > hi) return false;
            }
            t = lo;
            return true;
        }
    }

    private static string F3(Vector3 v) => $"{v.X:F3},{v.Y:F3},{v.Z:F3}";

    private static void Mat(string name, in Matrix4x4 m)
    {
        _log?.Always($"[framediff] {name}=[{m.M11:F4},{m.M12:F4},{m.M13:F4},{m.M14:F4}]"
                   + $"[{m.M21:F4},{m.M22:F4},{m.M23:F4},{m.M24:F4}]"
                   + $"[{m.M31:F4},{m.M32:F4},{m.M33:F4},{m.M34:F4}]"
                   + $"[{m.M41:F4},{m.M42:F4},{m.M43:F4},{m.M44:F4}]");
    }
}
