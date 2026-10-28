using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>CxGather</c> block in the GLSL's own member order (<c>cx_gatherblk.glsl</c>). C# sequential
/// layout IS std140 here: every vec3 is followed by the float that fills its fourth lane, and the only array
/// is of vec4.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GatherBlock
{
    public CullPlanes Planes;
    public Vector3 Focus;
    public float Reach;
    public Vector3 Dir;
    public float Along;
    public float LodR1, LodR2;
    public int DrawN, ViewN, Point, Cull, Set, Rx, Groups;

    /// <summary>The GLSL against this struct. A member moved in either file stages every term after it into its
    /// neighbour -- a wrong cull plane or another view's detail thresholds, with nothing in the log.</summary>
    internal static void Pin(Checker check)
    {
        var b = default(GatherBlock);
        Std140.Pin(check, ShaderSource.Get("cx_gatherblk.glsl"), "CxGather", UboSlots.Gather, Unsafe.SizeOf<GatherBlock>(),
        [
            ("uPlanes", Std140.At(ref b, ref b.Planes)), ("uFocus", Std140.At(ref b, ref b.Focus)),
            ("uReach", Std140.At(ref b, ref b.Reach)), ("uDir", Std140.At(ref b, ref b.Dir)),
            ("uAlong", Std140.At(ref b, ref b.Along)), ("uLodR1", Std140.At(ref b, ref b.LodR1)),
            ("uLodR2", Std140.At(ref b, ref b.LodR2)), ("uDrawN", Std140.At(ref b, ref b.DrawN)),
            ("uViewN", Std140.At(ref b, ref b.ViewN)), ("uPoint", Std140.At(ref b, ref b.Point)),
            ("uCull", Std140.At(ref b, ref b.Cull)), ("uSet", Std140.At(ref b, ref b.Set)),
            ("uRx", Std140.At(ref b, ref b.Rx)), ("uGroups", Std140.At(ref b, ref b.Groups)),
        ]);
    }
}

/// <summary>
/// Shadow caster selection in compute, in <see cref="CullPass"/>'s shape (ADR 0018): one staging pass per
/// FRAME, then per view a handful of uniforms, one dispatch that tests each caster and compacts the survivors
/// into an indirect command buffer, and one <c>glMultiDrawElementsIndirectCount</c> per batch -- no count ever
/// comes back to the CPU. The bounds are <see cref="CullPass"/>'s own spheres, so both culls test one set.
/// </summary>
internal static unsafe class CasterGather
{
    /// <summary>SSBO bindings private to this dispatch (<see cref="SsboSlots"/>), beside
    /// <see cref="CullPass.BindSphere"/>, which it also reads because the bounds are the colour cull's own.</summary>
    internal const uint BindSrc = SsboSlots.GatherSrc, BindCmd = SsboSlots.GatherCmd, BindCount = SsboSlots.GatherCount,
                        BindCast = SsboSlots.CastSphere;

    // CX-50: ...and the two COARSE runs of the same mesh, out of which every view picks one by its own texel
    // size. ONE record per caster whatever levels it has: the staging, its batch regions, the command buffer
    // and the thread count are all per caster, and a record per level would multiply every one of them --
    // four more words per record is the cheaper half of that trade. A level a mesh does not have is a zero
    // count, and the dispatch then bakes the finer run it does have, so a caster never bakes nothing.
    /// <summary>Uints per source record, and their order -- index count, first index, base vertex, OWNERSHIP
    /// slot (the sphere index, and the <c>baseInstance</c> the depth stages read <c>uDraw</c> through), batch,
    /// batch output base, the DYNAMIC flag (1 = a mover this frame, which the set filter tests), then count
    /// and first index at detail level 1 and at level 2. Otherwise <see cref="CullPass.SrcUints"/>'s layout.</summary>
    internal const int SrcUints = 11;
    /// <summary>The source word that carries the set flag -- the one word the standing staging rewrites
    /// per frame, so its index is a contract shared with <c>GatherCs</c>.</summary>
    internal const int SetFlagWord = 6;
    /// <summary>The coarse runs' words, a contract shared with both dispatches: count then first index,
    /// level 1 then level 2.</summary>
    internal const int Cnt1Word = 7, First1Word = 8, Cnt2Word = 9, First2Word = 10;

