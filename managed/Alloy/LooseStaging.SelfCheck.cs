using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>Two binds of a depth program inside one frame: one view's block, then another's. Oracle: the
/// hand-written views and arena offsets, and the block's own bytes read back out of the arena.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>A depth program as <c>VkBuild</c> leaves one that declares the view alone: its member table, its
    /// block and the location table the non-lit arm resolves against it.</summary>
    private static VkProg LooseStagingProg()
    {
        var p = new VkProg { Label = "Depth", Members = [new LooseBlock.Member("uVP", 0, 64, 0)], Block = LooseBlock.Block(64) };
        p.Names = LooseBlock.Index(p.Members);
        LooseBlock.Resolve(p.Names, () =>
        {
            p.Vp = LooseBlock.Loc("uVP"); p.Viz = LooseBlock.Loc("uViz"); p.Tint = LooseBlock.Loc("uTint");
            p.Oit = LooseBlock.Loc("uOit");
            p.F = Programs.FeatLocs();
        });
        return p;
    }

    /// <summary>One bind of <paramref name="p"/> under view <paramref name="vp"/>: the arena offset it got.</summary>
    private static int LooseStagingBind(VkProg p, in Matrix4x4 vp)
    {
        int[] viewport = [0, 0, 640, 480];
        fixed (int* v = viewport) VkFillLoose(p, in vp, v, 1f, 100f, 0, lit: false);
        return VkLooseStage(p, _vkc);
    }

    internal static bool LooseStagingSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.LooseStaging.SelfCheck");
        int frameWas = _frameSeq, changesWas = _vkc.LooseChanges;
        string noteWas = _vkc.LooseChangeNote;
        _frameSeq = 11;
        _vkc.LooseChanges = 0; _vkc.LooseChangeNote = "";
        VkArenaRewind();
        VkProg p = LooseStagingProg();
        Matrix4x4 a = Matrix4x4.CreateTranslation(1f, 2f, 3f), b = Matrix4x4.CreateTranslation(9f, 8f, 7f);

        int first = LooseStagingBind(p, in a);
        int again = LooseStagingBind(p, in a);
        int other = LooseStagingBind(p, in b);
        check.Expect(first >= 0 && again == first, $"a second bind of the same view took a second slice ({first}, {again})");
        check.Expect(other >= 0 && other != first, $"a second view inside one frame shared the first view's slice at {other}");
        ReadOnlySpan<float> mine = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_vkc.ArenaBytes.AsSpan(other, 64));
        check.Expect(mine[12] == 9f && mine[13] == 8f && mine[14] == 7f,
                     $"the second view's slice holds the second view ({mine[12]}, {mine[13]}, {mine[14]})");
        ReadOnlySpan<float> held = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_vkc.ArenaBytes.AsSpan(first, 64));
        check.Expect(held[12] == 1f && held[13] == 2f && held[14] == 3f,
                     $"the first view's slice was written over by the second ({held[12]}, {held[13]}, {held[14]})");
        check.Expect(_vkc.LooseChanges == 1 && _vkc.LooseChangeNote.Contains("Depth uVP at byte 50"),
                     $"the change inside the frame was counted and named: {_vkc.LooseChanges}, {_vkc.LooseChangeNote}");

        // a new frame is a new arena: the program's memo of an offset in the old one must not answer for it
        _frameSeq = 12;
        VkArenaRewind();
        int next = LooseStagingBind(p, in b);
        check.Expect(next == 0, $"the first bind of a new frame did not start the arena over ({next})");

        VkArenaRewind();
        _frameSeq = frameWas;
        _vkc.LooseChanges = changesWas; _vkc.LooseChangeNote = noteWas;
        log.Info(check.Ok ? "AlloyRenderer.LooseStaging.SelfCheck: passed" : "AlloyRenderer.LooseStaging.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
