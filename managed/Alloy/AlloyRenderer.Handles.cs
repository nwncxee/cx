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
    /// <summary>Resident bindless handle for a texture name. 0 in, 0 out. Called only inside the 4.6 bracket
    /// (handles belong to the share group) and, while ownedtex is armed, only for an Alloy-OWNED name. A
    /// handled texture is IMMUTABLE from this moment: a change means a new name, never an in-place
    /// <c>TexSubImage</c> from the engine context -- residency is per context and nothing propagates.</summary>
    internal static ulong HandleFor(int tid)
    {
        if (tid == 0) return 0;
        // a name that is not ours may already be dead or RECYCLED (Mesa hands the lowest free name straight
        // back): minting on it is a fault on zink and another texture's handle on radeonsi
        if (OwnedTextures.Armed && !OwnedTextures.IsOwned(tid)) return 0;
        if (Probe(tid, out int hit, out int free)) return _hVal[hit];
        if (free < 0) return 0;                        // table full: no handle, caller falls back
        ulong v = Gl.GetTextureHandleARB((uint)tid);
        if (Diag._handleDiagLeft > 0) HandleDiag(tid, v);
        // A failed mint is NOT cached. It used to be, and the enrollment wipe retried it within a second;
        // with that wipe gone (#220) a cached zero would be a permanently black texture.
        if (v == 0) return 0;
        Gl.MakeTextureHandleResidentARB(v);
        FrameZones.Count(FrameZones.Ev.Mint);
        _hTid[free] = tid; _hVal[free] = v; _hRes[free] = true;
        VkTextures.NoteHandle(v, tid);
        return v;
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

    /// <summary>This name's texture is about to be deleted, so its handle must stop being resident first: a
    /// resident handle outlives the object it names, and sampling one after the delete is corruption. Called
    /// by <c>OwnedTextures.Drain</c> inside the 4.6 bracket, only for a name that entry owns; the slot becomes
    /// a <see cref="HandleTombstone"/>. A no-op for a name that never got a handle, the common case.</summary>
    internal static void DropHandle(uint name) => DropHandle(name, gl: true);

    /// <summary>The texture died in the ENGINE's context before this bracket could run: the handle is gone
    /// with it, and asking the driver to make it non-resident is INVALID_OPERATION (Mesa) or worse. Tombstone only.</summary>
    internal static void ForgetHandle(uint name) => DropHandle(name, gl: false);

    internal static void DropHandle(uint name, bool gl)
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
                if (gl && _hRes[h] && _hVal[h] != 0 && Gl.MakeTextureHandleNonResidentARB != null)
                    Gl.MakeTextureHandleNonResidentARB(_hVal[h]);
                else if (!gl) BindlessLedger.Forget(name);
                ZeroHandle(_hVal[h]);
                _hTid[h] = HandleTombstone; _hVal[h] = 0; _hRes[h] = false;
                return;
            }
            h = (h + 1) & ((1 << HandleHashBits) - 1);
        }
    }

    private const int HandleDiagBudget = 24;
    /// <summary>Which GL call is failing on the handle path, and with what: the 4.6 bracket drains
    /// <c>glGetError</c> anonymously, so the two candidate faults are told apart by the CODE.
    /// <c>INVALID_OPERATION</c> after a non-zero handle is a re-resident of an already-resident handle;
    /// <c>INVALID_VALUE</c>, or a zero handle from a tid just resolved, is a dead or recycled name.</summary>
    private static void HandleDiag(int tid, ulong v)
    {
        uint err = 0, e;
        int guard = 0;
        while ((e = Gl.GetError()) != 0 && ++guard < 8) err = e;
        if (err == 0 && v != 0) return;   // the normal case; this path allocates nothing, so it stays silent
        Diag._handleDiagLeft--;
        _log?.Info($"AlloyRenderer: [handle] tid={tid} handle=0x{v:x} err=0x{err:x} " +
                   (v == 0 ? "-- glGetTextureHandleARB returned NO handle for a tid the material resolved this frame"
                           : "-- the handle came back but the call errored") +
                   $" ({Diag._handleDiagLeft} reports left)");
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