    /// <summary>Which half of the population a dispatch admits. <see cref="SetAll"/> = no filter (the
    /// sun, and the combined bake <c>/shadow split 0</c> restores); <see cref="SetStatic"/> = still casters
    /// only; <see cref="SetDynamic"/> = movers only. Applied BEFORE the cull and independent of
    /// <c>/shadow gather 0</c>, because the filter is what the two arrays' contents MEAN, not a cull.</summary>
    internal const int SetAll = 0, SetStatic = 1, SetDynamic = 2;
    /// <summary>Uints per <c>DrawElementsIndirectCommand</c>. Fixed by GL.</summary>
    internal const int CmdUints = 5, CmdBytes = CmdUints * 4;
    /// <summary>Count-buffer words past the per-batch counts: the receiver-cull tally, then triangles baked
    /// at each detail level. A shared contract with both dispatches, which index them off <c>uGroups</c>.</summary>
    internal const int CountExtra = 1 + MeshLod.Levels;
    /// <summary>Where one level's triangle tally sits in the count buffer -- past the per-batch counts and
    /// the receiver word. The one expression: both dispatches spell it and <see cref="Sample"/> reads it.</summary>
    internal static int TriWord(int groups, int level) => groups + 1 + level;
    /// <summary>Command/count buffer pairs rotated per VIEW, against a write-after-read: view K's indirect
    /// draws source their commands from one pair while view K+Rotation's dispatch rewrites it. Reusing a pair
    /// R views later leaves R-1 symmetric barriers standing between the last read and the rewrite, so 2 is the
    /// proven floor (pinned by <c>CasterPass.SelfCheck</c>) and 3 is that floor with one spare.</summary>
    internal const int Rotation = 3;

    /// <summary>Live dial behind <c>/shadow gather</c>: 1 = the dispatch culls, 0 = it admits every staged
    /// caster into every view, the same A/B pair <c>/alloy cull</c> spells for the colour path. The third
    /// bisect step is <c>shadow.mode=0</c>, which hands the whole shadow question back to the engine's
    /// stencil.</summary>
    internal static int Mode => Shadows.Gather.V ? 1 : 0;
    /// <summary>Live dial behind <c>/shadow receiver</c>: 1 = a view flagged as re-baked under this
    /// frame's camera also rejects casters whose shadow volume cannot reach the camera frustum; 0 = the
    /// light-cone tests alone. Inside <see cref="Mode"/>'s gate, so <c>/shadow gather 0</c> still disables
    /// everything. Its own dial because the bake A/B has to isolate this term from the cone cull.</summary>
    internal static int Receiver => Shadows.Receiver.V ? 1 : 0;
    /// <summary>This frame's receiver frustum, six planes as <c>ShadowMath.ReceiverPlanes</c> writes
    /// them (already pushed out by the camera epoch's slack). Filled by <c>CasterPass</c> once per frame.</summary>
    internal static readonly float[] Planes = new float[24];

    private static ExtenderLog? _log;
    /// <summary>Dispatches that ran over the dynamic source rather than the full staging -- against
    /// <see cref="Views"/> plus <see cref="LayerDispatches"/>, the period line's witness for S8.</summary>
    internal static long DynDispatches;

