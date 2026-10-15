using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The doll's own helmet. A creature's appearance record names its helmet by the object id of an item, and the engine builds the head from
/// that item: it hides the head and attaches the item's model in its place. The doll wears a throwaway copy of the helmet, registered like
/// any object and with a model of its own, and its record names the copy, so the engine attaches the copy's model, never the target's.
/// </summary>
internal static unsafe class NuiModelHelm
{

    /// <summary>The helmet copy a doll wears (its item and registered id), the real helmet's id it was made from, and the copy a change
    /// replaced, which is freed once the engine has detached its model. <see cref="Last"/> is the record the doll was last built from.</summary>
    internal sealed class HelmState
    {
        public IntPtr Item, Replaced;
        public uint Id = NuiModelDoll.InvalidId, ReplacedId = NuiModelDoll.InvalidId, From = NuiModelDoll.InvalidId;
        public byte[] Last = Array.Empty<byte>();
    }

    /// <summary>Make <paramref name="record"/>'s helmet the doll's own: the target's helmet id becomes the id of a copy (kept while the target's
    /// helmet is the same), or the invalid id when it has none or no copy can be made. Call before the engine builds the head, and
    /// <see cref="Settle"/> after.</summary>
    internal static void Swap(HelmState h, byte* record, int length)
    {
        h.Last = new ReadOnlySpan<byte>(record, length).ToArray();
        var info = new CCAppearanceInfo((IntPtr)record);
        uint real = info.HelmId;
        if (real == NuiModelDoll.InvalidId) { Drop(h); info.HelmId = NuiModelDoll.InvalidId; return; }
        if (h.Item != IntPtr.Zero && h.From == real && NuiModelDoll.Holds(h.Id, new CNWCItem(h.Item).AsObject.Ptr)) { info.HelmId = h.Id; return; }
        Drop(h);
        info.HelmId = Make(h, real) ? h.Id : NuiModelDoll.InvalidId;
    }

    /// <summary>The engine has rebuilt the head: the copy it replaced has no model attached any more, so it goes.</summary>
    internal static void Settle(HelmState h)
    {
        if (h.Replaced == IntPtr.Zero) return;
        NuiModelGear.Unregister(h.Replaced, h.ReplacedId);
        h.Replaced = IntPtr.Zero;
        h.ReplacedId = NuiModelDoll.InvalidId;
    }

    /// <summary>The doll is going: its head is rebuilt bare so the engine lets go of the helmet model, then the copy is freed. When the engine
    /// already freed every registered object (<paramref name="taken"/>) only the bookkeeping is dropped.</summary>
    internal static void Release(HelmState h, IntPtr doll, bool taken)
    {
        if (!taken)
        {
            if (h.Item != IntPtr.Zero && doll != IntPtr.Zero && h.Last.Length >= EngineLayout.SizeOfAppearanceInfo)
            {
                byte* info = stackalloc byte[NuiModelDoll.RecordMax];
                int n = EngineLayout.SizeOfAppearanceInfo;
                h.Last.AsSpan(0, n).CopyTo(new Span<byte>(info, n));
                new CCAppearanceInfo((IntPtr)info).HelmId = NuiModelDoll.InvalidId;
                Drop(h);
                EngineFn.NwcCreatureSetAppearance(doll, (IntPtr)info, NuiModelDoll.BuildFlags);
            }
            Settle(h);
            if (h.Item != IntPtr.Zero) NuiModelGear.Unregister(h.Item, h.Id);
        }
        h.Item = h.Replaced = IntPtr.Zero;
        h.Id = h.ReplacedId = h.From = NuiModelDoll.InvalidId;
    }

    /// <summary>The current copy becomes the replaced one, to be freed by <see cref="Settle"/>.</summary>
    private static void Drop(HelmState h)
    {
        if (h.Item == IntPtr.Zero) return;
        if (h.Replaced != IntPtr.Zero) NuiModelGear.Unregister(h.Replaced, h.ReplacedId);
        h.Replaced = h.Item;
        h.ReplacedId = h.Id;
        h.Item = IntPtr.Zero;
        h.Id = h.From = NuiModelDoll.InvalidId;
    }

    private static bool Make(HelmState h, uint real)
    {
        IntPtr src = NuiModelGear.Known(real);
        IntPtr copy = NuiItemIcons.MintCopy(src);
        if (copy == IntPtr.Zero) return false;
        if (NuiModelTarget.LoadItem(copy, NuiModelTarget.ModelSlot) == IntPtr.Zero)
        {
            NuiModelGear.Say("NuiModelHelm: the helmet has no model the client can load -- the doll goes without one");
            NuiItemIcons.Destroy(copy);
            return false;
        }
        uint id = NuiModelGear.Register(copy);
        if (id == NuiModelDoll.InvalidId) return false;
        h.Item = copy;
        h.Id = id;
        h.From = real;
        return true;
    }
}
