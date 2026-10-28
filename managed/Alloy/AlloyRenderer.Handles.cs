using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Bindless texture handles: one interned handle per owned texture id, the tombstone a dropped resource
/// leaves behind, and the fragment-stage rewrite that turns bound samplers into per-draw handle reads.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The read-only half of <see cref="HandleFor"/>: the handle if one was minted, 0 otherwise. Safe
    /// off the main thread as long as nothing mints during the stage, which nothing does.</summary>
    private static ulong HandleLookup(int tid)
        => tid != 0 && Probe(tid, out int hit, out _) ? _hVal[hit] : 0;
    /// <summary>Resident bindless handle for a texture name. 0 in, 0 out. Called only from the frame's own
    /// recording and, while ownedtex is armed, only for an Alloy-OWNED name. A handled texture is IMMUTABLE
    /// from this moment: a change means a new name, never an in-place update of the image behind it.</summary>
    internal static ulong HandleFor(int tid)
    {
        if (tid == 0) return 0;
        // a name that is not ours may already be dead or RECYCLED (Mesa hands the lowest free name straight
        // back): minting on it hands back another texture's handle, or faults outright
        if (OwnedTextures.Armed && !OwnedTextures.IsOwned(tid)) return 0;
        if (Probe(tid, out int hit, out int free)) return _hVal[hit];
        if (free < 0) return 0;                        // table full: no handle, caller falls back
        ulong vk = VkTextures.HandleOf((uint)tid);
        _hTid[free] = tid; _hVal[free] = vk;
        return vk;
    }

    /// <summary>Walk the open-addressed chain for <paramref name="tid"/>. True with <paramref name="hit"/> set
    /// when the name is cached; false with <paramref name="free"/> set to the slot an insert should take (-1
    /// only if the table is full). No GL in it, so <see cref="SelfCheckHandles"/> can exercise it. The rule
    /// the tombstone exists for: it must not answer for its name, must stay reusable, must not end the chain.</summary>
    private static bool Probe(int tid, out int hit, out int free)
    {
        int mask = (1 << HandleHashBits) - 1;
        int h = Slot(tid);
        hit = -1; free = -1;
        for (int probes = 0; probes <= mask; probes++, h = (h + 1) & mask)
        {
            int k = _hTid[h];
            if (k == HandleTombstone) { if (free < 0) free = h; continue; }
            if (k == tid) { hit = h; return true; }
            if (k == 0) { if (free < 0) free = h; return false; }
        }
        return false;
    }

    /// <summary>Off-device: the handle cache's slot discipline, with no GL. A <see cref="DropHandle"/> that
    /// leaves the slot keyed on the DELETED name with a zero handle makes the next copy -- which GL handed
    /// that very name to -- match the dead entry and draw black. This asserts that cannot happen.</summary>
    internal static bool SelfCheckHandles(ExtenderLog log)
    {
        int mask = (1 << HandleHashBits) - 1;
        const int A = 4242;
        int home = Slot(A);
        int s0 = _hTid[home], s1 = _hTid[(home + 1) & mask], s2 = _hTid[(home + 2) & mask];
        ulong v0 = _hVal[home];
        _hTid[home] = 0; _hTid[(home + 1) & mask] = 0; _hTid[(home + 2) & mask] = 0;

        bool ok = !Probe(A, out _, out int free) && free == home;      // empty: insert at home
        _hTid[home] = A; _hVal[home] = 7;
        ok &= Probe(A, out int hit, out _) && hit == home && _hVal[hit] == 7;   // cached: found

        // ...the texture dies. DropHandle's slot state, asserted directly rather than through GL.
        _hTid[home] = HandleTombstone; _hVal[home] = 0;
        ok &= !Probe(A, out _, out free);            // the dead entry must NOT answer for its own name...
        ok &= free == home;                          // ...and its slot is the one an insert reuses
        // A tombstone must not END the chain: A itself, displaced to the next slot by an old collision,
        // has to stay reachable through the tombstone rather than reading as absent.
        _hTid[(home + 1) & mask] = A; _hVal[(home + 1) & mask] = 9;
        ok &= Probe(A, out hit, out _) && hit == ((home + 1) & mask) && _hVal[hit] == 9;

        _hTid[home] = s0; _hVal[home] = v0;
        _hTid[(home + 1) & mask] = s1; _hTid[(home + 2) & mask] = s2;
        log.Info(ok ? "AlloyRenderer.SelfCheckHandles: passed" : "AlloyRenderer.SelfCheckHandles: FAILED");
        return ok;
    }

    /// <summary>A slot whose texture has been deleted. Not 0 -- that would end the open-addressed probe chain
    /// and hide every entry behind it -- and not the name either: GL recycles a deleted name immediately, so a
    /// slot still keyed on it answers the NEXT copy with the dead entry's zero handle. No GL texture is ever
    /// named -1.</summary>
    private const int HandleTombstone = -1;

    private static int Slot(int tid) =>
        (int)((uint)tid * 0x9E3779B9u >> (32 - HandleHashBits)) & ((1 << HandleHashBits) - 1);

    /// <summary>This name's texture died: the slot it cached becomes a <see cref="HandleTombstone"/> and the
    /// records holding it are marked. A no-op for a name that never got a handle, the common case.</summary>
    internal static void DropHandle(uint name)
    {
        if (name == 0) return;
        // #242: a persistent record carries its handles (and, bindless off, its cached tid) across frames, so a
        // dying name must reach the records that hold it. That used to be an epoch bump here; under the texture
        // cache names die every second or two, so it is now the per-slot mark NoteOwnedNamesRetired makes in
        // Drain before this runs.
        if (!_bindless) return;
        int tid = (int)name, h = Slot(tid);
        for (int probes = 0; probes < (1 << HandleHashBits); probes++)
        {
            int k = _hTid[h];
            if (k == 0) return;                       // never cached: nothing to drop
            if (k == tid)
            {
                ZeroHandle(_hVal[h]);
                _hTid[h] = HandleTombstone; _hVal[h] = 0;
                return;
            }
            h = (h + 1) & ((1 << HandleHashBits) - 1);
        }
    }

    /// <summary>Rewrite a fragment stage's bound samplers into per-draw bindless handles. The stage already
    /// receives <c>vDraw</c> (the record index) for its material colours; the same index now selects its
    /// textures, which lets one multidraw span every texture in a class. Pure text: where the declarations
    /// are not found the output lacks the handle block and the caller stands the bindless path down.</summary>
    internal static string BindlessFs(string fs)
    {
        string[] names = { "uTex", "uFlowTex", "uNrmTex", "uSpecTex", "uRoughTex", "uHeightTex", "uIllumTex", "uAoTex" };
        int[] slot = { 0, 6, 1, 2, 3, 4, 5, 7 };
        var sb = new System.Text.StringBuilder(
            "#define CX_BINDLESS 1\n#extension GL_ARB_bindless_texture : require\n" +
            $"layout(std430, binding = {BindTexH}) readonly buffer CxTexH {{ uvec2 uTexH[]; }};\n");
        for (int i = 0; i < names.Length; i++)
            sb.Append($"#define {names[i]} sampler2D(uTexH[(vDraw << 3) + {slot[i]}])\n");
        string outFs = fs;
        foreach (string nm in names)
            outFs = System.Text.RegularExpressions.Regex.Replace(outFs, $@"uniform sampler2D {nm};[^\n]*\n", "");
        if (outFs == fs) return fs;   // nothing replaced -- foreign text, leave it alone
        if (!outFs.Contains("flat in int vDraw;")) return fs;   // no record index to sample by
        // GLSL: #extension must precede every non-preprocessor token, so the whole block goes immediately
        // after #version. The #defines are preprocessor (position-free) and only reference vDraw at their
        // EXPANSION sites, which sit far below its declaration; the SSBO decl merely has to precede use.
        int at = outFs.IndexOf('\n', outFs.IndexOf("#version", StringComparison.Ordinal)) + 1;
        return outFs.Insert(at, sb.ToString());
    }
}