    /// <summary>Views dispatched, batches submitted, and the latent sample's tally (survivors and staged
    /// draws of one view per frame, three frames late) -- the gather's half of the caster pass's period
    /// line.</summary>
    internal static long Views, Batches, SampledKept, SampledStaged, Samples;
    /// <summary>S8: the same three over the samples whose dispatch ran on the DYNAMIC source. Kept apart
    /// because the two populations are two orders of magnitude apart -- averaging ~438 staged draws with ~37k
    /// gives a figure that describes neither.</summary>
    internal static long SampledKeptDyn, SampledStagedDyn, SamplesDyn, SampledRxDyn;
    /// <summary>Of the sampled view's staged draws, how many the receiver-frustum test rejected
    /// (the word after the per-batch counts, <c>uCnt[uGroups]</c>).</summary>
    internal static long SampledRx;
    /// <summary>Triangles the sampled views baked at each detail level, per view, over the period: the
    /// bake's answer to <c>MeshLod.Drew</c>. Levels 1 and 2 reading 0 in a dense outdoor area means every
    /// cascade is still baking full detail.</summary>
    internal static readonly long[] SampledTris = new long[MeshLod.Levels];
    /// <summary>Frames whose staged source matched the persistent buffer byte for byte, so nothing was
    /// uploaded -- the number that says the per-frame upload is really gone.</summary>
    internal static long UploadsSkipped, Uploads;

    internal static void Init(ExtenderLog log) => _log = log;

    /// <summary>One per-view dispatch's block, the ONE place those terms are staged -- both backends fill it here,
    /// so neither can hand the cull a term the other does not.</summary>
    internal static void View(ref GatherBlock b, int drawN, int groups, Vector3 focus, Vector3 dir, float reach,
                              float along, bool point, float lodR1, float lodR2, int set, bool receiver, int cull)
    {
        MemoryMarshal.Cast<float, Vector4>(Planes).CopyTo(b.Planes);
        b.Focus = focus; b.Dir = dir; b.Reach = reach; b.Along = along;
        b.LodR1 = lodR1; b.LodR2 = lodR2;
        b.DrawN = drawN; b.ViewN = 0; b.Groups = groups;
        b.Point = point ? 1 : 0; b.Cull = cull; b.Set = set;
        b.Rx = receiver && Receiver != 0 ? 1 : 0;
    }

    /// <summary>...and the layered dispatch's, whose per-view terms come out of the view table instead.</summary>
    internal static void Layer(ref GatherBlock b, int drawN, int viewN, int groups, int cull)
    {
        MemoryMarshal.Cast<float, Vector4>(Planes).CopyTo(b.Planes);
        b.Focus = default; b.Dir = default; b.Reach = 0f; b.Along = 0f;
        b.LodR1 = 0f; b.LodR2 = 0f;
        b.DrawN = drawN; b.ViewN = viewN; b.Groups = groups;
        b.Point = 0; b.Cull = cull; b.Set = 0;
        b.Rx = Receiver != 0 ? 1 : 0;
    }

    internal const uint BindView = SsboSlots.ViewTbl;
    internal const int ViewShift = 18;
    /// <summary>Floats per view-table record, the shader's <c>CxView</c>: mat4 vp + (focus, reach) + (dir, along)
    /// + (receiver, point, set, layer) + (the two detail thresholds, two spare). std430 gives it no padding.</summary>
    internal const int ViewFloats = 32;

    /// <summary>The view table's GLSL declaration, shared by the layered dispatch and both layered depth
    /// stages -- <c>AlloyRenderer.DrawBufDecl</c>'s pattern, so the three cannot drift apart.</summary>
    internal static string ViewTblDecl => ShaderSource.Get("cx_viewtbl.glsl");

    /// <summary>The per-dispatch block's declaration, spliced into both dispatches from the one text, so neither
    /// can drift from <see cref="GatherBlock"/>.</summary>
    internal static string GatherBlockDecl => ShaderSource.Get("cx_gatherblk.glsl");

    /// <summary>Layered dispatches issued, and views they covered -- the period line's halves.</summary>
    internal static long LayerDispatches, LayerViews;

    private static readonly string Slope =
        CasterPass.FaceConeSlope.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The two culls both dispatches share, <c>@SLOPE@</c> unexpanded: the receiver cull
    /// and the point-light face cull. Spliced, then the slope is interpolated over the whole text.</summary>
    private static string ReceiverCullLib => ShaderSource.Get("cx_recvcull.glsl");

