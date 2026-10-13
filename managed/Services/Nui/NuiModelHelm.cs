using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The doll's own helmet. A creature's appearance record names its helmet by the object id of an item, and the engine builds the head from
/// that item: it hides the head and attaches the item's model in its place. A doll that copied the record would attach the target's own
/// helmet model, taking it off the target and leaving the doll bare-headed whenever that model was not ready. So the doll
/// gets a throwaway copy of the helmet with its own model, registered like any object, and its record names the copy.
/// </summary>
internal static unsafe class NuiModelHelm
{
    private const uint InvalidId = 0x7f000000;   // not-an-offset: INVALID_OBJECT_ID

    /// <summary>The helmet copy a doll wears (its item and registered id), the real helmet's id it was made from, and the copy a change
    /// replaced, which is freed once the engine has detached its model. <see cref="Last"/> is the record the doll was last built from.</summary>
    internal sealed class HelmState
    {
        public IntPtr Item, Replaced;
        public uint Id = InvalidId, ReplacedId = InvalidId, From = InvalidId;
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
        if (real == InvalidId) { Drop(h); info.HelmId = InvalidId; return; }
        if (h.Item != IntPtr.Zero && h.From == real && NuiModelDoll.Holds(h.Id, h.Item + ItemObject)) { info.HelmId = h.Id; return; }
        Drop(h);
        info.HelmId = Make(h, real) ? h.Id : InvalidId;
    }

    /// <summary>The engine has rebuilt the head: the copy it replaced has no model attached any more, so it goes.</summary>
    internal static void Settle(HelmState h)
    {
        if (h.Replaced == IntPtr.Zero) return;
        Free(h.Replaced, h.ReplacedId);
        h.Replaced = IntPtr.Zero;
        h.ReplacedId = InvalidId;
    }

    /// <summary>The doll is going: its head is rebuilt bare so the engine lets go of the helmet model, then the copy is freed. When the engine
    /// already freed every registered object (<paramref name="taken"/>) only the bookkeeping is dropped.</summary>
    internal static void Release(HelmState h, IntPtr doll, bool taken)
    {
        if (!taken)
        {
            if (h.Item != IntPtr.Zero && doll != IntPtr.Zero && h.Last.Length >= EngineLayout.SizeOfAppearanceInfo)
            {
                byte* info = stackalloc byte[256];   // not-an-offset: room for either OS's CCAppearanceInfo
                int n = EngineLayout.SizeOfAppearanceInfo;
                h.Last.AsSpan(0, n).CopyTo(new Span<byte>(info, n));
                new CCAppearanceInfo((IntPtr)info).HelmId = InvalidId;
                Drop(h);
                EngineFn.NwcCreatureSetAppearance(doll, (IntPtr)info, FullBuild);
            }
            Settle(h);
            if (h.Item != IntPtr.Zero) Free(h.Item, h.Id);
        }
        h.Item = h.Replaced = IntPtr.Zero;
        h.Id = h.ReplacedId = h.From = InvalidId;
    }

    private const byte FullBuild = 0x2f;   // not-an-offset: SetAppearance flag bits, as NuiModelDoll builds
    private const int ItemObject = 0x48;   // not-an-offset: the CNWCObject inside a CNWCItem

    /// <summary>The current copy becomes the replaced one, to be freed by <see cref="Settle"/>.</summary>
    private static void Drop(HelmState h)
    {
        if (h.Item == IntPtr.Zero) return;
        if (h.Replaced != IntPtr.Zero) Free(h.Replaced, h.ReplacedId);
        h.Replaced = h.Item;
        h.ReplacedId = h.Id;
        h.Item = IntPtr.Zero;
        h.Id = h.From = InvalidId;
    }

    private static bool Make(HelmState h, uint real)
    {
        IntPtr src = NuiModelGear.Known(real);
        IntPtr copy = NuiItemIcons.MintCopy(src);
        if (copy == IntPtr.Zero) return false;
        if (NuiModelTarget.LoadItem(copy, NuiModelTarget.ModelSlot) == IntPtr.Zero) { NuiItemIcons.Destroy(copy); return false; }
        IntPtr array = EngineFn.GetGameObjectArray(NuiModelDoll.App());
        uint id = InvalidId;
        if (array == IntPtr.Zero || EngineFn.GameObjectArrayAdd(array, &id, copy + ItemObject, 0) != 0) { NuiItemIcons.Destroy(copy); return false; }
        new CNWCObject(copy + ItemObject).Id = id;
        h.Item = copy;
        h.Id = id;
        h.From = real;
        return true;
    }

    private static void Free(IntPtr item, uint id)
    {
        IntPtr app = NuiModelDoll.App();
        if (app != IntPtr.Zero && NuiModelDoll.Holds(id, item + ItemObject))
        {
            IntPtr gone;
            EngineFn.GameObjectArrayDelete(EngineFn.GetGameObjectArray(app), id, &gone);
        }
        NuiItemIcons.Destroy(item);
    }
}
