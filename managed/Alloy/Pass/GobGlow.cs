using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>One glow quad as the vertex stage reads it: world centre (alpha in w), the two half-axes, and rgb.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GlowRec
{
    public Vector4 Centre, AxisU, AxisV, Colour;
}

/// <summary>The vanilla "highlight gob" glow, a port of <c>Scene::DoHighlightGob</c> @0x470fc0 over the scene database:
/// every highlight the outline does not cover. Per highlighted root and per propagating attachment, every enabled
/// non-skin mesh part emits three textured quads through its node's box centre, scaled by the pulse.</summary>
internal static unsafe class GobGlow
{
    /// <summary>ponytail: a fixed staging ceiling (1 MiB) -- a Tab-highlight-all in a crowded area is the worst case;
    /// past it the rest is NOT DRAWN and logged once. Grow it if the log ever says so.</summary>
    internal const int MaxQuads = 16384;

    /// <summary>A gob whose alpha is under this dims its highlight colour by alpha / this (the 0.4f at 0x12f1ba8).</summary>
    private const float FadeKnee = 0.4f;

    /// <summary>Each quad's fixed coordinate is pulled this far along the eye direction (the -0.001f at 0x12f1b78),
    /// in the part's local frame as the engine does it.</summary>
    private const float EyeBias = -0.001f;

    /// <summary>The staging, the engine statics it reads, and the glow texture it holds.</summary>
    private sealed class GobGlowState
    {
        public readonly GlowRec[] Stage = new GlowRec[MaxQuads];
        public int Quads, Dropped;
        public ExtenderLog? Log;
        public float* Amp, Freq, Intensity, Size, DeltaT;
        public bool Off = true, CapSaid;
        /// <summary>The pulse phase the engine kept at <c>Scene+0xb0</c>; advanced only while something glows, as there.</summary>
        public float Phase, Pulse, AlphaScale;
        public Vector3 Eye;
        public string TexName = "";
        public TexId Tex;
    }

    private static readonly GobGlowState _s = new();

    internal static void Attach(ExtenderLog log)
    {
        GobGlowState s = _s;
        s.Log = log;
        s.Amp = (float*)Native.Sym(SymbolMap.GobHighlightAmplitude);
        s.Freq = (float*)Native.Sym(SymbolMap.GobHighlightFrequency);
        s.Intensity = (float*)Native.Sym(SymbolMap.GobHighlightIntensity);
        s.Size = (float*)Native.Sym(SymbolMap.GobHighlightSize);
        s.DeltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        s.Off = s.Amp == null || s.Freq == null || s.Intensity == null || s.Size == null || s.DeltaT == null;
        if (s.Off)
            log.Always("GobGlow: a Scene::m_fGobHighlight* static or DeltaT unresolved -- the vanilla highlight glow is NOT DRAWN, "
                     + "so a highlight the outline does not cover shows nothing");
    }

    /// <summary>Once per frame that has a glow, before any <see cref="Stage"/>: the pulse, the eye, and the scene's glow
    /// texture (<c>Scene::SetHighlightGobTexture</c>), read live -- <c>CNWCArea::LoadArea</c> rewrites the statics.</summary>
    internal static void Begin(DbId scene, Vector3 eye)
    {
        GobGlowState s = _s;
        s.Quads = 0;
        if (s.Off) return;
        s.Phase += 2f * *s.DeltaT * MathF.PI * *s.Freq;
        while (s.Phase > 2f * MathF.PI) s.Phase -= 2f * MathF.PI;
        s.Pulse = Pulse(s.Phase, *s.Amp, *s.Size);
        s.AlphaScale = *s.Intensity;
        s.Eye = eye;
        string name = SceneFeed.HighlightTexture(scene);
        if (name == s.TexName) return;
        if (!s.Tex.IsNull) TexRegistry.Release(s.Tex);
        s.TexName = name;
        s.Tex = name.Length == 0 ? default : TexRegistry.Acquire(name);
    }

    /// <summary>The box scale about its centre: <c>((sin * 0.5 + 0.5) * (0.25 * amplitude) + 1) * size</c>,
    /// disassembled from @0x4710be..0x471101.</summary>
    internal static float Pulse(float phase, float amplitude, float size)
        => ((MathF.Sin(phase) * 0.5f + 0.5f) * (0.25f * amplitude) + 1f) * size;

    /// <summary>Stage one highlighted root, its alpha fade folded into the colour: its own tree, then every attached
    /// object that propagates the highlight (<paramref name="kids"/>), all in the root's colour.</summary>
    internal static void Stage(DbId root, Vector3 colour, IntPtr* kids, int nKids)
    {
        if (_s.Off || !SceneDb.Objects.Valid(root)) return;
        Vector3 rgb = Vector3.Clamp(colour, Vector3.Zero, Vector3.One);
        float ga = SceneDb.ObjTint[root.Index].W;
        if (ga < FadeKnee) rgb *= ga / FadeKnee;
        Tree(root, rgb);
        for (int i = 0; i < nKids; i++) Tree(new DbId((uint)kids[i]), rgb);
    }