    // CX-50: the per-view detail pick, spliced into BOTH dispatches from this one text so they cannot drift.
    // The thresholds are BOUNDS, not pixels: the CPU already divided its two pixel thresholds by the view's
    // world units per shadow texel, so the pick is two compares. lq is the caster's own bound, over its
    // distance from the light for a cube face (a 90deg face spans 2d at depth d, so its texels grow with
    // it). The ladder is MeshArena.Draw's, term for term -- level 2 falls back to level 1 and then to the
    // base run -- so a level the simplifier declined bakes the finer one and never nothing. Two cases need
    // no arithmetic at all and fall out of it: thresholds of 0 (detail off) and a bound exempt from the cull
    // (1e30, a slot with no caster sphere either), both of which keep the base run.
    // A posed skin or dangly reads its caster sphere here (uCast), so it is reach-culled per view; it still
    // bakes its base run, because no coarser run is generated for a skinned or dangly mesh.
    /// <summary>The detail pick's GLSL, with the view's focus, its point flag and its two bound thresholds
    /// spelled as the calling dispatch reads them. Leaves <c>cnt</c>/<c>first</c> declared: the command
    /// write takes them instead of the record's level-0 words.</summary>
    private static string LodPick(string focus, string point, string r1, string r2) => "\n" + $$"""
                uint cnt = uSrc[b], first = uSrc[b + 1u], lvl = 0u;
                if (cnt != 0u && {{r1}} > 0.0)
                {
                    float lq = {{point}} ? s.w / max(length(s.xyz - {{focus}}), 1e-4) : s.w;
                    if (lq < {{r1}})
                    {
                        uint c1 = uSrc[b + {{Cnt1Word}}u], c2 = uSrc[b + {{Cnt2Word}}u];
                        if (lq < {{r2}} && c2 != 0u) { cnt = c2; first = uSrc[b + {{First2Word}}u]; lvl = 2u; }
                        else if (c1 != 0u) { cnt = c1; first = uSrc[b + {{First1Word}}u]; lvl = 1u; }
                    }
                }
                atomicAdd(uCnt[uint(uGroups) + 1u + lvl], cnt / 3u);
        """ + "\n";

