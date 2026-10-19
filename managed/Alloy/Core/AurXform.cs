using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The Aurora calls that pass or return a <c>Vector</c> BY VALUE, and the two argument lists each of them
/// needs: SysV classifies a 12-byte three-float struct into xmm0 (x, y) + xmm1 (z) with no hidden buffer,
/// while Win64 passes and returns one through a pointer. No single declared signature covers that, and the
/// generator's <c>sret</c> ordering does not either -- it assumes both platforms use a buffer.
/// </summary>
internal static unsafe class AurXform
{
    /// <summary>A three-float value classified the way SysV classifies it -- xmm0 carries x and y, xmm1
    /// carries z. <c>Vector3</c>'s ABI is the runtime's to choose, so the layout is declared here instead.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct V3 { public float X, Y, Z; }

    private static IntPtr _wind, _gust;

    /// <summary>Both symbols resolved on this client? A caller gates on this the way it gates on any other
    /// engine pointer.</summary>
    internal static bool Ready => _wind != IntPtr.Zero && _gust != IntPtr.Zero;

    internal static void Resolve()
    {
        _wind = Native.Sym(SymbolMap.SceneGetWindVector);
        _gust = Native.Sym(SymbolMap.AddPointSourceWind);
    }

    /// <summary>Names whichever of the two did not resolve, for the caller's one stand-down line.</summary>
    internal static string Missing => _wind == IntPtr.Zero ? SymbolMap.SceneGetWindVector : SymbolMap.AddPointSourceWind;

    /// <summary>The wind displacement to add to a particle stepped over <paramref name="dt"/>, sampled at
    /// <paramref name="pos"/>: <see cref="CX.Alloy.AlloyWind"/> answers it off its own clock and noise field,
    /// and <c>view.wind 0</c> sends it back to <c>Scene::GetWindVector</c> -- what the two per-OS declarations
    /// are for; the dangly dispatch samples the same field through the same pair.</summary>
    internal static V3 Wind(IntPtr scene, V3 pos, float dt)
    {
        if (CX.Alloy.AlloyWind.Owned)
        {
            System.Numerics.Vector3 w = CX.Alloy.AlloyWind.Wind(new System.Numerics.Vector3(pos.X, pos.Y, pos.Z), dt);
            return new V3 { X = w.X, Y = w.Y, Z = w.Z };
        }
        if (OperatingSystem.IsWindows())
        {
            V3 r = default;
            ((delegate* unmanaged<IntPtr, V3*, V3*, float, void>)_wind)(scene, &r, &pos, dt);
            return r;
        }
        return ((delegate* unmanaged<IntPtr, V3*, float, V3>)_wind)(scene, &pos, dt);
    }

    /// <summary>A detonation's gust: <c>WindManager::AddPointSourceWind(pos, radius, life, strength)</c>, with
    /// strength 1.0 exactly as <c>ExplosionEmitter::Update</c> @0x4b259e pins it; a moving gob's gust passes
    /// <see cref="EngineLayout.GobWindStrength"/>. <paramref name="windManager"/> is
    /// <see cref="EngineLayout.SceneWindManager"/>'s pointer, and <c>AlloyWind</c> hooks the call too.</summary>
    internal static void Gust(IntPtr windManager, V3 pos, float radius, float life, float strength = 1f)
    {
        if (OperatingSystem.IsWindows())
            ((delegate* unmanaged<IntPtr, V3*, float, float, float, void>)_gust)(windManager, &pos, radius, life, strength);
        else
            ((delegate* unmanaged<V3, IntPtr, float, float, float, void>)_gust)(pos, windManager, radius, life, strength);
    }
}
