using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
namespace CX.Alloy;

/// <summary>The engine reads the frame still makes: the keyhole aperture's box test (the arithmetic of
/// <c>Camera::GetCanKeyholeDissolve</c>, which <c>DbKeyhole</c> runs over the database's own boxes),
/// <c>g_nEnableBumpyWater</c>, and the material/part accessors resolved once at <see cref="Resolve"/>.</summary>
internal static unsafe class PartTable
{
    internal static PartGetIsOpaqueFn _isOpaque;
    internal static MatHasBumpyShinyMapFn _hasBumpyShiny;
    internal static MatGetTextureFn _getTexture;
    internal static uint* _primTable;
    /// <summary><c>g_nEnableBumpyWater</c>: null (Windows, or a build without the symbol) leaves the
    /// bumpy-shiny route to water unrecognised, so only .mtr-declared water is owned. Never a misrender:
    /// an unrecognised water part stays the engine's.</summary>
    internal static int* _bumpyWater;
    /// <summary>Is the engine's bumpy-shiny route into the water class live this session? The gate
    /// <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 reads, so both scene paths classify water on one
    /// term; false where the symbol did not resolve, which leaves only <c>.mtr</c>-declared water.</summary>
    internal static bool BumpyWaterOn => _bumpyWater != null && *_bumpyWater != 0;
    /// <summary>Resolve every engine read the three modules make off a part, a node or a material. One
    /// place, because all three call them and none may resolve a symbol of its own. False = a required one
    /// is missing (headless, or Windows), and nothing above may arm.</summary>
    internal static bool Resolve()
    {
        // Resolved ONCE into a static here rather than through EngineFn at the use site: these are asked
        // per part per frame, and a lazy compare in that loop is the cost the declaration removes.
        _isOpaque = EngineFn.PartGetIsOpaque;
        _hasBumpyShiny = EngineFn.MatHasBumpyShinyMap;
        _getTexture = EngineFn.MatGetTexture;
        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        _bumpyWater = (int*)Native.Sym(SymbolMap.EnableBumpyWater);
        return _isOpaque != null && _hasBumpyShiny != null && _getTexture != null && _primTable != null;
    }
    /// <summary>The engine's verdict reproduced from a cached box. <c>Camera::GetCanKeyholeDissolve</c>
    /// @0x4a6f70 requires a gobless part above the cut and <c>BoxAbovePlane</c> @0x5b32d0 negative;
    /// <c>BoxAbovePlane</c> evaluates the plane at the box corner that minimises it (each axis takes min when
    /// its coefficient is positive) as <c>y*b + a*x + c*z + d</c>, in that order, in floats.</summary>
    internal static bool KeyholeDissolvesBox(in Box box, byte keyholeOn, Vector4 plane, float cut)
    {
        if (keyholeOn == 0 || !(box.Max.Z > cut)) return false;
        float a = plane.X, b = plane.Y, c = plane.Z, d = plane.W;
        float x = a > 0f ? box.Min.X : box.Max.X;
        float y = b > 0f ? box.Min.Y : box.Max.Y;
        float z = c > 0f ? box.Min.Z : box.Max.Z;
        return y * b + a * x + c * z + d < 0f;
    }
}
