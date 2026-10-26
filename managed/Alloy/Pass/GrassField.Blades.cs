using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Which atlas cells draw as procedural blades. Servers and builders swap grass textures at will
/// (<c>AddGrassOverride</c>, per-area tilesets) and NWN does not say which is grass and which is a flower, so
/// each live atlas is classified from its own pixels (<see cref="GrassAtlas"/>) once per upload. ponytail: no
/// hak 2DA override of the verdict -- that needs a <c>C2DA</c> read on both platforms.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>One material slot's verdict, keyed by the texture it was read from: the GL name and a hash of
    /// the resref. Either changing (an override, an area transition re-uploading under a recycled name)
    /// re-classifies; <c>Done</c> with no blade bits is also the failure latch, so an unreadable atlas is asked
    /// once, not every frame (the <see cref="HasMips"/> per-frame-rebuild trap).</summary>
    private struct Cls { public int Tid; public uint Name; public int Kinds; public bool Done; }
    private static readonly Cls[] _cls = new Cls[MaxGrassMaterials];
    /// <summary>Root/mid/tip per cell per material, display space.</summary>
    private static readonly Vector3[] _ramp = new Vector3[MaxGrassMaterials * 12];
    /// <summary>Per cell per material: the art's mean blade length over the card height (<see cref="GrassAtlas.Cell.Height"/>).</summary>
    private static readonly float[] _bladeLen = new float[MaxGrassMaterials * 4];

    /// <summary>Every live material's BLADE cell bits this frame: none under the texture style, or without
    /// the blade program. <paramref name="cards"/> takes the complement -- does the material draw any CARD --
    /// off the same walk, because it is a property of the material and the visible-patch loop was re-deriving
    /// it per patch for an answer that cannot differ between two patches of one material.</summary>
    private static void ClassifyAll(int* tids, bool* live, int* bits, bool* cards, bool bladeProg)
    {
        bool blades = Grass.Style.V == Grass.StyleBlades && bladeProg;
        for (int m = 0; m < MaxGrassMaterials; m++)
        {
            CAurGrassInfo info = live[m] ? GrassInfo(m) : default;
            bits[m] = blades && !info.IsNull ? Classified(m, tids[m], info) : 0;
            cards[m] = !info.IsNull && CardCells(info, bits[m]);
        }
    }

    /// <summary>The material's BLADE cell bits, classifying its atlas when it is new. Cold on a miss: one
    /// level-0 readback per atlas upload, which allocates.</summary>
    private static int Classified(int m, int tid, CAurGrassInfo info)
    {
        byte* resref = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref info.Name);
        uint name = 2166136261u;
        for (int i = 0; i < 32 && resref[i] != 0; i++) name = (name ^ resref[i]) * 16777619u;
        ref Cls c = ref _cls[m];
        if (c.Done && c.Tid == tid && c.Name == name) return c.Kinds;
        if (tid == 0) return 0;   // not uploaded yet: a card this frame (which skips it too), asked again next
        c = new Cls { Tid = tid, Name = name, Done = true };
        int w = 0, h = 0;
        Gl.GetTextureLevelParameteriv((uint)tid, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTextureLevelParameteriv((uint)tid, 0, Gl.TEXTURE_HEIGHT, &h);
        if (w < 8 || h < 8 || (long)w * h > 4L << 20)
        {
            _log?.Info($"[grass] material {m} tid {tid}: atlas unreadable ({w}x{h}) -- drawn as cards");
            return 0;
        }
        var px = new byte[w * h * 4];
        Span<GrassAtlas.Cell> cells = stackalloc GrassAtlas.Cell[4];
        fixed (byte* p = px)
        {
            Gl.GetTextureImage((uint)tid, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, p);
            GrassAtlas.Classify(p, w, h, cells);
        }
        for (int k = 0; k < 4; k++)
        {
            if (cells[k].Blade) c.Kinds |= 1 << k;
            _ramp[m * 12 + k * 3] = cells[k].Root;
            _ramp[m * 12 + k * 3 + 1] = cells[k].Mid;
            _ramp[m * 12 + k * 3 + 2] = cells[k].Tip;
            _bladeLen[m * 4 + k] = cells[k].Height;
        }
        string v = "";
        for (int k = 0; k < 4; k++) v += (c.Kinds >> k & 1) != 0 ? $" BLADE({cells[k].Height:0.00}h)" : " CARD";
        _log?.Info($"[grass] material {m} tid {tid} {w}x{h} classified:{v}");
        return c.Kinds;
    }
}
