using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The deferred path's SHADOW pass (ADR 0029): one compute dispatch between the cutout pass and
/// <see cref="DeferredLights"/> that picks, per PIXEL, the four shadow-casting lights reaching it most
/// strongly and writes each one's cube visibility once into the record this class owns (<c>cx_shrec.glsl</c>'s
/// format, at <c>shadow.deferredres</c>). A link failure stands the whole deferred path down.</summary>
internal static unsafe class DeferredShadows
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_defshadow_cs";

    /// <summary>Invocations per group per axis, matching the stage's own <c>local_size</c>.</summary>
    private const int Group = 8;

    /// <summary>Bytes a record texel the two targets cost together, for the allocation line.</summary>
    private const int BytesPerTexel = 4 + 8;

    /// <summary>Lights one record texel can hold -- <c>cx_shrec.glsl</c>'s <c>CX_SH_MAX</c>, which is the
    /// definition; this is the ceiling <c>shadow.perpixel</c> is declared against. Four packed slot bytes in one
    /// word and four visibility channels in one RGBA8, so widening it is a format change.</summary>
    internal const int RecordLights = 4;

    /// <summary>How far above the best DROPPED light's score a record lane must sit to keep its shadow in full
    /// -- <c>cx_shrec.glsl</c>'s <c>CX_SH_FADE</c>, which is the definition; this is the mirror the continuity
    /// self-check sweeps. An octave: a lane at the budget's edge is fully dissolved, so the light that falls out
    /// of the record had already faded to unshadowed and the budget's membership step is invisible.</summary>
    internal const float RecordFade = 2f;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredShadows");

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so neither the
    /// program nor a record name from the dead context survives as a field nobody remembered to zero.</summary>
    private sealed class ShadowRecordState
    {
        public uint Prog;
        public int LocRect, LocInvVp, LocProj, LocCubes, LocShSize, LocShRes;
        public uint TexVis, TexKey;
        public int W, H;
    }

    private static ShadowRecordState _gl = new();
    /// <summary>Dispatches this period, and the record size the last one ran at, for the <c>[alloy]</c> line.</summary>
    private static int _runs;

    /// <summary>The record is allocated and was written this frame: the divisor the reader must upsample by,
    /// 0 when there is no record and every shadow is the walk's own hardware tap. Read by
    /// <see cref="DeferredLights"/> after <see cref="Run"/>.</summary>
    internal static int Res { get; private set; }

    /// <summary>The record's size in texels -- the reader's own bounds test.</summary>
    internal static int Width => _gl.W;
    internal static int Height => _gl.H;
    internal static uint TexVis => _gl.TexVis;
    internal static uint TexKey => _gl.TexKey;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The pass. <paramref name="vp"/> is the JITTERED view-projection the prepass rasterised with;
    /// <paramref name="viewport"/> the render viewport. Sets <see cref="Res"/> for the reader.</summary>
    internal static void Run(Matrix4x4* vp, Matrix4x4* proj, int* viewport, float near, float far)
    {
        Res = 0;
        if (_gl.Prog == 0 || viewport[2] <= 0 || viewport[3] <= 0) return;
        if (PostChain.DepthTex == 0)
        { _down.Refuse(_log, "the owned depth attachment is not armed", "point-light shadows take one hardware tap this frame"); return; }
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 invVp)) return;
        int div = Shadows.DeferredRes.V;
        int w = (viewport[2] + div - 1) / div, h = (viewport[3] + div - 1) / div;
        if (!Ensure(w, h)) return;

        Gl.UseProgram(_gl.Prog);
        // The light SSBO, the cluster table and the mover spheres, and this pass's own CxPass block plus the
        // cube arrays -- the same binds a lit draw and the deferred walk get, so the ranking this pass does
        // cannot differ from the walk's by an input.
        AlloyLights.BindForDraw();
        AlloyRenderer.BindPass(_gl.LocCubes, viewport, near, far);
        Gl.Uniform4iv(_gl.LocRect, 1, viewport);
        Gl.UniformMatrix4fv(_gl.LocInvVp, 1, 0, (float*)&invVp);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.Uniform2i(_gl.LocShSize, w, h);
        Gl.Uniform1i(_gl.LocShRes, div);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, DeferredLights.BindStats, DeferredLights.StatsBuf);

        GBuffer.BindForCompute();
        Gl.BindImageTexture(0, _gl.TexVis, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(1, _gl.TexKey, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG32UI);

        Gl.DispatchCompute((uint)(w + Group - 1) / Group, (uint)(h + Group - 1) / Group, 1);
        // the record is read back by the next dispatch as a TEXTURE FETCH, which the image-store bit alone
        // does not order
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);

        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA8);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG32UI);
        GBuffer.UnbindCompute();
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.UseProgram(0);
        _runs++;
        Res = div;
    }

    /// <summary>(Re)allocate the record at <paramref name="w"/>x<paramref name="h"/> texels. False = the record
    /// cannot be had on this context, said once and loudly: the frame then takes one hardware tap per light.</summary>
    private static bool Ensure(int w, int h)
    {
        if (_gl.TexVis != 0 && _gl.W == w && _gl.H == h) return true;
        if (Gl.BindImageTexture == null)
            return _down.Refuse(_log, "glBindImageTexture is unresolved",
                                "point-light shadows take one hardware tap on the deferred path");
        // immutable storage, one level: a new size is a new name. NEAREST because the bilateral upsample
        // fetches texels and weights them itself -- and an integer texture under LINEAR is incomplete.
        RenderTarget.Create(ref _gl.TexVis, Gl.RGBA8, w, h, Gl.NEAREST);
        RenderTarget.Create(ref _gl.TexKey, Gl.RG32UI, w, h, Gl.NEAREST);
        _gl.W = w; _gl.H = h;
        _log?.Info($"DeferredShadows: visibility record {w}x{h} ({(long)w * h * BytesPerTexel >> 20} MiB)");
        return true;
    }

    /// <summary>Build the program on the first frame that wants it, once. False is latched and LOUD: the
    /// deferred path then stands down in favour of the forward loop, which has shadows of its own.</summary>
    internal static bool Ready()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing",
                              "deferred point lights are off for the session -- the forward loop walks every light");
        uint prog = GlProgram.Compute(ShdCs, ShaderStages.Source(ShdCs), _log, "DeferredShadows");
        if (prog == 0)
        {
            _log?.Always("DeferredShadows: the per-pixel shadow program did not build (the driver's own log is "
                       + "above) -- deferred point lights are off for the session and the forward light loop, "
                       + "whose own shadow filter is intact, draws the frame");
            return _down.Fail(_log, "the compute program did not link",
                              "deferred point lights are off for the session");
        }
        Select(prog);
        _log?.Info("DeferredShadows: ready -- one 8x8 compute dispatch writes the per-pixel visibility record");
        return true;
    }

    /// <summary>The program this pass binds. Assigning points the pass at another build of its own stage, for
    /// a diagnostic that measures one; the locations come with it, since a build is its own program object.</summary>
    internal static uint Program { get => _gl.Prog; set => Select(value); }

    /// <summary>Bind <paramref name="prog"/> and take its uniform locations -- at link, and again on each
    /// change of <see cref="Program"/>.</summary>
    private static void Select(uint prog)
    {
        _gl.Prog = prog;
        _gl.LocRect = GlProgram.Loc(prog, "uRect");
        _gl.LocInvVp = GlProgram.Loc(prog, "uInvVp");
        _gl.LocProj = GlProgram.Loc(prog, "uProj");
        _gl.LocCubes = GlProgram.Loc(prog, "uLocalCubes");
        _gl.LocShSize = GlProgram.Loc(prog, "uShSize");
        _gl.LocShRes = GlProgram.Loc(prog, "uShRes");
        // The two cube arrays carry no layout(binding) -- the lib is shared with programs that bind them on
        // other units -- so they take theirs here, as every lit program does at its own link.
        Gl.UseProgram(prog);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(prog, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.UseProgram(0);
    }

    /// <summary>Context loss: the program and the record textures are gone.</summary>
    internal static void Forget()
    {
        _gl = new ShadowRecordState();
        Res = 0;
        _down.Forget();
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        return _down.Failed ? "defshadow STOOD DOWN"
             : !Lighting.Deferred.V ? "defshadow off"
             : n == 0 ? "defshadow idle"
             : $"defshadow {_gl.W}x{_gl.H} ({(float)n / MathF.Max(frames, 1):F2} dispatches/frame, "
             + $"{(long)_gl.W * _gl.H * BytesPerTexel >> 20} MiB"
             + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "") + ")";
    }
}