    /// <summary><c>WritePartHighlightVertices</c>'s recursion over the flattened pre-order range: a disabled part prunes
    /// its subtree, and a mesh part that is not a skin emits its node's box.</summary>
    private static void Tree(DbId obj, Vector3 rgb)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        GobGlowState s = _s;
        int first = SceneDb.ObjPartFirst[obj.Index], n = SceneDb.ObjPartCount[obj.Index];
        for (int p = first; p < first + n; p++)
        {
            PartKind k = SceneDb.PartKindOf[p];
            if (k is not (PartKind.Mesh or PartKind.Dangly or PartKind.AnimMesh or PartKind.Aabb)) continue;
            if (SceneDb.PartMerged[p] || !SubtreeEnabled(first, p)) continue;
            ModelDef? m = SceneDb.PartModel[p];
            int node = SceneDb.PartNode[p];
            MeshDef? mesh = m != null && (uint)node < (uint)m.Nodes.Length ? m.Nodes[node].Mesh : null;
            if (mesh == null) continue;
            if (s.Quads + 3 > MaxQuads) { s.Dropped++; continue; }
            if (!Matrix4x4.Decompose(SceneDb.PartWorld[p], out Vector3 sc, out Quaternion rot, out Vector3 pos)) continue;
            s.Quads += Quads(mesh.BMin, mesh.BMax, pos, rot, sc.X, s.Pulse, s.Eye, s.AlphaScale, rgb, s.Stage.AsSpan(s.Quads));
        }
    }

    /// <summary>Is <paramref name="p"/> enabled and every ancestor too? The engine's recursion stops at a disabled part,
    /// so its subtree draws nothing.</summary>
    private static bool SubtreeEnabled(int first, int p)
    {
        for (int i = p; i >= first; i = SceneDb.PartParent[i])
        {
            if (!SceneDb.PartEnabled[i]) return false;
            if (SceneDb.PartParent[i] < 0) break;
        }
        return true;
    }

    /// <summary>One component of <paramref name="v"/> by axis (0 = X, 1 = Y, 2 = Z).</summary>
    private static ref float Axis(ref Vector3 v, int a)
    {
        switch (a)
        {
            case 0: return ref v.X;
            case 1: return ref v.Y;
            default: return ref v.Z;
        }
    }

    /// <summary>One mesh part's three quads, in the engine's order: the x plane spans (y, z), the y plane (x, z), the z
    /// plane (x, y). Alpha is <c>dot(plane normal, eye direction)^2 * intensity</c>, so a quad seen edge-on vanishes.
    /// A zero-area quad is skipped. Returns the quads written.</summary>
    internal static int Quads(Vector3 bmin, Vector3 bmax, Vector3 pos, Quaternion rot, float scale, float pulse,
                              Vector3 eye, float intensity, Vector3 rgb, Span<GlowRec> d)
    {
        Vector3 mid = (bmin + bmax) * (0.5f * scale);
        Vector3 half = (bmax - bmin) * (0.5f * pulse * scale);
        Vector3 toEye = eye - pos;
        float len2 = toEye.LengthSquared();
        // normalize() @0x5b37d0: a zero vector comes back as (1, 0, 0)
        toEye = len2 == 0f ? Vector3.UnitX : toEye * (1f / MathF.Sqrt(len2));
        Vector3 biased = mid + toEye * EyeBias;
        int written = 0;
        for (int a = 0; a < 3; a++)
        {
            int u = a == 0 ? 1 : 0, v = a == 2 ? 1 : 2;
            float hu = Axis(ref half, u), hv = Axis(ref half, v);
            if (hu == 0f || hv == 0f) continue;
            Vector3 centre = mid, axisU = default, axisV = default, n = default;
            Axis(ref centre, a) = Axis(ref biased, a);
            Axis(ref axisU, u) = hu;
            Axis(ref axisV, v) = hv;
            Axis(ref n, a) = 1f;
            float f = Vector3.Dot(Vector3.Transform(n, rot), toEye);
            Vector3 c = pos + Vector3.Transform(centre, rot), wu = Vector3.Transform(axisU, rot),
                    wv = Vector3.Transform(axisV, rot);
            ref GlowRec r = ref d[written];
            r.Centre = new Vector4(c, Math.Clamp(f * f * intensity, 0f, 1f));
            r.AxisU = new Vector4(wu, 0f);
            r.AxisV = new Vector4(wv, 0f);
            r.Colour = new Vector4(rgb, 0f);
            written++;
        }
        return written;
    }

    /// <summary>This frame's staged quads; says once when the staging cap dropped parts.</summary>
    internal static ReadOnlySpan<GlowRec> Staged()
    {
        GobGlowState s = _s;
        if (s.Dropped > 0 && !s.CapSaid)
        {
            s.CapSaid = true;
            s.Log?.Always($"GobGlow: {s.Dropped} mesh part(s) past the {MaxQuads}-quad staging cap -- their glow is NOT DRAWN (raise MaxQuads)");
        }
        s.Dropped = 0;
        return s.Stage.AsSpan(0, s.Quads);
    }

    /// <summary>The scene's glow texture's GL-side name once it can draw, 0 before; <paramref name="final"/> when it
    /// never will (unset, or failed to load).</summary>
    internal static uint TexName(out bool final, out string resref)
    {
        GobGlowState s = _s;
        resref = s.TexName;
        final = TexRegistry.Final(s.Tex);
        return TexRegistry.TryDrawable(s.Tex, out uint name, out _) ? name : 0u;
    }

    /// <summary>Off-device: the pulse range and the quad geometry, which fail silently on screen (a glow in the wrong
    /// place, or none). Oracle: hand-worked boxes under known transforms.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GobGlow.SelfCheck");
        // LoadArea's values: amplitude 0.5, size 2.5 -> the box swings 2.5x .. 2.8125x
        check.Expect(MathF.Abs(Pulse(-MathF.PI / 2f, 0.5f, 2.5f) - 2.5f) < 1e-5f
                     && MathF.Abs(Pulse(MathF.PI / 2f, 0.5f, 2.5f) - 2.8125f) < 1e-5f, "the pulse swings size .. size * (1 + amplitude / 4)");

        Span<GlowRec> d = stackalloc GlowRec[3];
        // a box centred on (0, 0, 1) at the origin, unrotated, eye straight above
        int q = Quads(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 2f), default, Quaternion.Identity, 1f, 2f,
                      new Vector3(0f, 0f, 10f), 0.3f, Vector3.One, d);
        check.Expect(q == 3, "a solid box gives three quads");
        check.Expect(MathF.Abs(d[0].Centre.X) < 1e-3f && MathF.Abs(d[0].Centre.Z - 1f) < 1e-5f && d[0].AxisU.Y == 2f && d[0].AxisV.Z == 2f,
                     "the x plane sits at the box centre and spans y and z, pulse-scaled");
        check.Expect(d[0].Centre.W == 0f && d[1].Centre.W == 0f && MathF.Abs(d[2].Centre.W - 0.3f) < 1e-5f,
                     "only the plane facing the eye glows: alpha = dot^2 * intensity");
        check.Expect(MathF.Abs(d[2].Centre.Z - (1f - 0.001f)) < 1e-6f, "the fixed coordinate takes the engine's 0.001 eye bias");

        // the gob's transform moves and turns the box: yaw 90 degrees, 2 units along x, scale 2
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        q = Quads(new Vector3(1f, 0f, 0f), new Vector3(3f, 2f, 2f), new Vector3(2f, 0f, 0f), yaw, 2f, 1f,
                  new Vector3(2f, 0f, 50f), 1f, Vector3.One, d);
        // local centre (2, 1, 1) * 2 = (4, 2, 2); yawed -> (-2, 4, 2); plus the position -> (0, 4, 2)
        check.Expect(q == 3 && MathF.Abs(d[1].Centre.X) < 1e-3f && MathF.Abs(d[1].Centre.Y - 4f) < 1e-2f
                     && MathF.Abs(d[1].Centre.Z - 2f) < 1e-2f, "the box centre is composed through scale, rotation, position");
        // the x plane's U half-axis is local y (1 * scale 2), which the yaw turns onto -x
        check.Expect(MathF.Abs(d[0].AxisU.X + 2f) < 1e-4f && MathF.Abs(d[0].AxisU.Y) < 1e-4f, "the half-axes turn with the part");

        check.Expect(Quads(new Vector3(-1f, -1f, 0f), new Vector3(1f, 1f, 0f), default, Quaternion.Identity, 1f, 1f,
                           new Vector3(0f, 0f, 5f), 1f, Vector3.One, d) == 1, "a flat box keeps only its own plane");

        string f = ShaderSource.Get("cx_gobglow_f.glsl");
        check.Expect(f.Contains("if (-vViewZ > CxSceneViewZ("), "the quads are depth-tested against the scene, as the engine drew them");
        check.Expect(ShaderSource.Get("cx_gobglow_v.glsl").Contains($"binding = {SsboSlots.Glow})"),
                     "the vertex stage reads the quads on the point the pass binds them");
        log.Info(check.Ok ? "GobGlow.SelfCheck: passed" : "GobGlow.SelfCheck: FAILED");
    }
}