    /// <summary>The cull. One thread per staged draw; a survivor appends into its batch's own output region
    /// with an <c>atomicAdd</c>, so every batch keeps its boundaries and only its interior order moves. Both
    /// view tests are the CPU reference's, term for term, and the cone test is conservative BY CONSTRUCTION:
    /// admitting a caster a face cannot see costs a depth draw, rejecting one it can see deletes a shadow.</summary>
    internal static readonly string GatherCs = ("""
        #version 460 core
        layout(local_size_x = 64) in;   // GroupSize

        layout(std430, binding = 9) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 12) readonly buffer CxCastSphere { vec4 uCast[]; };
        layout(std430, binding = 8) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 10) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 11) buffer CxGatherCount         { uint uCnt[]; };

        """ + GatherBlockDecl + """

        """ + ReceiverCullLib + """
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(uDrawN)) return;
            uint b = i * 11u;
            // #305: the set filter, ahead of the cull and NOT gated on uCull -- which half of the split a
            // caster belongs in is what the two arrays' contents mean, not a cull /shadow gather 0 may waive.
            if (uSet == 1 && uSrc[b + 6u] != 0u) return;
            if (uSet == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            // a bound the colour cull exempts (posed skin, dangly, displacing) takes the caster's geometric
            // sphere, pose slack included; only a slot with neither stays never reach-culled
            if (s.w >= 1e29) { vec4 g = uCast[slot]; if (g.w > 0.0 && g.w < 1e29) s = g; }
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uFocus;
                float t = dot(d, uDir);
                bool offAxis, offEnds, rx = uRx != 0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (uPoint != 0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (uReach + r) * (uReach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    // the tangent cone from the light, out to its reach; a light inside the caster keeps it
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * uReach * k; p1 = uFocus + d * (uReach / dist); }
                }
                else
                {
                    vec3 perp = d - t * uDir;
                    offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);
                    offEnds = abs(t) > uAlong + r;
                    p1 = s.xyz + uDir * max(0.0, uAlong - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
            }
        """ + LodPick("uFocus", "uPoint != 0", "uLodR1", "uLodR2") + """
            uint o = uSrc[b + 5u] + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = cnt;                 // count, at this view's detail level
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = first;          // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot);     // baseInstance IS the ownership slot the depth stages read uDraw at
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>The layered cull -- <see cref="GatherCs"/>'s tests, term for term, with the view's parameters
    /// read out of the view table instead of uniforms and one thread per (draw, view) pair. A survivor's
    /// command region is its batch's staged region scaled by the chunk's view count, and its
    /// <c>baseInstance</c> packs the view-table index above the ownership slot, for routing by <c>gl_Layer</c>.</summary>
    internal static readonly string LayerCs = ("""
        #version 460 core
        layout(local_size_x = 64, local_size_y = 1) in;   // GroupSize

        layout(std430, binding = 9) readonly buffer CxCullSphere { vec4 uSph[]; };
        layout(std430, binding = 12) readonly buffer CxCastSphere { vec4 uCast[]; };
        layout(std430, binding = 8) readonly buffer CxGatherSrc  { uint uSrc[]; };
        layout(std430, binding = 10) writeonly buffer CxGatherCmd { uint uCmd[]; };
        layout(std430, binding = 11) buffer CxGatherCount         { uint uCnt[]; };

        """ + ViewTblDecl + """

        """ + GatherBlockDecl + """

        """ + ReceiverCullLib + """
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            uint vi = gl_GlobalInvocationID.y;
            if (i >= uint(uDrawN) || vi >= uint(uViewN)) return;
            uint b = i * 11u;
            vec4 vc = uViewTbl[vi].c;
            int set = int(vc.z);
            if (set == 1 && uSrc[b + 6u] != 0u) return;
            if (set == 2 && uSrc[b + 6u] == 0u) return;
            int slot = int(uSrc[b + 3u]);
            vec4 s = uSph[slot];
            if (s.w >= 1e29) { vec4 g = uCast[slot]; if (g.w > 0.0 && g.w < 1e29) s = g; }
            if (uCull != 0 && s.w < 1e29)
            {
                float r = s.w;
                vec3 d = s.xyz - uViewTbl[vi].a.xyz;
                float reach = uViewTbl[vi].a.w;
                vec3 dir = uViewTbl[vi].b.xyz;
                float t = dot(d, dir);
                bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;
                vec3 p1 = s.xyz; float r0 = r, r1 = r;
                if (vc.y != 0.0)
                {
                    float dSq = dot(d, d);
                    offAxis = dSq > (reach + r) * (reach + r);
                    offEnds = CxFaceCulls(dSq, t, r);
                    float dist = sqrt(dSq);
                    if (dist <= r) rx = false;
                    else { float k = 1.0 / (dist - r); r0 = r * dist * k; r1 = r * reach * k; p1 = uViewTbl[vi].a.xyz + d * (reach / dist); }
                }
                else
                {
                    vec3 perp = d - t * dir;
                    offAxis = dot(perp, perp) > (reach + r) * (reach + r);
                    offEnds = abs(t) > uViewTbl[vi].b.w + r;
                    p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);
                }
                if (offAxis || offEnds) return;
                if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }
            }
        """ + LodPick("uViewTbl[vi].a.xyz", "vc.y != 0.0", "uViewTbl[vi].d.x", "uViewTbl[vi].d.y") + """
            uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);
            uint q = o * 5u;
            uCmd[q] = cnt;                 // count, at this view's detail level
            uCmd[q + 1u] = 1u;             // instanceCount
            uCmd[q + 2u] = first;          // firstIndex, in INDICES
            uCmd[q + 3u] = uSrc[b + 2u];   // baseVertex
            uCmd[q + 4u] = uint(slot) | (vi << 18u);   // baseInstance = ownership slot | view index
        }
        """).Replace("@SLOPE@", Slope);

    /// <summary>Where the view record keeps its first detail threshold, in bytes, measured off the record itself.</summary>
    private static long ViewLodAt()
    {
        CasterViewRec v = default;
        return (byte*)&v.LodR1 - (byte*)&v;
    }

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the layered dispatch, on <see cref="SourceSafe"/>'s
    /// argument: every term whose silent drift deletes a shadow, spelled from the same constants.</summary>
    internal static bool LayerSafe()
        => LayerCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && LayerCs.Contains("offEnds = abs(t) > uViewTbl[vi].b.w + r;")
           && LayerCs.Contains("offAxis = dot(perp, perp) > (reach + r) * (reach + r);")
           && LayerCs.Contains($"binding = {CullPass.BindSphere})") && LayerCs.Contains("CxCullSphere")
           && LayerCs.Contains($"binding = {BindCast})") && LayerCs.Contains("if (s.w >= 1e29) { vec4 g = uCast[slot];")
           && LayerCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && LayerCs.Contains($"uint b = i * {SrcUints}u;")
           && LayerCs.Contains("if (set == 1 && uSrc[b + 6u] != 0u) return;")
           && LayerCs.Contains("if (set == 2 && uSrc[b + 6u] == 0u) return;")
           // CX-50: the detail pick, reading this view's own thresholds out of the view table's d lane --
           // whose float offsets are pinned against the struct DECLARATION both depth stages share -- and
           // the command taking the PICKED run. A pick whose result nothing reads is full detail
           // everywhere, silently, and a lane one float over is another view's thresholds.
           && ViewTblDecl.Contains("struct CxView { mat4 vp; vec4 a; vec4 b; vec4 c; vec4 d; };")
           && ViewFloats == 32 && sizeof(CasterViewRec) == 32 * sizeof(float) && ViewLodAt() == 28 * sizeof(float)
           && LayerCs.Contains("float lq = vc.y != 0.0 ? s.w / max(length(s.xyz - uViewTbl[vi].a.xyz), 1e-4) : s.w;")
           && LayerCs.Contains("if (lq < uViewTbl[vi].d.x)")
           && LayerCs.Contains("if (lq < uViewTbl[vi].d.y && c2 != 0u) { cnt = c2; first = uSrc[b + 10u]; lvl = 2u; }")
           && LayerCs.Contains("else if (c1 != 0u) { cnt = c1; first = uSrc[b + 8u]; lvl = 1u; }")
           && LayerCs.Contains("atomicAdd(uCnt[uint(uGroups) + 1u + lvl], cnt / 3u);")
           && LayerCs.Contains("uint c1 = uSrc[b + 7u], c2 = uSrc[b + 9u];")
           && LayerCs.Contains("uCmd[q] = cnt;") && LayerCs.Contains("uCmd[q + 2u] = first;")
           // the pack: the shift is ViewShift on both sides, the batch region scales by the view count, and
           // the slot rides the low bits the depth stage masks back out
           && LayerCs.Contains($"uCmd[q + 4u] = uint(slot) | (vi << {ViewShift}u);")
           && LayerCs.Contains("uint o = uSrc[b + 5u] * uint(uViewN) + atomicAdd(uCnt[uSrc[b + 4u]], 1u);")
           && LayerCs.Contains($"binding = {BindView})")
           // #488: the receiver cull is per-VIEW flagged (c.x), inside the uCull gate, and conservative in
           // the two-spheres form; a rejection tallies past the batch counts, never into one
           && LayerCs.Contains("bool offAxis, offEnds, rx = uRx != 0 && vc.x != 0.0;")
           && LayerCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && LayerCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && LayerCs.Contains("if (dist <= r) rx = false;")
           && LayerCs.Contains("p1 = s.xyz + dir * max(0.0, uViewTbl[vi].b.w - t);")
           && LayerCs.Contains($"binding = {BindSrc})") && LayerCs.Contains($"binding = {BindCmd})")
           && LayerCs.Contains($"binding = {BindCount})");

    /// <summary>What <c>CasterPass.SelfCheck</c> pins about the dispatch: the cull it runs is the CPU
    /// reference's, over the SHARED sphere buffer, with the shared exemption, conservative in the direction
    /// that costs a draw. Text rather than arithmetic because GLSL cannot be run headless, but every term
    /// that would silently delete a shadow if it drifted is named here.</summary>
    internal static bool SourceSafe()
        => GatherCs.Contains("if (t < -r) return true;")
           && GatherCs.Contains($"float lim = t * {Slope} + 2.0 * r;")
           && GatherCs.Contains("return lim < 0.0 || dSq - t * t > lim * lim;")
           && GatherCs.Contains("offEnds = abs(t) > uAlong + r;")
           && GatherCs.Contains("offAxis = dot(perp, perp) > (uReach + r) * (uReach + r);")
           // #303: the shared bounds, at the colour cull's own binding, with its own exemption value.
           && GatherCs.Contains($"binding = {CullPass.BindSphere})") && GatherCs.Contains("CxCullSphere")
           && GatherCs.Contains($"binding = {BindCast})") && GatherCs.Contains("if (s.w >= 1e29) { vec4 g = uCast[slot];")
           && GatherCs.Contains("if (uCull != 0 && s.w < 1e29)")
           && GatherCs.Contains("uCmd[q + 4u] = uint(slot);")
           // #488: same receiver cull, flagged per dispatch
           && GatherCs.Contains("bool offAxis, offEnds, rx = uRx != 0;")
           && GatherCs.Contains("if (dot(pl.xyz, p0) + pl.w < -r0 && dot(pl.xyz, p1) + pl.w < -r1) return true;")
           && GatherCs.Contains("if (rx && CxShadowMisses(s.xyz, r0, p1, r1)) { atomicAdd(uCnt[uGroups], 1u); return; }")
           && GatherCs.Contains("if (dist <= r) rx = false;")
           && GatherCs.Contains("p1 = s.xyz + uDir * max(0.0, uAlong - t);")
           // #305: the set filter reads the staged dynamic flag at the record stride, in BOTH directions --
           // a filter that lost either arm bakes the same caster into both arrays, and min() then hides it.
           // CX-50: ...and the stride is the one the staging writes, spelled as a LITERAL in the GLSL and
           // interpolated here, so a record that grows on one side only fails this.
           && GatherCs.Contains("uint b = i * 11u;") && SrcUints == 11
           && GatherCs.Contains("if (uSet == 1 && uSrc[b + 6u] != 0u) return;")
           && GatherCs.Contains("if (uSet == 2 && uSrc[b + 6u] == 0u) return;")
           // CX-50: the detail pick, and the command taking the PICKED run rather than the record's level-0
           // words -- a pick whose result nothing reads is full detail everywhere, silently. Every word index
           // is a LITERAL in the GLSL against the const the staging writes at, so the two cannot renumber
           // apart; the ladder is MeshArena.Draw's (level 2 where it exists, else level 1, else the base
           // run); and an entry turned OFF (count 0) stays off at every level.
           && Cnt1Word == 7 && First1Word == 8 && Cnt2Word == 9 && First2Word == 10
           && GatherCs.Contains("float lq = uPoint != 0 ? s.w / max(length(s.xyz - uFocus), 1e-4) : s.w;")
           && GatherCs.Contains("if (lq < uLodR1)")
           && GatherCs.Contains("if (lq < uLodR2 && c2 != 0u) { cnt = c2; first = uSrc[b + 10u]; lvl = 2u; }")
           && GatherCs.Contains("else if (c1 != 0u) { cnt = c1; first = uSrc[b + 8u]; lvl = 1u; }")
           // ...and the per-level tally sits past the batch counts and the receiver word, where Sample reads it
           && GatherCs.Contains($"atomicAdd(uCnt[uint(uGroups) + {TriWord(0, 0)}u + lvl], cnt / 3u);")
           && CountExtra == 4 && MeshLod.Levels == 3
           && GatherCs.Contains("uint c1 = uSrc[b + 7u], c2 = uSrc[b + 9u];")
           && GatherCs.Contains("if (cnt != 0u && uLodR1 > 0.0)")
           && GatherCs.Contains("uCmd[q] = cnt;") && GatherCs.Contains("uCmd[q + 2u] = first;")
           && GatherCs.Contains($"binding = {BindSrc})") && GatherCs.Contains($"binding = {BindCmd})")
           && GatherCs.Contains($"binding = {BindCount})");
}
