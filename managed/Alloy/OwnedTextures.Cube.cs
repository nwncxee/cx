using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Cube textures, the one class of engine texture the 2D upload funnel never sees (<c>glImageBasic</c> routes
/// <c>flagCube</c> to <c>CreateCubeTexture</c>). <c>GLRender::CreateCubeTexture</c> @0x4caab0 (Windows RVA 0xAEFF0)
/// transcribed onto an immutable cube of Alloy's own, as <see cref="UploadOwn"/> does a 2D one; the cell array is
/// handed Alloy's name and the engine's dies unused. Nothing in Alloy samples it; no scan.
/// </summary>
internal static unsafe partial class OwnedTextures
{
    private static RiCreateCubeTextureFn _origCreateCube;
    private static long _cubeUploads, _cubeRefused;
    /// <summary>A cube's faces: the strip's slot count, and the factor <see cref="Measure"/>'s one-face answer is short by.</summary>
    private const int CubeFaces = 6;
    /// <summary>The clamp bits the engine's cube upload passes <c>SetTextureParameters</c>: S, T and R clamped.</summary>
    private const int CubeClamp = 7;

    /// <summary>The engine is about to upload cell <paramref name="cell"/> of a cube texture into the name it has
    /// bound to <c>GL_TEXTURE_CUBE_MAP</c>. Cold: once per cube per (re)build.</summary>
    [UnmanagedCallersOnly]
    private static void OnCreateCubeTexture(IntPtr tex, byte useSubImage, int cell)
    {
        long t0 = FrameZones.Now;
        if (!(_single && AdoptCube(tex, cell))) _origCreateCube(tex, useSubImage, cell);
        FrameZones.Add(FrameZones.Zone.Tex, t0);
        FrameZones.Count(FrameZones.Ev.TexUpload);
    }

    /// <summary>True when Alloy's cube stands in for the engine's; false leaves the upload to the caller.</summary>
    private static bool AdoptCube(IntPtr tex, int cell)
    {
        if (!_armed || !AurTex.IsBasic(tex) || !GlContextService.Available) return false;
        var b = new CAurTextureBasic(tex);
        uint* cells = (uint*)b.CellNames;
        int n = b.NumX * b.NumY;
        if (cells == null || cell < 0 || cell >= n || n > MaxCells) return false;
        int etid = (int)cells[cell];
        if (etid <= 0) return false;
        FreshName((uint)etid);

        uint dst = UploadCube(tex, cell, out uint err);
        if (dst == 0)
        {
            if (++_cubeRefused <= 3)
                _log?.Always($"OwnedTextures: cube tid {etid} left to the engine -- "
                           + (err != 0 ? $"Alloy's upload gave 0x{err:x}" : $"not a shape Alloy uploads ({TexScan.Describe(tex)})"));
            return false;
        }
        long bytes = Measure(dst) * CubeFaces;

        cells[cell] = dst;
        if (cell == 0) b.GlId = (int)dst;
        uint old = (uint)etid;
        if (!_owned.Contains(old) && Gl.DeleteTextures != null) { Gl.DeleteTextures(1, &old); ScrubEngineBoundCache(old); }
        else _engineHeld.Remove(old);
        _engineHeld.Add(dst);
        _borrowed.Remove(etid);
        _stats.Remove(dst);
        Replace(Key(tex, cell), (int)dst, dst, bytes);
        HighWater();
        _cubeUploads++;
        return true;
    }

    /// <summary>Cell <paramref name="cell"/> of a cube's strip into a cube of Alloy's own, faces per <see cref="CubeStrip.Cut"/>,
    /// engine mips; two channels go up as RG and one with no swizzle, as the engine's cube path (unlike its 2D one)
    /// does. 0 = a compressed image, a non-square face, or a driver refusal (<paramref name="err"/>).</summary>
    private static uint UploadCube(IntPtr tex, int cell, out uint err)
    {
        err = 0;
        var t = new CAurTexture(tex);
        int w = t.GetWidth(), h = t.GetHeight(), bpp = t.GetBPP(), nx = t.GetNumX(), ny = t.GetNumY();
        ReadOnlySpan<byte> src = t.Pixels;
        if (src.IsEmpty || bpp is < 1 or > 4 || nx <= 0 || ny <= 0 || t.GetCompressedSize() > 0) return 0;
        int slotH = h / CubeFaces, side = w / nx;
        if (side <= 0 || side != slotH / ny || cell < 0 || cell >= nx * ny) return 0;
        bool mip = (t.GetMipMap() & MipByte) != 0;
        Sampler s = EngineSampler(CubeClamp, t.GetFilter() != 0, mip);
        uint sized = bpp switch { 4 => Gl.RGBA8, 3 => Gl.RGB8, 2 => Gl.RG8, _ => Gl.R8 };
        uint fmt = bpp switch { 4 => Gl.RGBA, 3 => Gl.RGB, 2 => Gl.RG, _ => Gl.RED };
        Gl.DrainErrors(8);   // the verdict is ours to read, so nobody else's error may be sitting here
        uint dst = Storage(Gl.TEXTURE_CUBE_MAP, mip ? EngineLevels(side, side) : 1, sized, side, side, s, red: false);
        if (dst == 0) return 0;
        var face = new byte[side * side * bpp];
        var u = Unpack.NoBuffer();
        fixed (byte* p = face)
            for (int f = 0; f < CubeFaces; f++)
            {
                CubeStrip.Cut(src, w, slotH, bpp, nx, ny, cell, f, face);
                Gl.TextureSubImage3D(dst, 0, 0, 0, f, side, side, 1, fmt, Gl.UNSIGNED_BYTE, p);
            }
        u.Give();
        if (mip) Gl.GenerateTextureMipmap(dst);
        err = Gl.GetError();
        if (err == 0) return dst;
        Gl.DeleteTextures(1, &dst);
        return 0;
    }
}
