using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>Footsteps on rain-wet ground sound wet. Stock footstepsounds.2da carries a <c>Puddles0..2</c> sound
/// no stock step can reach: <c>PlayFootstep</c> builds its column name from surfacemat.2da's <c>Name</c> plus a
/// digit and the Puddles row is named <c>Puddle</c>, so <c>Puddle0</c> names no column. Here a footfall on wet,
/// rain-open ground plays that sound instead, resolved the way <c>PlayFootstep</c> resolves its row.</summary>
internal static unsafe partial class Weather
{
    /// <summary>surfacemat.2da rows that sound dry and lie open to the weather: Dirt 1, Grass 3, Stone 4, Wood 5,
    /// Metal 10, Leaves 14, Sand 20, StoneBridge 22. ponytail: stock row ids; a server that renumbers surfacemat.2da
    /// gets the dry sound on the moved rows, read the <c>Label</c> column if one ever does.</summary>
    private const ulong DrySurfaces = (1UL << 1) | (1UL << 3) | (1UL << 4) | (1UL << 5) | (1UL << 10) | (1UL << 14) | (1UL << 20) | (1UL << 22);
    /// <summary>Wetness from which a step sounds wet: the ground reads wet well before it pools.</summary>
    private const float WetStepMin = 0.35f;
    /// <summary>Metres above the foot the shelter ray starts (over a kerb or a doorstep) and how far it reaches.</summary>
    private const float ShelterLiftM = 0.5f, ShelterRayM = 40f;
    /// <summary>Creatures and items in the way are cast past, as the foot placement does, at most this often.</summary>
    private const int ShelterCasts = 6;
    /// <summary><c>PlayFootstep</c>'s priority groups: the player's, then a non-player's by size category.</summary>
    private const byte GroupPlayer = 0x11, GroupSmall = 0x12, GroupLarge = 0x10;   // not-an-offset: CExoSound priority group ids
    /// <summary>A CExoString: <c>{char* buf; uint len; uint cap}</c>. not-an-offset: a caller-owned value's size.</summary>
    private const int ExoSize = 16;
    /// <summary>A CResRef is sixteen chars, not terminated when full. not-an-offset: its size.</summary>
    private const int ResRefSize = 16;

    internal static Knob<bool> WetSteps = null!;

    private static readonly IntPtr _soundSym = Native.Sym(SymbolMap.ExoSound), _rulesSym2 = Native.Sym(SymbolMap.Rules);
    private static IntPtr _colType, _colSize;
    private static readonly IntPtr[] _colPud = new IntPtr[3];
    private static bool _wetArmed;
    private static int _wetSteps, _sheltered;

    private static void DeclareWetStepKnob()
        => WetSteps = G.Toggle("wetsteps", "Wet footstep sounds", true, Tier.Advanced,
                               help: "Steps on rain-wet ground make the game's own puddle sound for that creature, until the ground dries. Not under a roof.");

    /// <summary>Resolve what the wet step calls and build its column names once. Missing anything = the steps stay
    /// the engine's, said once.</summary>
    private static void ArmWetStep(ExtenderLog log)
    {
        if (!SymbolMap.Require(log, "Weather.WetStep", SymbolMap.C2DAGetCExoStringEntry, SymbolMap.C2DAGetINTEntry,
                               SymbolMap.AreaGetSurfaceMaterial, SymbolMap.SoundPlay3DOneShot, SymbolMap.ExoSound,
                               SymbolMap.Rules, SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor)
            || !EngineLayout.Require(log, "Weather.WetStep", "CTwoDimArrays", "CPathfindInformation")
            || EngineFn.ExoStringInit == null)
            return;
        _colType = Column("FootstepType"u8);
        _colSize = Column("SIZECATEGORY"u8);
        _colPud[0] = Column("Puddles0"u8); _colPud[1] = Column("Puddles1"u8); _colPud[2] = Column("Puddles2"u8);
        _wetArmed = true;
        static IntPtr Column(ReadOnlySpan<byte> name)
        {
            IntPtr c = (IntPtr)NativeMemory.AllocZeroed(ExoSize);
            fixed (byte* n = name) EngineFn.ExoStringInit(c, n, uint.MaxValue);
            return c;
        }
    }

    /// <summary>A footfall, from <see cref="Services.Anim.Footfall"/>'s <c>PlayFootstep</c> forward: the print and the
    /// splash, and the wet sound. True = the wet sound played, so the caller skips the engine's dry one. Cold: a
    /// few per second per walking creature.</summary>
    internal static bool Footstep(IntPtr creature)
    {
        if (_off || !Enabled.V || FrameInputs.Interior || creature == IntPtr.Zero) return false;
        var o = new CNWCObject(creature);
        Vector3 p = o.Position, f = o.Orientation;
        bool wet = WetShown > FootMinWet && (FootSplash.V > 0f || WetSteps.V);
        bool open = wet && Open(p);
        if (wet && !open) _sheltered++;
        FootDown(p, new Vector2(f.X, f.Y), o.Id, TrackNow, open);
        return open && WetSteps.V && WetShown > WetStepMin && WetSound(creature, o, p);
    }

