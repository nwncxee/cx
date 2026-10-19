using System;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The engine's C++ classes as C# types: a <c>readonly ref struct</c> per class, so a pointer the engine may
/// free and recycle cannot be stashed in a field, an array or across an await -- the liveness scar is a
/// compile error, not a comment. A PROPERTY is a single load through the generated view; anything that walks a
/// vtable, calls native code or can fail is a METHOD with a verb. The tables still store raw <c>IntPtr</c>.
/// </summary>
internal readonly unsafe ref struct PartRef
{
    private readonly IntPtr _p;
    internal PartRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Part</c> base this ref reads through.</summary>
    internal Part View => new(_p);
    /// <summary>The <c>PartTriMesh</c> block: the LAYOUT view, for a part already known to be one. The
    /// engine's own subclass cast is <see cref="EngineLayout.PartAsTriMeshSlot"/>.</summary>
    internal PartTriMesh Tri => new(_p);

    internal MaterialRef Material => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Material.Ptr);
    internal GobRef Gob => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Gob.Ptr);
    /// <summary>The part's <c>VertexPrimitive</c>, which despite the name is what carries the model matrix
    /// and the bucket flags -- see <see cref="EngineLayout.PartNode"/> for why the name stays.</summary>
    internal NodeRef Node => new(_p == IntPtr.Zero ? IntPtr.Zero : Tri.VertexPrimitive.Ptr);
    /// <summary>The MATERIAL node at part+0x8, which is a different object from <see cref="Node"/> and
    /// carries the ambient/diffuse terms MatStatic sets.</summary>
    internal MdlRef Mdl => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Node.Ptr);
    internal IntPtr Tile => _p == IntPtr.Zero ? IntPtr.Zero : Tri.Tile.Ptr;

    internal float Alpha => Tri.Alpha;
    internal float* SelfIllum => (float*)Unsafe.AsPointer(ref Tri.SelfIllum);
    internal float* BboxMin => (float*)Unsafe.AsPointer(ref Tri.BboxMin);
    internal float* BboxMax => (float*)Unsafe.AsPointer(ref Tri.BboxMax);
    internal IntPtr Vtbl => *(IntPtr*)_p;

    /// <summary>Is this part an INSTANCE -- does it hang off a Gob? See <see cref="EngineLayout.PartGob"/>;
    /// this is NOT a skin test.</summary>
    internal bool IsInstance => !Gob.IsNull;
}

internal readonly unsafe ref struct MaterialRef
{
    private readonly IntPtr _p;
    internal MaterialRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Material</c> this ref reads through.</summary>
    internal Material View => new(_p);

    internal SharedRef Shared => new(_p == IntPtr.Zero ? IntPtr.Zero : View.Shared.Ptr);
    /// <summary>The material's own env map. Compared, never dereferenced -- a <c>CAurTexture*</c> is
    /// borrowed. See <see cref="EngineLayout.MatEnvMap"/>.</summary>
    internal IntPtr EnvMap => _p == IntPtr.Zero ? IntPtr.Zero : View.EnvMap.Ptr;
    internal int SrcBlend => View.SrcBlend;
    internal int DstBlend => View.DstBlend;
    /// <summary>A BYTE, not the int at the same offset: the only reader hashes it into a group key as a
    /// byte, and a widened load would fold three neighbouring bytes into that key.</summary>
    internal byte BlendFlag => View.BlendFlag;
    internal IntPtr Params => View.Params;
    internal int ParamCount => View.ParamCount;

    /// <summary>A numbered texture slot, live: <c>Material::GetTexture</c> @0x4c5810 as the two loads it is --
    /// the SharedMaterial's slot table when it holds one (<c>shared + 8 + (slot+13)*0x10</c>), else the
    /// material's own slot at <c>mat + slot*0x10</c>. The result is a BORROWED <c>CAurTexture*</c> that dies
    /// at the next area transition: re-fetched per frame, never cached. Null-safe on the receiver only.</summary>
    internal TextureRef Texture(int slot)
    {
        if (_p == IntPtr.Zero) return new(IntPtr.Zero);
        SharedMaterial shared = View.Shared;
        if (!shared.IsNull)
        {
            IntPtr over = shared.Slot(slot).Ptr;
            if (over != IntPtr.Zero) return new(over);
        }
        // the engine's fallback is unbounded: past the Material's own slots it hands back whatever field
        // sits at mat+slot*0x10 (crashes 1789305736, 1789311470 dereferenced it as a texture)
        if (slot >= OwnSlots) return new(IntPtr.Zero);
        return new(View.Slot(slot).Ptr);
    }
    /// <summary>Slots a plain <c>Material</c> owns itself (<c>BindAllStandardTextures</c> @0x4c6e30 stops at
    /// i &lt; 3); anything higher is real only in a SharedMaterial's table.</summary>
    private const int OwnSlots = 3;
    /// <summary>A <c>.mtr</c> slot by its index, engine (<c>texture0..10</c>) or extender-owned (11+).</summary>
    internal TextureRef TextureAt(int n)
        => n >= MaterialExtraSlots.First ? MaterialExtraSlots.Texture(Shared.Ptr, n) : Texture(n);
}

