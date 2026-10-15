using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The selection GLOW, a port of <c>Scene::DoHighlightGob</c> @0x470fc0 over the scene database --
/// the "glow" highlight mode, and whatever the outline pass cannot cover in "outline" mode. Per highlighted
/// root and per propagating attachment, every enabled non-skin mesh part emits three textured quads through
/// its node's box centre, scaled by the pulse. Additive over the finished frame, depth-tested, unfogged.</summary>
internal static unsafe class HighlightGlow
{
    /// <summary>ponytail: a fixed staging ceiling (1 MiB) -- a Tab-highlight-all in a crowded area is the
    /// worst case; past it the rest is NOT DRAWN and logged once. Grow it if the log ever says so.</summary>
    private const int MaxQuads = 16384;
    /// <summary>A shared point (<see cref="SsboSlots"/>): rebound before every draw.</summary>
    private const uint BindGlow = SsboSlots.Glow;
    /// <summary>A gob whose <c>+0x108</c> alpha is under this dims its highlight colour by alpha / this
    /// (the 0.4f at 0x12f1ba8).</summary>
    private const float FadeKnee = 0.4f;
    /// <summary>Each quad's fixed coordinate is pulled this far along the eye direction (the -0.001f at
    /// 0x12f1b78), in the part's local frame as the engine does it.</summary>
    private const float EyeBias = -0.001f;

    private static readonly GlowRec[] _stage = new GlowRec[MaxQuads];
    private static int _quads, _dropped;
    /// <summary>Everything the GL context owns, plus the glow texture held against it: context loss replaces it.</summary>
    private sealed class GlowGlState
    {
        public uint Prog, Buf, Vao;
        public int Cap, LocView, LocProj, LocDepth, LocInvView, LocTex;
        public string TexName = "";
        public TexId Tex;
    }