    /// <summary>Can the rain reach <paramref name="p"/>? One ray back up the cover bake's direction; a tile, a
    /// static or a placeable on it is a roof, anything else (a creature, a held item) is cast past.</summary>
    private static bool Open(Vector3 p)
    {
        IntPtr scene = SceneRay.Armed ? SceneRay.WorldScene() : IntPtr.Zero;
        if (scene == IntPtr.Zero) return true;
        Vector3 up = -CoverDir;
        if (up.Z <= 0f) up = Vector3.UnitZ;
        Vector3 from = p + new Vector3(0f, 0f, ShelterLiftM), to = from + up * ShelterRayM;
        for (int i = 0; i < ShelterCasts; i++)
        {
            if (!SceneRay.Cast(scene, from, to, IntPtr.Zero, out Vector3 hit, out IntPtr g, statics: true)) break;
            if (g == IntPtr.Zero || SceneRay.IsTile(g) || SceneRay.IsPlaceable(g)) { SceneRay.Release(); return false; }
            from = hit + up * SceneRay.Nudge;
        }
        SceneRay.Release();
        return true;
    }

    /// <summary>Play this creature's <c>Puddles</c> step, as <c>PlayFootstep</c> would play its surface's. False =
    /// not a dry-sounding surface, or nothing to play: the engine's own step goes ahead.</summary>
    private static bool WetSound(IntPtr creature, CNWCObject o, Vector3 p)
    {
        if (!_wetArmed || o.FootstepSkip != 0 || o.AreaPtr == IntPtr.Zero) return false;
        IntPtr rules = _rulesSym2 == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_rulesSym2;
        IntPtr sound = _soundSym == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_soundSym;
        IntPtr tables = rules == IntPtr.Zero ? IntPtr.Zero : new CNWRules(rules).TwoDimArrays;
        if (sound == IntPtr.Zero || tables == IntPtr.Zero) return false;
        var t = new CTwoDimArrays(tables);
        Vec3 at = new() { X = p.X, Y = p.Y, Z = p.Z };
        int surface = EngineFn.AreaGetSurfaceMaterial(o.AreaPtr, VectorArg.Of(&at));
        if ((uint)surface >= 64 || (DrySurfaces & (1UL << surface)) == 0) return false;
        var c = new CNWCCreature(creature);
        CPathfindInformation pf = c.Pathfind;
        int app = pf.IsNull ? -1 : pf.Appearance;
        int row = c.FootstepType;
        if (row == -1 && (app < 0 || EngineFn.C2DAGetINTEntry(t.Appearance, app, _colType, &row) == 0)) return false;
        if (c.SoftStep == 1) row = 3;
        byte* res = stackalloc byte[ResRefSize];
        if (!Cell(t.FootstepSounds, row, _colPud[Random.Shared.Next(3)], res)) return false;
        byte group = GroupPlayer;
        if (creature != PlayerIdentity.Creature())
        {
            int size = 0;
            if (app >= 0) EngineFn.C2DAGetINTEntry(t.Appearance, app, _colSize, &size);
            group = (uint)(size - 1) < 3 ? GroupSmall : GroupLarge;
        }
        EngineFn.SoundPlay3DOneShot(sound, res, VectorArg.Of(&at), 0f, group, 0, 0);
        _wetSteps++;
        return true;
    }

    /// <summary>A footstepsounds.2da cell as a CResRef in <paramref name="res"/>; false when it is empty.</summary>
    private static bool Cell(IntPtr twoda, int row, IntPtr column, byte* res)
    {
        if (twoda == IntPtr.Zero) return false;
        byte* exo = stackalloc byte[ExoSize];
        NativeMemory.Clear(exo, (nuint)ExoSize);   // the engine frees a non-null buffer first
        bool ok = false;
        if (EngineFn.C2DAGetCExoStringEntry(twoda, row, column, (IntPtr)exo) != 0)
        {
            byte* s = *(byte**)exo;
            int n = 0;
            for (; s != null && n < ResRefSize && s[n] != 0; n++) res[n] = (byte)char.ToLowerInvariant((char)s[n]);
            for (int i = n; i < ResRefSize; i++) res[i] = 0;
            ok = n > 0;
        }
        EngineFn.ExoStringDtor((IntPtr)exo);
        return ok;
    }

    private static string WetStepStats() => $", {_wetSteps} wet steps, {_sheltered} sheltered";
}