internal readonly unsafe ref struct SharedRef
{
    private readonly IntPtr _p;
    internal SharedRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>SharedMaterial</c> this ref reads through.</summary>
    internal SharedMaterial View => new(_p);

    internal bool Transparency => View.Transparency != 0;
    internal int SampleFb => View.SampleFb;
    internal bool Volumetric => View.Volumetric != 0;
    /// <summary>The <c>twosided</c> keyword -- draw this material's faces with culling OFF. See
    /// <see cref="EngineLayout.SharedTwoSided"/> for the engine's own three-sub-list spelling of it.</summary>
    internal bool TwoSided => View.TwoSided != 0;
    /// <summary>Is a <c>customShaderVS</c>/<c>FS</c> resref named at all? The first byte of the inline
    /// 17-byte buffer -- an empty VS name is the "pure texture redirect" test.</summary>
    internal bool HasVs => View.VsName != 0;
    internal bool HasFs => View.FsName != 0;
    internal byte* VsName => (byte*)Unsafe.AsPointer(ref View.VsName);
    internal byte* FsName => (byte*)Unsafe.AsPointer(ref View.FsName);
    internal IntPtr Params => View.Params;
    internal int ParamCount => View.ParamCount;
}

internal readonly unsafe ref struct GobRef
{
    private readonly IntPtr _p;
    internal GobRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>Gob</c> this ref reads through.</summary>
    internal Gob View => new(_p);

    internal IntPtr Node => View.Node.Ptr;
    internal IntPtr PartTree => View.PartTree.Ptr;
    internal float* Position => (float*)Unsafe.AsPointer(ref View.Position);

    internal bool Highlight => View.Highlight != 0;
    internal bool SceneTint => View.SceneTint != 0;
    internal float Alpha => View.Alpha;
    internal float FadeAlpha => View.FadeAlpha;
    internal float* Colour => (float*)Unsafe.AsPointer(ref View.Colour);
    internal float* Emissive => (float*)Unsafe.AsPointer(ref View.Emissive);
    internal IntPtr EnvOverride => View.EnvOverride;
}

internal readonly unsafe ref struct NodeRef
{
    private readonly IntPtr _p;
    internal NodeRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>VertexPrimitive</c> this ref reads through.</summary>
    internal VertexPrimitive View => new(_p);

    internal float* Matrix => (float*)Unsafe.AsPointer(ref View.Matrix);
    /// <summary>The same 16 floats as a value. The raw bytes already read as the transpose GLSL wants, so
    /// this is a copy and not a conversion -- every caller that wanted one was casting the pointer back.</summary>
    internal System.Numerics.Matrix4x4 Model => View.Matrix;
    internal byte Flags => View.Flags;
}

