using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Grass as a shadow caster: the blades are drawn into the sun cascades the caster pass re-baked this
/// frame, on last frame's visible list, so the shadow follows the sway at the cascade's own cadence.</summary>
internal static unsafe partial class GrassField
{
    /// <summary><see cref="Grass.Shadow"/> sun cascades (nearest first) the blades are drawn into as
    /// casters; 0 = grass casts nothing. The draw reuses the depth prepass program under the cascade's
    /// matrix on LAST frame's visible list (the caster pass runs ahead of the colour pass), and only into a
    /// cascade the frame re-baked, so the shadow follows the sway at the static cascade's own cadence.</summary>
    private static int _shadowN;
    private static Vector3 _shadowEye;
    private static float _shadowNear, _shadowFar;
    private static long _shadowDraws;
    /// <summary><c>/grass dumpshadow</c>: write the next cast layer back as a PGM beside the shader dump.
    /// The render thread consumes it; the button only raises it.</summary>
    private static bool _dumpShadow;
    private static int _dumpN;

    /// <summary>Does cascade <paramref name="i"/> take the blades as casters this frame?</summary>
    internal static bool CastsInto(int i) => i < Grass.Shadow.V && _shadowN > 0 && Grass.Enabled.V && !_off && _gl.ProgDepth != 0;

    /// <summary>Draw last frame's scattered cards into every re-baked sun cascade under
    /// <see cref="Grass.Shadow"/>, as alpha-tested depth. The stream still holds last frame's scatter (the
    /// caster pass runs ahead of this frame's). Called by the caster pass after its layered flush, with its
    /// framebuffer; leaves FBO 0 bound.</summary>
    internal static void DrawShadows(uint fbo, uint tex, int size, bool[] rebaked, int cascades)
    {
        int want = Math.Min(Grass.Shadow.V, cascades);
        bool blades = _bladeNear + _bladeFar > 0 && _gl.ProgBladeDepth != 0, cards = _slotCount > 0 && _gl.ProgDepth != 0;
        if (want <= 0 || _shadowN <= 0 || !(blades || cards) || _gl.Vao == 0 || _off || !Grass.Enabled.V || _down.Failed) return;
        if (_binBucket.IsNull || *_grassEnabled == 0 || Grass.Density.V <= 0f) return;
        bool any = false;
        for (int i = 0; i < want && !any; i++) any = rebaked[i];
        if (!any) return;
        int* tids = stackalloc int[MaxGrassMaterials];
        bool* live = stackalloc bool[MaxGrassMaterials];
        ResolveTextures(tids, live);
        EnsureSamplers();
        float* pw = stackalloc float[3] { _pushOriginX, _pushOriginY, _pushPerUnit / PushRes };
        int* viewport = stackalloc int[4] { 0, 0, size, size };
        Vector3 eye = _shadowEye;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.DrawBuffer(Gl.NONE); Gl.ReadBuffer(Gl.NONE);
        Gl.Viewport(0, 0, size, size);
        Gl.BindVertexArray(_gl.Vao);
        // the caster gather leaves its layered commands bound; hand them back as found
        int prevCmd = 0;
        Gl.GetIntegerv(DrawIndirectBinding, &prevCmd);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, _gl.CmdBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindMats, _gl.MatBuf);
        new PassState(depth: true, depthWrite: true, cull: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        // the push field's unit is the post stack's HDR unit (TextureSlots.GrassPush == PostHdr): by the
        // shadow bracket its 2D binding is last frame's picture, and the blades would be displaced by it
        Gl.ActiveTexture(Gl.TEXTURE0 + PushUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.PushTex);
        Gl.ActiveTexture(Gl.TEXTURE0);
        for (int i = 0; i < want; i++)
        {
            if (!rebaked[i]) continue;
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, tex, 0, i);
            Matrix4x4 vp;
            fixed (float* pub = CasterPass.CascadeVp.AsSpan(i * 16)) vp = *(Matrix4x4*)pub;
            if (blades)
            {
                Gl.UseProgram(_gl.ProgBladeDepth);
                SharedUniforms(in _gl.LocBD, in _gl.FeatLocBD, &vp, &eye, _shadowNear, _shadowFar, false, pw, viewport, vlit: false);
                Gl.Uniform1i(_gl.LocBD.Cast, 1);
                DrawBlades(in _gl.LocBD);
            }
            if (cards)
            {
                Gl.UseProgram(_gl.ProgDepth);
                SharedUniforms(in _gl.LocD, in _gl.FeatLocD, &vp, &eye, _shadowNear, _shadowFar, false, pw, viewport, vlit: false);
                Gl.Uniform1i(_gl.LocD.Cast, 1);
                Gl.Uniform1i(_gl.LocD.Frame, 0);   // a cascade bake is held across frames: a moving dither would freeze mid-step
                Gl.Uniform1i(_gl.LocD.SoftEdge, 0);
                DrawSlots(tids, in _gl.LocD, depth: true);
            }
            _shadowDraws++;
            if (_dumpShadow) { _dumpShadow = false; DumpLayer(tex, i, size); }
        }
        if (_gl.SampMip != 0) Gl.BindSampler(0, 0);
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, (uint)prevCmd);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
    }

    /// <summary>Ground truth for the cast layer: an 8-bit PGM (near = bright), read back through
    /// glGetTextureSubImage. Cold, once per command.</summary>
    private static void DumpLayer(uint tex, int layer, int size)
    {
        if (Gl.GetTextureSubImage == null) { _log?.Info("GrassField: no glGetTextureSubImage -- no dump"); return; }
        var depth = new float[size * size];
        fixed (float* p = depth)
            Gl.GetTextureSubImage(tex, 0, 0, 0, layer, size, size, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, size * size * 4, p);
        float min = 1f, max = 0f; int near = 0;
        foreach (float d in depth) { if (d < min) min = d; if (d > max) max = d; if (d < 0.999f) near++; }
        string path = Native.ConfigPath($"cx_grass_shadow_{_dumpN++}.pgm");
        using var f = new System.IO.FileStream(path, System.IO.FileMode.Create);
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{size} {size}\n255\n");
        f.Write(hdr, 0, hdr.Length);
        var row = new byte[size * size];
        for (int i = 0; i < depth.Length; i++) row[i] = (byte)Math.Clamp((1f - depth[i]) * 255f * 4f, 0f, 255f);
        f.Write(row, 0, row.Length);
        _log?.Info($"GrassField: cast layer {layer} dumped to {path} -- depth min {min:0.0000} max {max:0.0000}, {near} texels written of {depth.Length}, {_shadowDraws} cascade draws so far");
    }

    /// <summary>Sampler units for the shadow rig + push texture; both grass programs share the vertex text
    /// and so both carry these samplers.</summary>
    private static void ShadowUnits(in Loc l)
    {
        Gl.Uniform1i(l.SunTex, (int)AlloyRenderer.SunUnit);
        // the grass program's unit 10 is uPushTex, so its sun-dyn sampler reads the second unit the table assigns
        Gl.Uniform1i(l.SunDynTex, (int)AlloyRenderer.SunDynUnitGrass);
        Gl.Uniform1i(l.CubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(l.CubeDynTex, (int)AlloyRenderer.CubeDynUnit);
        Gl.Uniform1i(l.PushTex, (int)PushUnit);
        Gl.Uniform1i(l.Ground, (int)TextureSlots.GrassGround);
        Gl.Uniform1i(l.Probe, (int)EnvProbe.ProbeUnit);
    }
}
