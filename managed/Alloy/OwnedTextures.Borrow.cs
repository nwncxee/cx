using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Why a texture Alloy samples is not one Alloy uploaded. Every engine tid an owned draw falls back on is
/// recorded under the shape that left it borrowed, so the acceptance line says WHICH kind is left rather than
/// only how many -- the count that decides what to close next.
/// </summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>The shapes a borrowed tid can come from, in the order the period line reports them.</summary>
    internal enum Borrow
    {
        /// <summary>The tid never reached <c>RenderInterface::CreateTexture</c> @0x55d200, so no shape was recorded.
        /// Its one caller is <c>CAurTextureBasic::glImageBasic</c>, which allocates the cell array and loops the
        /// cell over <c>numX*numY</c> first, so <see cref="Adopt"/>'s unrecorded exits -- not a basic, no array,
        /// cell out of range, cell name 0 -- are states the funnel cannot be in, or a name no draw asks about.</summary>
        Funnel,
        /// <summary>The table is stood down for this upload (<c>alloy.texsingle=0</c>, or the hook did not install),
        /// so the engine's own copy is what the draw samples.</summary>
        StoodDown,
        /// <summary>Uploaded before the 4.6 context existed. <see cref="RefreshPreContext"/> re-drives the engine's
        /// own refresh once, which is what empties this kind.</summary>
        PreContext,
        /// <summary>Level 0 is past <c>GL_MAX_TEXTURE_SIZE</c>; the engine downsamples to fit and Alloy leaves the
        /// cell to it deliberately (<see cref="Decline.Oversize"/>).</summary>
        Oversize,
        /// <summary>Alloy's upload did not happen and the engine's may still (<see cref="Decline.Refused"/>), or the
        /// stand-in for a shape it also fails on could not be made. The diagnostic line names which.</summary>
        Refused,
        /// <summary>The sheet's <c>numX*numY</c> is past <see cref="MaxCells"/>, so the content key cannot encode
        /// the cell and the whole sheet stays the engine's.</summary>
        Grid,
    }

    private const int BorrowKinds = (int)Borrow.Grid + 1;
    /// <summary>Loud lines about textures kept EMPTY, capped: a shape the engine fails on fails for every texture
    /// like it, and the first few name it as well as all of them would.</summary>
    private const int EmptyDiag = 3;
    private static readonly string[] BorrowNames = { "never funnelled", "stood down", "pre-context", "oversize", "refused", "grid too big" };

    /// <summary>The borrow ledger, plus the level-0 ceiling the oversize decline is measured against and whether
    /// the one pre-context refresh has run -- all of it about this context's names, so it goes with them.</summary>
    private sealed class BorrowState
    {
        /// <summary>Engine tid -> the shape that declined it, written at the decline edge and read once, when a
        /// draw first samples that tid. Erased with the tid's index entry.</summary>
        public readonly Dictionary<int, Borrow> Why = new();
        public readonly long[] Count = new long[BorrowKinds];
        public readonly bool[] Said = new bool[BorrowKinds];
        /// <summary><c>GL_MAX_TEXTURE_SIZE</c>, 0 until asked.</summary>
        public int MaxTex;
        /// <summary>Textures declined for want of a context. Non-zero means the refresh is owed.</summary>
        public int PreCtx;
        /// <summary>The refresh has run; it runs once per context.</summary>
        public bool Refreshed;
        /// <summary>Textures given a zeroed Alloy texture because the engine's own upload produces no pixels either.</summary>
        public long Empty;
    }
    private static BorrowState _borrow = new();

    /// <summary>Alloy will not upload this cell: remember which shape, so the draw that samples the engine's
    /// name can say so. Cold -- once per declined texture build.</summary>
    private static void Declined(int etid, Borrow why)
    {
        _borrow.Why[etid] = why;
        if (why == Borrow.PreContext) _borrow.PreCtx++;
    }

    /// <summary>A DISTINCT engine tid has just been drawn borrowed. Cold: <see cref="Draw"/> calls this only on
    /// the frame a tid first enters the borrowed set, and one line per shape says which it was.</summary>
    private static void CountBorrow(int etid)
    {
        int i = (int)(_borrow.Why.TryGetValue(etid, out Borrow w) ? w : Borrow.Funnel);
        _borrow.Count[i]++;
        if (_borrow.Said[i]) return;
        _borrow.Said[i] = true;
        _log?.Always($"OwnedTextures: tid {etid} drawn BORROWED -- {BorrowNames[i]}; every texture a frame samples is meant to be one Alloy uploaded (CX-339)");
    }

    /// <summary>A tid stops meaning its resource: its decline reason goes with its index entry, or a recycled
    /// name would be reported under the shape that declined a texture it no longer is.</summary>
    private static void ForgetBorrow(int tid) { _borrowed.Remove(tid); _borrow.Why.Remove(tid); }

    /// <summary>The borrow breakdown for the acceptance line: the non-zero shapes, or empty when nothing
    /// borrows.</summary>
    internal static string BorrowNote()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < BorrowKinds; i++)
        {
            if (_borrow.Count[i] == 0) continue;
            sb.Append(sb.Length == 0 ? " by shape: " : ", ").Append(BorrowNames[i]).Append(' ').Append(_borrow.Count[i]);
        }
        if (_borrow.Empty > 0) sb.Append(sb.Length == 0 ? " -- " : "; ").Append(_borrow.Empty).Append(" EMPTY Alloy textures (the engine's own upload produces no pixels for them either)");
        return sb.ToString();
    }

    /// <summary>The level-0 ceiling the oversize decline is measured against: the device's, asked once.</summary>
    private static int MaxTexSize()
    {
        if (_borrow.MaxTex == 0)
            return _log != null && AlloyDevice.Vulkan(_log) is CX.Graphics.Rhi.VkBackend vk ? _borrow.MaxTex = vk.Caps.MaxTextureSize : int.MaxValue;   // no device: the upload itself refuses
        return _borrow.MaxTex;
    }

    /// <summary>A texture the engine uploaded before the 4.6 context existed is borrowed for the session
    /// otherwise: nothing re-drives the funnel for one the engine already holds. So drive the engine's OWN
    /// refresh once (<c>AurTextureRefreshAll</c>, what it runs when the anisotropy option changes), whose rebuild
    /// comes back through the funnel with a context to upload into.</summary>
    private static void RefreshPreContext()
    {
        if (_borrow.PreCtx == 0 || _borrow.Refreshed || !_armed || !GlContextService.Available) return;
        if (TexStream.InTransition) return;   // a rebuild of EVERY texture does not belong inside an area load
        if (EngineFn.AurTextureRefreshAll == null)
        {
            _borrow.Refreshed = true;
            _log?.Always("OwnedTextures: AurTextureRefreshAll unresolved -- every texture the engine uploaded before the 4.6 context stays BORROWED for the session");
            return;
        }
        _borrow.Refreshed = true;
        _log?.Info($"OwnedTextures: re-driving AurTextureRefreshAll -- {_borrow.PreCtx} textures were uploaded before the 4.6 context existed and come back through the upload funnel now");
        EngineFn.AurTextureRefreshAll();
    }
}