    private static GlowGlState _gl = new();
    private static bool _failed, _logged, _texLogged, _capLogged, _off;
    private static ExtenderLog? _log;
    private static float* _amp, _freq, _intensity, _size, _deltaT;
    /// <summary>The pulse phase the engine kept at <c>Scene+0xb0</c>; advanced only while something is
    /// highlighted, as there.</summary>
    private static float _phase;
    private static float _pulse, _alphaScale;
    private static Vector3 _eye;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _amp = (float*)Native.Sym(SymbolMap.GobHighlightAmplitude);
        _freq = (float*)Native.Sym(SymbolMap.GobHighlightFrequency);
        _intensity = (float*)Native.Sym(SymbolMap.GobHighlightIntensity);
        _size = (float*)Native.Sym(SymbolMap.GobHighlightSize);
        _deltaT = (float*)Native.Sym(SymbolMap.DeltaT);
        _off = _amp == null || _freq == null || _intensity == null || _size == null || _deltaT == null;
        if (_off)
            log.Always("HighlightGlow: a Scene::m_fGobHighlight* static or DeltaT unresolved -- the glow "
                     + "highlight is NOT DRAWN (nothing else draws it since CX-82)");
    }

    /// <summary>Once per frame that has a highlight, before any <see cref="Stage"/>: the pulse, the eye, the
    /// scene's glow texture (<c>Scene::SetHighlightGobTexture</c>). The engine's own frame-level terms, read
    /// live -- <c>CNWCArea::LoadArea</c> rewrites the four statics on every area load.</summary>
    internal static void Begin(DbId scene, Vector3 eye)
    {
        _quads = 0;
        if (_off) return;
        _phase += 2f * *_deltaT * MathF.PI * *_freq;
        while (_phase > 2f * MathF.PI) _phase -= 2f * MathF.PI;
        _pulse = Pulse(_phase, *_amp, *_size);
        _alphaScale = *_intensity;
        _eye = eye;
        string name = SceneFeed.HighlightTexture(scene);
        if (name == _gl.TexName) return;
        if (!_gl.Tex.IsNull) TexRegistry.Release(_gl.Tex);
        _gl.TexName = name;
        _gl.Tex = name.Length == 0 ? default : TexRegistry.Acquire(name);
    }

    /// <summary>The box scale about its centre: <c>((sin * 0.5 + 0.5) * (0.25 * amplitude) + 1) * size</c>,
    /// disassembled from @0x4710be..0x471101.</summary>
    internal static float Pulse(float phase, float amplitude, float size)
        => ((MathF.Sin(phase) * 0.5f + 0.5f) * (0.25f * amplitude) + 1f) * size;

    /// <summary>Stage one highlighted root, its alpha fade folded into the colour: its own tree, then every
    /// attached object that propagates the highlight (<paramref name="kids"/>), all in the root's colour.</summary>
    internal static void Stage(DbId root, float* colour, IntPtr* kids, int nKids)
    {
        if (_off || !SceneDb.Objects.Valid(root)) return;
        var rgb = new Vector3(Math.Clamp(colour[0], 0f, 1f), Math.Clamp(colour[1], 0f, 1f), Math.Clamp(colour[2], 0f, 1f));
        float ga = SceneDb.ObjTint[root.Index].W;
        if (ga < FadeKnee) rgb *= ga / FadeKnee;
        Tree(root, rgb);
        for (int i = 0; i < nKids; i++) Tree(new DbId((uint)kids[i]), rgb);
    }

    /// <summary><c>WritePartHighlightVertices</c>'s recursion over the flattened pre-order range: a disabled
    /// part prunes its subtree, and a mesh part that is not a skin emits its node's box.</summary>
    private static void Tree(DbId obj, Vector3 rgb)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
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
            if (_quads + 3 > MaxQuads) { _dropped++; continue; }
            if (!Matrix4x4.Decompose(SceneDb.PartWorld[p], out Vector3 sc, out Quaternion rot, out Vector3 pos)) continue;
            _quads += Quads(mesh.BMin, mesh.BMax, pos, rot, sc.X, _pulse, _eye, _alphaScale, rgb, _stage.AsSpan(_quads));
        }
    }

    /// <summary>Is <paramref name="p"/> enabled and every ancestor too? The engine's recursion stops at a
    /// disabled part, so its subtree draws nothing.</summary>
    private static bool SubtreeEnabled(int first, int p)
    {
        for (int i = p; i >= first; i = SceneDb.PartParent[i])
        {
            if (!SceneDb.PartEnabled[i]) return false;
            if (SceneDb.PartParent[i] < 0) break;
        }
        return true;
    }

    /// <summary>One mesh part's three quads, in the engine's order: the x plane spans (y, z), the y plane
    /// (x, z), the z plane (x, y). Alpha is <c>dot(plane normal, eye direction)^2 * intensity</c>, so a quad
    /// seen edge-on vanishes. A zero-area quad is skipped. Returns the quads written.</summary>
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
            float hu = ((float*)&half)[u], hv = ((float*)&half)[v];
            if (hu == 0f || hv == 0f) continue;
            Vector3 centre = mid, axisU = default, axisV = default, n = default;
            ((float*)&centre)[a] = ((float*)&biased)[a];
            ((float*)&axisU)[u] = hu;
            ((float*)&axisV)[v] = hv;
            ((float*)&n)[a] = 1f;
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

    /// <summary>The pass: one instanced quad per staged record, additive, over the FINISHED display-space
    /// frame, depth-tested in the fragment stage against the frame's own depth. Hands the state back the way
    /// the composite expects it.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* display)
    {
        if (_dropped > 0 && !_capLogged)
        {
            _capLogged = true;
            _log?.Always($"HighlightGlow: {_dropped} mesh part(s) past the {MaxQuads}-quad staging cap -- their glow "
                       + "is NOT DRAWN (raise MaxQuads)");
        }
        _dropped = 0;
        if (_quads == 0 || PostChain.DepthTex == 0 || !EnsureProgram()) return;
        if (!TexRegistry.TryDrawable(_gl.Tex, out uint tid, out _))
        {
            if (!_texLogged && TexRegistry.Final(_gl.Tex))
            {
                _texLogged = true;
                _log?.Always($"HighlightGlow: the scene's highlight texture '{_gl.TexName}' is "
                           + (_gl.TexName.Length == 0 ? "unset" : "unreadable") + " -- the glow is NOT DRAWN");
            }
            return;
        }
        if (_quads > _gl.Cap)
        {
            _gl.Cap = Math.Max(_quads, 1024);
            Gl.NamedBufferData(_gl.Buf, (IntPtr)(_gl.Cap * sizeof(GlowRec)), null, Gl.DYNAMIC_DRAW);
        }
        fixed (GlowRec* s = _stage)
            Gl.NamedBufferSubData(_gl.Buf, IntPtr.Zero, (IntPtr)(_quads * sizeof(GlowRec)), s);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindGlow, _gl.Buf);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.HiliteDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, PostChain.DepthTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, tid);
        Gl.UseProgram(_gl.Prog);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.Uniform1i(_gl.LocDepth, (int)TextureSlots.HiliteDepth);
        Gl.Uniform1i(_gl.LocTex, 0);
        Gl.Uniform2f(_gl.LocInvView, 1f / MathF.Max(display[2], 1), 1f / MathF.Max(display[3], 1));
        new PassState(depth: false, depthWrite: false, blend: true,
                      blendSrc: AlloyRenderer.GlOne, blendDst: AlloyRenderer.GlOne).Apply();
        Gl.BindVertexArray(_gl.Vao);
        Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, _quads, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);
        if (_logged) return;
        _logged = true;
        _log?.Info($"HighlightGlow: LIVE -- {_quads} glow quad(s) this frame, three per visible mesh part of each "
                 + "highlighted root, from the scene database");
    }

    private const uint GlTriangleStrip = 0x0005;   // not-an-offset: GL primitive-mode enum

    private static bool EnsureProgram()
    {
        if (_gl.Prog != 0) return true;
        if (_failed || Gl.DrawArraysInstancedBaseInstance == null || Gl.CreateBuffers == null) return false;
        _gl.Prog = GlProgram.Graphics(ShaderSource.Get("cx_hiliteglow_v.glsl"),
                                   ShaderSource.Get("cx_hiliteglow_f.glsl"), _log, "AlloyRenderer highlight glow");
        if (_gl.Prog == 0)
        {
            _failed = true;
            _log?.Always("HighlightGlow: program build failed -- the glow highlight is NOT DRAWN this session");
            return false;
        }
        uint b; Gl.CreateBuffers(1, &b); _gl.Buf = b; _gl.Cap = 0;
        uint v; Gl.CreateVertexArrays(1, &v); _gl.Vao = v;
        _gl.LocView = GlProgram.Loc(_gl.Prog, "uView");
        _gl.LocProj = GlProgram.Loc(_gl.Prog, "uProj");
        _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uSceneDepth");
        _gl.LocInvView = GlProgram.Loc(_gl.Prog, "uInvView");
        _gl.LocTex = GlProgram.Loc(_gl.Prog, "uTex");
        return true;
    }

    /// <summary>GL names die with the context; the stage array does not.</summary>
    internal static void ContextLost() { _gl = new GlowGlState(); _quads = 0; }

    /// <summary>Off-device: the pulse range and the quad geometry, which fail silently on screen (a glow in
    /// the wrong place, or none).</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "HighlightGlow.SelfCheck");
        // LoadArea's values: amplitude 0.5, size 2.5 -> the box swings 2.5x .. 2.8125x
        check.Expect(MathF.Abs(Pulse(-MathF.PI / 2f, 0.5f, 2.5f) - 2.5f) < 1e-5f
                     && MathF.Abs(Pulse(MathF.PI / 2f, 0.5f, 2.5f) - 2.8125f) < 1e-5f, "the pulse swings size .. size * (1 + amplitude / 4)");

        Span<GlowRec> d = stackalloc GlowRec[3];
        check.Expect(sizeof(GlowRec) == 64, "a glow quad is four vec4s");
        // a unit box centred on (0, 0, 1) at the origin, unrotated, eye straight above
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

        string f = ShaderSource.Get("cx_hiliteglow_f.glsl");
        check.Expect(f.Contains("if (-vViewZ > CxSceneViewZ(gl_FragCoord.xy * uInvView)) discard;"),
                     "the quads are depth-tested against the scene, as the engine drew them");
        log.Info(check.Ok ? "HighlightGlow.SelfCheck: passed" : "HighlightGlow.SelfCheck: FAILED");
    }
}
