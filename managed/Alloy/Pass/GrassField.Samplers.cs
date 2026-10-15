using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>How a blade samples its atlas: two sampler objects because a chainless texture is mipmap-incomplete
/// under a mip filter, the Alloy-built mip chain that replaces the DDS box chain, and the one-shot mip
/// dump.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>The dials live in <see cref="Grass"/> (ADR 0025); what stays here is the render-thread state
    /// a set arms (<see cref="SamplerDirty"/>, <see cref="Rebake"/>, <see cref="GroundDiag"/>).</summary>
    private static float _appliedScaleBias;
    private static bool _samplerDirty = true;

    private static float _appliedAniso = 1f;
    private static bool _anyFlat;

    /// <summary>Every live grass material's texture name, for this frame only -- never cached.</summary>
    private static void ResolveTextures(int* tids, bool* live)
    {
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            CAurGrassInfo info = GrassInfo(m);
            live[m] = !info.IsNull && info.InUse != 0;
            IntPtr tex = !live[m] ? IntPtr.Zero
                : _aurGet((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name), IntPtr.Zero, null, null, 0, 0);
            tids[m] = OwnedTextures.Draw(AurTex.Tid(tex));
        }
    }

    /// <summary><c>/grass dumpmips</c>: one-shot readback of every level of every live grass atlas, as
    /// <c>cx-grassmip-{material}-L{level}.ppm</c> (rgb) + <c>.pgm</c> (alpha) in the user directory. The
    /// ground truth for what the blades sample; cold path, allocates.</summary>
    private static bool _dumpMips;
    private static void DumpMips(int* tids, bool* live)
    {
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            if (!live[m] || tids[m] == 0) continue;
            uint tm = (uint)tids[m];
            for (int l = 0; l < 16; l++)
            {
                int w = 0, h = 0, fmt = 0;
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_WIDTH, &w);
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_HEIGHT, &h);
                Gl.GetTextureLevelParameteriv(tm, l, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
                if (w <= 0 || h <= 0) break;
                var px = new byte[w * h * 4];
                fixed (byte* pp = px) Gl.GetTextureImage(tm, l, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, pp);
                var rgb = new byte[w * h * 3]; var a = new byte[w * h];
                for (int i = 0; i < w * h; i++) { rgb[i*3] = px[i*4]; rgb[i*3+1] = px[i*4+1]; rgb[i*3+2] = px[i*4+2]; a[i] = px[i*4+3]; }
                string b = Native.ConfigPath($"cx-grassmip-{m}-L{l}");
                using (var f = File.Create(b + ".ppm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n"); f.Write(hd); f.Write(rgb); }
                using (var f = File.Create(b + ".pgm")) { var hd = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"); f.Write(hd); f.Write(a); }
                _log?.Info($"[grass] dumpmips: material {m} tid {tids[m]} level {l} {w}x{h} fmt 0x{fmt:x} -> {b}.ppm/.pgm");
            }
        }
    }

    /// <summary><c>GL_TEXTURE_IMMUTABLE_FORMAT</c>: 1 on a texture whose storage came from <c>glTexStorage2D</c>.</summary>
    private const uint TextureImmutableFormat = 0x912F;   // not-an-offset: GL enum

    /// <summary>Every grass atlas samples an Alloy-built chain: level 0 read back through the driver's own
    /// decode, mips averaged alpha-weighted, the whole texture respecified RGBA8 -- a DDS ships a box chain
    /// over black transparent texels, so a mid-field blade sampled a fraction of its colour as an opaque
    /// cutout. The rebuild is marked by MAX_LEVEL == levels-1 on RGBA8, which a fresh engine upload lacks.</summary>
    private static bool HasMips(int tid)
    {
        uint t = (uint)tid;
        int w0 = 0, h0 = 0, fmt = 0, cap = 0, w1 = 0;
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w0);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_HEIGHT, &h0);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Gl.GetTextureParameteriv(t, Gl.TEXTURE_MAX_LEVEL, &cap);
        int* lw = stackalloc int[16], lh = stackalloc int[16]; long* off = stackalloc long[16];
        long total = 0;
        int n = w0 > 0 ? TexScan.Chain(w0, h0, true, 16, lw, lh, off, out total) : 0;
        if (n > 0 && fmt == (int)Gl.RGBA8 && cap == n - 1) return true;   // the marker: already ours
        // The BCn transcode drip (#265) re-creates an uncompressed atlas as IMMUTABLE storage, which TexImage2D
        // cannot respecify: the rebuild below failed silently every frame (a readback sync + the mip loop +
        // ten refused uploads, 22k times a minute in a dense module = 144 -> 30 fps). Its chain is already
        // TexScan.BuildMips' alpha-weighted one, so there is nothing to rebuild.
        int immutable = 0;
        Gl.GetTextureParameteriv(t, TextureImmutableFormat, &immutable);
        if (immutable != 0) return n > 0;
        if (n == 0)
        {
            Gl.GetTextureLevelParameteriv(t, 1, Gl.TEXTURE_WIDTH, &w1);
            _anyFlat |= w1 <= 0;
            return w1 > 0;
        }
        // ponytail: cold one-shot per upload, allocates; the AMD GetTexImage trap (#396) was the per-upload
        // coverage scan, not a handful of grass atlases -- revisit if a radeonsi client faults here.
        var px = new byte[total];
        fixed (byte* pp = px)
        {
            Gl.GetTextureImage(t, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, (int)total, pp);
            TexScan.BuildMips(pp, n, lw, lh, off);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
            // no DSA respecify exists and the name is the engine's: this rides DrawSlot's draw bind of tid
            for (int l = 0; l < n; l++)
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.RGBA8, lw[l], lh[l], 0, Gl.RGBA, Gl.UNSIGNED_BYTE, &pp[off[l]]);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        }
        Gl.TextureParameteri(t, Gl.TEXTURE_MAX_LEVEL, n - 1);
        _mipsBuilt++;
        _log?.Info($"[grass] rebuilt the mip chain of tid {tid}: {w0}x{h0} fmt 0x{fmt:x} -> RGBA8, {n} levels alpha-weighted");
        return true;
    }

    /// <summary>The two samplers, and the dials they carry. Anisotropy is the one that matters for grass:
    /// blades are looked at edge-on, where an isotropic filter has to pick one mip for a footprint long in one
    /// axis and short in the other, and picks wrong in both -- the crawling noise in the mid-field. Wrap is
    /// CLAMP_TO_EDGE because the UVs address one cell of a 2x2 atlas and must never cross the seam.</summary>
    private static void EnsureSamplers()
    {
        if (Gl.GenSamplers == null || Gl.SamplerParameteri == null || Gl.SamplerParameterf == null) return;
        if (_gl.SampMip == 0)
        {
            uint* ids = stackalloc uint[2];
            Gl.GenSamplers(2, ids);
            if (ids[0] == 0 || ids[1] == 0) return;
            _gl.SampMip = ids[0]; _gl.SampFlat = ids[1];
            _samplerDirty = true;
        }
        // #391: the render-scale bias rides the soften dial; re-applied when the scale moves
        float scaleBias = PostChain.LodBias;
        if (!_samplerDirty && scaleBias == _appliedScaleBias) return;
        _samplerDirty = false; _appliedScaleBias = scaleBias;
        float maxAniso = 1f;
        if (Gl.GetFloatv != null) { float v; Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &v); if (v > 1f) maxAniso = v; }
        float aniso = MathF.Min(Grass.Aniso.V, maxAniso);
        for (int i = 0; i < 2; i++)
        {
            uint sm = i == 0 ? _gl.SampMip : _gl.SampFlat;
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MIN_FILTER, i == 0 ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_MAX_ANISOTROPY, aniso);
            Gl.SamplerParameterf(sm, Gl.TEXTURE_LOD_BIAS, Grass.Soften.V + scaleBias);
        }
        _appliedAniso = aniso;
    }
}
