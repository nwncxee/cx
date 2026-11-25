using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// alloy.raytrace: the ray-traced scene the reflection resolve traces. One bottom level per arena range a rigid, solid
/// slot draws (its full-detail triangles), one top level a frame over every such slot -- off screen too, which is the
/// point -- and a per-slot geometry row so a hit can find its triangle's vertices and its diffuse.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Bottom levels made per frame at most: an area's first frames build its scene over a few frames, not one.</summary>
    private const int RtBlasPerFrame = 192;
    /// <summary>Report lines the scene says per session at most.</summary>
    private const int RtReports = 12;

    /// <summary>One slot's row as cx_ssrres_f.glsl reads it: the layout (and whether a hit is alpha-tested), first vertex,
    /// first index, stride in floats.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RtGeomRow
    {
        public uint FormatFlags, Base, First, Stride;
    }

    private const uint RtCutout = 1u << 8, RtBaked = 1u << 9;

    /// <summary>A stage's ray terms (cx_rtlib.glsl), resolved against its loose block.</summary>
    internal sealed class RtLocs
    {
        public int A = -1, V01 = -1, V23 = -1, E = -1, L = -1, Sun = -1, SunCol = -1, Amb = -1, Fog = -1, FogRange = -1, Shadow = -1;

        public static RtLocs Of(Func<string, int> loc) => new()
        {
            A = loc("uRtA"), V01 = loc("uRtV01"), V23 = loc("uRtV23"), E = loc("uRtE"), L = loc("uRtL"), Sun = loc("uRtSun"),
            SunCol = loc("uRtSunCol"), Amb = loc("uRtAmb"), Fog = loc("uRtFog"), FogRange = loc("uRtFogRange"), Shadow = loc("uRtShadow"),
        };
    }

    private sealed class VkRtState
    {
        public VkBackend? Dev;
        /// <summary>Arena range to its bottom level, with the tenant it was built for.</summary>
        public readonly Dictionary<int, (ulong Sig, int Blas)> Blas = new();
        public readonly BufferId[] Vbo = new BufferId[MeshArena.Formats];
        public BufferId Ebo;
        public RtInstance[] Inst = new RtInstance[1024];
        public readonly RtGeomRow[] Geom = new RtGeomRow[MaxRecords];
        public BufferId GeomBuf;
        public int GeomLo = int.MaxValue, GeomHi = -1;
        /// <summary>This frame's top level (0 = none) and what the resolve needs beside it.</summary>
        public ulong Tlas;
        public int Instances;
        public bool Said;
        /// <summary>The report's frame counter and lines said.</summary>
        public int Frames, Reports;
    }

    private static VkRtState _vkRt = new();
    /// <summary>Per slot: shown in the world this frame (not faded, hidden or a hidden LOD), its shadow flag aside.</summary>
    private static readonly bool[] _rtOk = new bool[MaxOwned];

    /// <summary>Is the ray-traced scene live this frame: the device has it, the knob asks for it, and a top level was built.</summary>
    internal static bool RtLive => _vkRt.Tlas != 0;

    /// <summary>The frame's scene: bottom levels for new ranges, the top level over every rigid solid slot, the geometry
    /// rows uploaded, and the builds recorded into <paramref name="cl"/> (outside a render pass).</summary>
    private static void VkRtFrame(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        VkRtState s = _vkRt;
        s.Tlas = 0;
        if (!vk.RayTracing || !Renderer.RayTrace.Boot) return;
        if (s.Dev != vk) { _vkRt = s = new VkRtState { Dev = vk }; }
        // a grown arena buffer holds the same ranges at new addresses: every bottom level is rebuilt against it
        bool moved = !MeshArena.VkEbo.Equals(s.Ebo);
        for (int f = 0; f < MeshArena.Formats; f++) moved |= !MeshArena.VkVbo((MeshArena.Fmt)f).Equals(s.Vbo[f]);
        if (moved)
        {
            foreach (var kv in s.Blas) vk.RtFreeBlas(kv.Value.Blas);
            s.Blas.Clear();
            s.Ebo = MeshArena.VkEbo;
            for (int f = 0; f < MeshArena.Formats; f++) s.Vbo[f] = MeshArena.VkVbo((MeshArena.Fmt)f);
        }
        if (s.GeomBuf.IsNull)
            s.GeomBuf = vk.CreateBuffer(new BufferDesc((long)MaxRecords * sizeof(RtGeomRow), BufferUse.Storage | BufferUse.Address,
                                                       MemoryKind.Device, "vk ray geometry rows"));
        if (s.GeomBuf.IsNull) return;
        int made = 0, n = 0, noRecipe = 0, noCast = 0, notSolid = 0, posed = 0, noGeom = 0, failed = 0, unbuilt = 0;
        for (int i = 0; i < _ownCount && i < MaxOwned; i++)
        {
            int recipe = DbSlot(i) ? _oDbRecipe[i] : -1;
            if (recipe < 0) { noRecipe++; continue; }
            if (!_rtOk[i]) { noCast++; continue; }
            if (_oCls[i] > (byte)AlloyClass.Cutout) { notSolid++; continue; }
            if ((DrawRecipe.Flags(recipe) & (RecipeFlag.Dangly | RecipeFlag.Skinned | RecipeFlag.AnimMesh)) != 0) { posed++; continue; }
            int range = DrawRecipe.Range(recipe);
            // a baked slot traces its charted copy: the same surface, with the lightmap uvs a hit reads its lamps from
            bool baked = DbBakeable(i) && MeshChart.Range(MeshChart.DbSlot, range, 0, out _, out _, out _, out _);
            if (baked) range = MeshChart.RangeOf(MeshChart.DbSlot, range);
            if (!MeshArena.RayGeometry(range, out MeshArena.Fmt fmt, out int baseV, out int verts, out nint off, out int count, out ulong sig))
            { noGeom++; continue; }
            if (!s.Blas.TryGetValue(range, out var b) || b.Sig != sig)
            {
                if (made >= RtBlasPerFrame) continue;
                if (s.Blas.ContainsKey(range)) vk.RtFreeBlas(b.Blas);
                int stride = MeshArena.StrideBytes(fmt);
                b = (sig, vk.RtBlas(s.Vbo[(int)fmt], (long)baseV * stride, stride, verts, s.Ebo, off, count,
                                    opaque: _oCls[i] != (byte)AlloyClass.Cutout));
                s.Blas[range] = b;
                made++;
            }
            if (b.Blas < 0) { failed++; continue; }
            if (!vk.RtBlasReady(b.Blas)) unbuilt++;
            bool cutout = _oCls[i] == (byte)AlloyClass.Cutout;
            var row = new RtGeomRow
            {
                FormatFlags = (uint)fmt | (cutout ? RtCutout : 0u) | (baked ? RtBaked : 0u), Base = (uint)baseV, First = (uint)(off / sizeof(ushort)),
                Stride = (uint)(MeshArena.StrideBytes(fmt) / sizeof(float)),
            };
            if (!s.Geom[i].Equals(row)) { s.Geom[i] = row; s.GeomLo = Math.Min(s.GeomLo, i); s.GeomHi = Math.Max(s.GeomHi, i); }
            if (n == s.Inst.Length) Array.Resize(ref s.Inst, n * 2);
            s.Inst[n++] = new RtInstance { World = _dData[i].Model, Custom = (uint)i, Blas = b.Blas, Opaque = !cutout };
        }
        if (s.GeomHi >= s.GeomLo
            && vk.Upload(s.GeomBuf, (long)s.GeomLo * sizeof(RtGeomRow),
                         MemoryMarshal.AsBytes(s.Geom.AsSpan(s.GeomLo, s.GeomHi - s.GeomLo + 1))))
        { s.GeomLo = int.MaxValue; s.GeomHi = -1; }
        s.Tlas = vk.RtTlas(s.Inst.AsSpan(0, n));
        s.Instances = n;
        cl.RayBuild();
        // the scene's own report: a few frames into an area, and on while it stays empty, so a scene that never forms says why
        s.Frames++;
        if (s.Reports < RtReports && (s.Frames == 30 || s.Frames == 300 || (s.Tlas == 0 && s.Frames % 1800 == 0)))
        {
            s.Reports++;
            log.Always($"AlloyRenderer: [rt] {_ownCount} slots: {n} placed ({unbuilt} still building, top level {(s.Tlas != 0 ? "built" : "NONE")}); "
                     + $"skipped {noRecipe} engine-drawn, {noCast} hidden or faded out, {notSolid} blended or water, {posed} posed, "
                     + $"{noGeom} without resident geometry, {failed} refused ({vk.RayRefused ?? "none"}); {s.Blas.Count} meshes, {vk.RayBuilds} built");
        }
        if (!s.Said && s.Tlas != 0)
        {
            s.Said = true;
            log.Always($"AlloyRenderer: ray-traced reflections are LIVE -- {n} instances over {s.Blas.Count} meshes so far");
        }
    }

    /// <summary>The resolve's ray terms: the top level, the geometry rows, the four vertex layouts, the indices and the
    /// handle table by address (two words each, x low), then the light a hit is shaded by and the fog it fades into.</summary>
    private static void VkRtTerms(VkBackend vk, RtLocs? s)
    {
        if (s == null) return;
        VkRtState r = _vkRt;
        bool on = r.Tlas != 0;
        LooseBlock.Set(s.A, on ? Lo(r.Tlas) : 0, on ? Hi(r.Tlas) : 0, Lo(vk.DeviceAddress(r.GeomBuf)), Hi(vk.DeviceAddress(r.GeomBuf)));
        ulong v0 = vk.DeviceAddress(r.Vbo[0]), v1 = vk.DeviceAddress(r.Vbo[1]), v2 = vk.DeviceAddress(r.Vbo[2]), v3 = vk.DeviceAddress(r.Vbo[3]);
        LooseBlock.Set(s.V01, Lo(v0), Hi(v0), Lo(v1), Hi(v1));
        LooseBlock.Set(s.V23, Lo(v2), Hi(v2), Lo(v3), Hi(v3));
        ulong e = vk.DeviceAddress(r.Ebo), t = vk.DeviceAddress(_vkc.TexH);
        LooseBlock.Set(s.E, Lo(e), Hi(e), Lo(t), Hi(t));
        ulong lm = vk.DeviceAddress(LightmapBake.VkChart);
        LooseBlock.Set(s.L, Lo(lm), Hi(lm), lm != 0 ? LightmapBake.VkLivePages : 0, 0);
        Vector3 amb = FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient * AlloyLights.AmbientScale : new Vector3(DefaultAmbient);
        Vector3 dif = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * AlloyLights.SunScale : default;
        Vector3 dir = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuseDir : Vector3.UnitZ;
        LooseBlock.Set(s.Sun, dir.X, dir.Y, dir.Z, on ? 1f : 0f);
        LooseBlock.Set(s.SunCol, dif.X, dif.Y, dif.Z, 0f);
        LooseBlock.Set(s.Amb, amb.X, amb.Y, amb.Z, 0f);
        // the fog's colour (display space: decoded in the shader) and its linear start and end
        Vector3 fog = FrameInputs.FogColor;
        LooseBlock.Set(s.Fog, fog.X, fog.Y, fog.Z, FrameInputs.FogOn ? 1f : 0f);
        LooseBlock.Set(s.FogRange, [FrameInputs.FogStart, FrameInputs.FogEnd]);
        // shadow.raytrace: on, and the sun's disc as a cone half-angle in radians
        LooseBlock.Set(s.Shadow, [on && Shadows.RayTrace.V && !_capturing ? 1f : 0f, Shadows.RaySoft.V * MathF.PI / 180f]);
    }

    /// <summary>Off-device: the resolve's ray-traced variant translates and compiles to SPIR-V as the device would build it,
    /// and the plain one still does.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void VkRtSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkRtSelfCheck");
        string res = ShaderSource.Get("cx_ssrres_f.glsl");
        string rt = RtVariant(res);
        check.Expect(rt != res, "the ray-traced variant is a define after the version line");
        string? plain = PostChainVk.CompileCheck(PostChain.FullscreenVs, res), traced = PostChainVk.CompileCheck(PostChain.FullscreenVs, rt);
        check.Expect(plain == null, $"the plain resolve compiles ({plain})");
        check.Expect(traced == null, $"the ray-traced resolve compiles ({traced})");
        string? hist = PostChainVk.CompileCheck(PostChain.FullscreenVs, ShaderSource.Get("cx_ssrhist_f.glsl"));
        check.Expect(hist == null, $"the reflection history with the tile-edge blend compiles ({hist})");
        string? water = VkPrograms.WaterCompileCheck(false), waterRt = VkPrograms.WaterCompileCheck(true);
        check.Expect(water == null, $"the plain water stage compiles ({water})");
        check.Expect(waterRt == null, $"the ray-traced water stage compiles ({waterRt})");
        string? litRt = VkPrograms.WaterCompileCheck(true, lit: true);
        check.Expect(litRt == null, $"the ray-traced lit stage compiles ({litRt})");
        log.Info(check.Ok ? "AlloyRenderer.VkRtSelfCheck: passed" : "AlloyRenderer.VkRtSelfCheck: FAILED");
    }

    /// <summary>A stage's ray-traced variant: <c>CX_RT</c> defined after its version line.</summary>
    internal static string RtVariant(string glsl) => glsl.Replace(VkGlsl.Version, VkGlsl.Version + RtPrologue);

    /// <summary>What a ray-traced stage opens with: the define, and the extensions cx_rtlib.glsl needs ahead of any declaration.</summary>
    private const string RtPrologue = "#define CX_RT 1\n#extension GL_EXT_ray_query : require\n#extension GL_EXT_buffer_reference : require\n"
                                    + "#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require\n";

    /// <summary>alloy.tileblend's band each side of a grid line at full strength, and NWN's tile size, in metres.</summary>
    private const float TileBlendBand = 1.2f, TileMetres = 10f;

    private static int Lo(ulong a) => unchecked((int)(uint)(a & 0xFFFFFFFFu));
    private static int Hi(ulong a) => unchecked((int)(uint)(a >> 32));
}
