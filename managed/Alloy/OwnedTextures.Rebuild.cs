using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The textures the engine's own one-shot build queue lost to a residency race: a live CAurTexture whose
/// GL id is still zero is re-offered to BuildAndStore a few per frame, and draws white until it lands.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>Textures whose GL id is still 0 -- a live <c>CAurTexture*</c> whose build never produced a
    /// name. The engine builds a texture EXACTLY twice (creation and a PLT scheme change) and
    /// <c>AurTextureBuildAndStoreAll</c> @0x48e680 drains that list whether or not each build succeeded, with
    /// nothing re-enqueueing on resource arrival: the one-shot queue can lose a race with residency.</summary>
    private static readonly IntPtr[] _rebuildQ = new IntPtr[RebuildQueue];
    private static int _rebuildN;
    /// <summary>What one texture has cost, and whether asking again can ever pay: attempts spent, the drip
    /// frame the last one ran on, and a verdict of NEVER for the two states the binary says no build can
    /// leave. Re-armed every <see cref="RebuildRearm"/> drip frames, up to <see cref="RebuildAttempts"/>
    /// times. Keyed on the raw <c>CAurTexture*</c>, so <see cref="AreaEdge"/> drops the ledger whole.</summary>
    private struct Try { internal int Tries, Frame; internal bool Never; }
    private static readonly Dictionary<IntPtr, Try> _rebuildTried = new();
    private static int _dripFrame;
    private static long _rebuilds, _rebuildWon, _rebuildDead, _rebuildDropped;
    /// <summary>Asks the ledger refused to re-ask about (verdict already final). Nonzero beside a null-tex count is the ledger, not a race.</summary>
    private static long _rebuildRefused;
    /// <summary>The residual, split by the reason a build cannot help -- see <see cref="Rebuild"/> for what
    /// each one is in the binary. These are POPULATIONS of distinct textures, not attempt counts: a texture
    /// is charged to exactly one of them, at its FINAL disposition, once.</summary>
    private static long _rebuildNullTex, _rebuildNoBasic, _rebuildNoImage, _rebuildSpent;
    /// <summary>How many unbuilt textures may wait at once, how many rebuilds a frame may pay for, how many
    /// attempts one texture is worth, and how long it waits between them -- at most four engine builds per
    /// frame, on frames where anything is queued. The re-arm interval is a WAIT (about five seconds at 60fps)
    /// and the attempt cap is what stops it becoming a cadence.</summary>
    private const int RebuildQueue = 64, RebuildBudget = 4, RebuildAttempts = 3, RebuildRearm = 300;

    /// <summary>Note a texture that resolved to GL id 0, for <see cref="DripRebuilds"/> to retry. Cold --
    /// called from the enrollment scan, not from the draw loop -- and it allocates only on the first sighting
    /// of a given pointer. Nothing here dereferences <paramref name="tex"/>; the liveness guard is at the
    /// drain, where the call actually happens.</summary>
    internal static void NoteUnbuilt(IntPtr tex)
    {
        if (tex == IntPtr.Zero || _buildAndStore == null) return;
        if (_rebuildTried.TryGetValue(tex, out Try t)
            && (t.Never || t.Tries >= RebuildAttempts || _dripFrame - t.Frame < RebuildRearm))
        { if (t.Never || t.Tries >= RebuildAttempts) _rebuildRefused++; return; }
        if (_rebuildN >= RebuildQueue) { _rebuildDropped++; return; }
        for (int i = 0; i < _rebuildN; i++) if (_rebuildQ[i] == tex) return;
        _rebuildQ[_rebuildN++] = tex;
    }

    /// <summary>The one opaque white texel a part draws on when its diffuse resolves to GL id 0. Vanilla
    /// parity: <c>AurTextureGetTID</c> @0x48e080 returns 0 for the engine's shared <c>m_pNullTexture</c> and
    /// the draw is still issued, so the part renders blank white where Alloy's own skip made it invisible.
    /// Minted lazily inside the 4.6 bracket and registered OWNED; 0 out means the caller keeps its skip.</summary>
    internal static int WhiteTid()
    {
        if (_standIn.White != 0) return (int)_standIn.White;
        if (!GlContextService.Available) return 0;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) return 0;
        uint white = 0xFFFFFFFF;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, 1, 1);
        Gl.TextureSubImage2D(t, 0, 0, 0, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &white);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        _owned.Add(t);
        _standIn.White = t;
        _log?.Info($"OwnedTextures: minted the white null-texture stand-in (tid {t}) -- a diffuse with no GL id now draws as vanilla draws it");
        return (int)t;
    }
    /// <summary>The one transparent-black texel every empty map slot's bindless handle points at: what NVIDIA
    /// and Mesa return for a null handle, which AMD's driver faults on instead. Minted lazily inside the 4.6
    /// bracket and registered OWNED; 0 out means the caller uploads the zero handle as before.</summary>
    internal static int NullTid()
    {
        if (_standIn.Black != 0) return (int)_standIn.Black;
        if (!GlContextService.Available) return 0;
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        if (t == 0) return 0;
        uint zero = 0;
        Gl.TextureStorage2D(t, 1, Gl.RGBA8, 1, 1);
        Gl.TextureSubImage2D(t, 0, 0, 0, 1, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, &zero);
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        _owned.Add(t);
        _standIn.Black = t;
        return (int)t;
    }
    /// <summary>The <see cref="WhiteTid"/> name, 0 until minted. Dropped (never deleted) on
    /// <see cref="ContextLost"/> with every other name, and re-minted on the next draw that needs it.</summary>
    private sealed class StandInState { public uint White, Black; }
    private static StandInState _standIn = new();
    /// <summary>The white stand-in's name, 0 until minted; a read, never a mint (stage workers call it).</summary>
    internal static int WhiteName => (int)_standIn.White;

    /// <summary>Spend at most <see cref="RebuildBudget"/> engine texture builds, once a frame before the Alloy
    /// bracket with the ENGINE's context current. That is not incidental: <c>BuildAndStore</c> re-runs the
    /// engine's own upload and the engine caches what it thinks is bound, so running it inside the 4.6 bracket
    /// would leave that cache naming objects in the other context. A retry lands in the owned table itself.</summary>
    internal static void DripRebuilds()
    {
        _dripFrame++;
        CensusSettleCheck();
        DdsReader.Tick();
        TexLoad.Drip();
        ModelLoad.Drip();
        TexStream.Tick();
        DripTranscodes();
        if (_buildAndStore == null) return;
        long tp = FrameZones.Now;
        DripPrefetch();
        FrameZones.Add(FrameZones.Zone.Prebuild, tp);
        for (int spent = 0; spent < RebuildBudget && _rebuildN > 0; spent++)
        {
            IntPtr tex = _rebuildQ[--_rebuildN];
            _rebuildQ[_rebuildN] = IntPtr.Zero;
            FrameZones.Count(FrameZones.Ev.TexBuild);
            Rebuild(tex);
        }
    }

    /// <summary>One retry, guarded, with the refusals that come BEFORE the call. The <c>CAurTexture*</c> is
    /// borrowed, so it is re-proven through <see cref="AurTex"/> first. Then the identity question:
    /// <c>AurTextureGetTID</c> @0x48e080 returns 0 OUTRIGHT for the shared <c>m_pNullTexture</c>, and building
    /// THAT deletes the GL names every other material falls back to. Only "no decoded image" is worth asking.</summary>
    private static void Rebuild(IntPtr tex)
    {
        long refusedBefore = AurTex.Refused;
        if (AurTex.Tid(tex) != 0) { _rebuildWon++; _rebuildTried.Remove(tex); return; }
        if (AurTex.Refused != refusedBefore) { _rebuildDead++; Give(tex, true); return; }

        IntPtr real = new CAurTexture(tex).GetReferencedTexture().Ptr;
        if (real == IntPtr.Zero) real = tex;
        if (_nullTex != null && real == *_nullTex)
        { _rebuildNullTex++; Give(tex, true); UnbuiltDiag(tex, "the engine's shared NULL texture", IntPtr.Zero, 0); return; }
        IntPtr basic = new CAurTexture(real).AsCAurTextureBasic().Ptr;
        if (basic == IntPtr.Zero)
        { _rebuildNoBasic++; Give(tex, true); UnbuiltDiag(tex, "no CAurTextureBasic behind it", IntPtr.Zero, 0); return; }

        // The PLT composite runs on the CPU only while the engine's global says so, and ConstructImage reads
        // it at the moment of the build -- so it is asserted here rather than trusted from Attach.
        KeepPltOnCpu();
        _rebuilds++;
        _buildAndStore(tex);
        TexStream.Dequeue(basic);
        int tid = AurTex.Tid(tex);
        if (tid != 0) { _rebuildWon++; _rebuildTried.Remove(tex); return; }
        // Counted at FINAL disposition only. Charged per attempt these would be attempt tallies wearing a
        // population's label -- two textures at three tries each reading as six -- and a texture that wins on
        // its last attempt would sit in the residual and in `now have an id` at once.
        if (Give(tex, false))
        {
            if (new CAurTextureBasic(basic).FlagNoImage != 0) _rebuildNoImage++;
            else _rebuildSpent++;
        }
        UnbuiltDiag(tex, "built, still no id", basic, tid);
    }

    /// <summary>Charge one attempt against a texture, or write it off for good. Returns true when this was
    /// the last attempt it will get, so the caller can count a residual exactly once.</summary>
    private static bool Give(IntPtr tex, bool never)
    {
        _rebuildTried.TryGetValue(tex, out Try t);
        t.Tries++; t.Frame = _dripFrame; t.Never = never;
        _rebuildTried[tex] = t;
        return never || t.Tries >= RebuildAttempts;
    }

    /// <summary>Name what a still-unbuilt texture actually IS, once per distinct resource: the counters say
    /// how many, only these fields say which of the two states the binary allows. <c>scheme</c> is
    /// <c>basic+0x48</c>, the layer colours <c>SetScheme</c> @0x485f90 allocates -- nonzero is a PLT texture,
    /// which is what a player character's head, torso and thighs are.</summary>
    private static void UnbuiltDiag(IntPtr tex, string what, IntPtr basic, int tid)
    {
        if (_unbuiltDiagLeft <= 0) return;
        string nm = AlloyCensus.TexName(tex);
        if (!_unbuiltDiagSeen.Add(nm)) return;
        _unbuiltDiagLeft--;
        _log?.Info($"OwnedTextures: [unbuilt] \"{nm}\" -- {what}"
                 + (basic == IntPtr.Zero ? "" :
                    $"; scheme={(new CAurTextureBasic(basic).Schemes == IntPtr.Zero ? "none (file-backed)" : "PLT")}"
                  + $" image={(new CAurTextureBasic(basic).Image == IntPtr.Zero ? "none" : "decoded")}"
                  + $" built={new CAurTextureBasic(basic).FlagBuilt} invalid={new CAurTextureBasic(basic).FlagNoImage} tid={tid}")
                 + $" (#284: {_rebuildTried[tex].Tries}/{RebuildAttempts} attempts)");
    }

    private static int _unbuiltDiagLeft = 8;
    private static readonly HashSet<string> _unbuiltDiagSeen = new();

    private static AurTextureBuildAndStoreFn _buildAndStore;
    private static IntPtr* _nullTex;

    /// <summary>For the period line: how many unbuilt textures Alloy has retried, how many now have an id, and
    /// how many it refused to touch. A climbing rebuild count is a texture population arriving late, not a
    /// leak; a nonzero drop count means the queue is too small for the area. The residual is attributed, and
    /// <c>no image</c> is its one term that was still waiting on something.</summary>
    internal static string RebuildNote()
    {
        long residual = _rebuildNullTex + _rebuildNoBasic + _rebuildNoImage + _rebuildSpent;
        if (_rebuilds == 0 && _rebuildN == 0 && _rebuildDropped == 0 && residual == 0) return "";
        return $", unbuilt textures: {_rebuilds} retried ({_rebuildWon} now have an id, {_rebuildDead} refused as "
             + $"freed), {_rebuildN} queued, {_rebuildRefused} skips refused by the ledger"
             + (_rebuildDropped == 0 ? "" : $", {_rebuildDropped} DROPPED (queue full)")
             + (residual == 0 ? "" : $"; UNBUILDABLE: {_rebuildNullTex} are the NULL texture, {_rebuildNoBasic} "
                 + $"have no basic, {_rebuildNoImage} found no image (resource not resident), {_rebuildSpent} "
                 + "out of attempts (all four are holes for the session; `no image` is the one whose resource "
                 + "never arrived inside the attempt window)");
    }
}