internal readonly unsafe ref struct MdlRef
{
    private readonly IntPtr _p;
    internal MdlRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>MdlNodeTriMesh</c> block this ref reads through -- the part's material node, already
    /// known to be a tri-mesh by the caller that fetched it.</summary>
    internal MdlNodeTriMesh View => new(_p);

    internal float* Ambient => (float*)Unsafe.AsPointer(ref View.Ambient);
    internal float* Diffuse => (float*)Unsafe.AsPointer(ref View.Diffuse);
}

internal readonly unsafe ref struct VdoRef
{
    private readonly IntPtr _p;
    internal VdoRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>VertexDataObject</c> this ref reads through.</summary>
    internal VertexDataObject View => new(_p);

    internal uint Vao => View.Vao;
    /// <summary>1 = indexed static, which is the only mode this renderer can replay.</summary>
    internal int Mode => View.Mode;
    internal long VertItems => (long)View.VertItems;
    internal int VertCount => View.VertCount;
    internal long ElemItems => (long)View.ElemItems;
    internal int ElemCount => View.ElemCount;

    /// <summary><c>Build</c> ran and gave this VDO an element stream. See
    /// <see cref="EngineLayout.VdoIndexed"/> for why the VAO drip refuses without it.</summary>
    internal bool Indexed => View.Indexed != 0;
    internal uint ElemToken => View.ElemToken;
    internal uint VertToken => View.VertToken;
    internal uint DynToken => View.DynToken;
    internal uint VertMask => View.VertMask;
    internal uint DynMask => View.DynMask;
}

/// <summary>Everything a VAO-cache slot's harvest depends on, read off the live VDO: the engine VAO, the mode,
/// the three pool tokens and the two attribute masks (which fix every stride and offset). Equal fingerprints
/// mean the harvested layout is still the truth -- a VDO freed and re-minted at the same address (operator new
/// recycles the 0x68-byte block, and VDOs get no death notice) draws as screen-spanning nonsense.</summary>
internal readonly record struct VdoSig(uint Vao, int Mode, uint Elem, uint Vert, uint Dyn, uint VertMask, uint DynMask)
{
    internal static VdoSig Of(IntPtr vdo)
    {
        var v = new VdoRef(vdo);
        return new VdoSig(v.Vao, v.Mode, v.ElemToken, v.VertToken, v.DynToken, v.VertMask, v.DynMask);
    }
}

/// <summary>A borrowed <c>CAurTexture*</c>. Every accessor is a METHOD: the name walks two vtable slots, and
/// the pointer itself is freed on area transitions and module unload -- there is no field read here that
/// could honestly be a property.</summary>
internal readonly unsafe ref struct TextureRef
{
    private readonly IntPtr _p;
    internal TextureRef(IntPtr p) => _p = p;
    internal IntPtr Ptr => _p;
    internal bool IsNull => _p == IntPtr.Zero;

    /// <summary>The <c>CAurTexture</c> this ref reads through -- the vtable slots the verbs below call.</summary>
    internal CAurTexture View => new(_p);

    /// <summary>The texture's name, through <c>GetReferenced</c> then <c>AsBasic</c>. Two vtable calls.</summary>
    internal string ResolveName() => AlloyCensus.TexName(_p);
    /// <summary>This texture's GL id, vtable-checked -- see AurTex: Material::GetTexture is a plain
    /// field read, so the pointer it hands back can outlive the object.</summary>
    internal int ResolveTid() => AurTex.Tid(_p);
}

/// <summary>The engine's own pure queries about a part or a material, taking the typed refs. The
/// <c>_orig*</c> trampolines deliberately do NOT move here -- those belong beside the hooks they chain.</summary>
internal static unsafe class Engine
{
    internal static bool HasEnvMap(MaterialRef mat) => PartTable._hasEnvMap(mat.Ptr) != 0;
}
