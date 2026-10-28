using System;
using System.Globalization;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Content-overridable GLSL: the Alloy pipeline asks for each of its shaders by
/// <c>cx_*</c> resref and gets whatever a hak, <c>override/</c> or NWSync supplies -- or null, which
/// means the caller builds from its embedded default. Cold by construction: a program builds once per
/// session, so the string this allocates never lands in a frame.</summary>
internal static unsafe class ShaderResource
{
    /// <summary>Resource type of a .shd, read off the client's own <c>CResHelper&lt;CResSHD, 2069&gt;</c>.</summary>
    private const ushort ResTypeShd = 2069;
    /// <summary>A CResRef is 16 bytes of null-padded ASCII; longer names cannot address a resource.</summary>
    internal const int ResRefBytes = 16;

    private static readonly IntPtr _resManSym = Native.Sym(SymbolMap.ExoResMan);

    /// <summary>The GLSL a content author supplied for <paramref name="resref"/>, or null when nothing in the
    /// resource chain provides it (the common case). <b>OUR resrefs only</b> -- <c>cx_*</c>: where no
    /// <c>CRes</c> exists this makes a BARE one, and the engine reads a <c>.shd</c> back as a <c>CResSHD</c>,
    /// so a resref the engine also loads kills the client at its next shader compile.</summary>
    internal static string? Override(string resref, ExtenderLog? log)
    {
        byte[]? b = Blob(resref, ResTypeShd, log);
        if (b == null) return null;
        if (ShaderBinary.IsModule(b))
        {
            log?.Info($"shader override: {resref}.shd is a SPIR-V module, not text -- "
                    + (SpirvArmed ? "the stage's own text still answers for its interface"
                                  : "IGNORED, set alloy.spirv=1 in cx.cfg to accept it"));
            return null;
        }
        // A BOM ahead of #version is a compile error, and an editor on Windows writes one without asking.
        string text = Encoding.UTF8.GetString(b).TrimStart('\uFEFF');
        log?.Info($"shader override: {resref}.shd from resman ({b.Length} bytes)");
        return text;
    }

    /// <summary>The raw SPIR-V a content author supplied for <paramref name="resref"/>, or null when the resource
    /// is absent, is GLSL text, or the <c>spirv</c> capability gate is off. What may be read off it is checked in
    /// <see cref="ShaderBinary.Validate"/>; the text machinery -- tokens, library splice, the stage contracts --
    /// reaches no module, so the author owns the interface it declares.</summary>
    internal static byte[]? OverrideSpirv(string resref, ExtenderLog? log)
    {
        if (!SpirvArmed) return null;
        byte[]? b = Blob(resref, ResTypeShd, log);
        return b != null && ShaderBinary.IsModule(b) ? b : null;
    }

    /// <summary>Restart gate: a binary stage is opted into, because none of the text machinery can audit one --
    /// only what <see cref="ShaderBinary.Validate"/> reads off the module itself.</summary>
    internal static bool SpirvArmed => Renderer.Spirv.Boot;

    /// <summary>The raw bytes behind <paramref name="resref"/> at <paramref name="type"/>, or null when nothing supplies
    /// it. Our own resrefs only (a <c>cx*</c> name the engine never loads): see <see cref="Override"/>. Main thread, cold.</summary>
    internal static byte[]? Blob(string resref, ushort type, ExtenderLog? log)
    {
        if (_resManSym == IntPtr.Zero || EngineFn.ResManExists == null || EngineFn.ResManGetResObject == null
            || EngineFn.ResManSetResObject == null || EngineFn.ResManDemand == null || EngineFn.ResManRelease == null
            || EngineFn.ResManReleaseObj == null || EngineFn.CResCtor == null || EngineFn.OperatorNew == null
            || resref.Length > ResRefBytes) return null;
        IntPtr resMan = *(IntPtr*)_resManSym;
        if (resMan == IntPtr.Zero) return null;

        byte* rr = stackalloc byte[ResRefBytes];
        for (int i = 0; i < ResRefBytes; i++) rr[i] = i < resref.Length ? (byte)resref[i] : (byte)0;
        if (EngineFn.ResManExists(resMan, rr, type, null) == 0) return null;   // size out-param is null-checked there

        IntPtr res = EngineFn.ResManGetResObject(resMan, rr, type);
        if (res == IntPtr.Zero)
        {
            // First ask for this resource: no CRes exists yet, so make one exactly as CResHelper does --
            // sizeof(CRes) is a SIZE, and there is no object to view until operator new has returned one.
            res = EngineFn.OperatorNew((nuint)EngineLayout.SizeOfCRes);
            if (res == IntPtr.Zero) return null;
            EngineFn.CResCtor(res);
            EngineFn.ResManSetResObject(resMan, rr, type, res);   // seeds the refcount at 1, like GetResObject's bump
        }
        EngineFn.ResManDemand(resMan, res);

        var cres = new CRes(res);
        byte* data = (byte*)cres.Data;
        int size = cres.Size;
        byte[]? bytes = null;
        if (data != null && size > 0) { bytes = new byte[size]; new Span<byte>(data, size).CopyTo(bytes); }

        EngineFn.ResManRelease(resMan, res);
        // Second arg is ReleaseResObject's "do not queue for freeing"; CResHelper::ClearResHelper passes
        // exactly this predicate -- GetData() == 0, i.e. nothing was ever serviced, so there is nothing to free.
        EngineFn.ResManReleaseObj(resMan, res, data == null ? (byte)1 : (byte)0);

        return bytes;
    }

    /// <summary>Expand the sizing tokens an Alloy pipeline source may carry, so a shipped .shd tracks the
    /// CPU-side constants rather than freezing a copy of them: lights per cluster, the cluster sort array,
    /// mover spheres per cube slot, cube slots, compute workgroup size, the engine's sRGB exponent, the SMAA
    /// preset and the hosted post chain's option mask and quality. Applied to every stage that compiles.</summary>
    internal static string Tokens(string src) => src
        .Replace("$CAP$", AlloyLights.ClusterCap.ToString())
        .Replace("$SORT$", AlloyLights.SortCap.ToString())
        .Replace("$DYNSPH$", CasterPass.DynSpheres.ToString())
        .Replace("$CUBES$", CasterPass.MaxCubes.ToString())
        .Replace("$GROUP$", AlloyLights.GroupSize.ToString())
        .Replace("$SRGB$", AlloyLights.SrgbGamma.ToString("0.0#", CultureInfo.InvariantCulture))
        .Replace("$AA$", SmaaStage.Level.ToString())
        .Replace("$POSTMASK$", FbPostStage.BuildMask.ToString())
        .Replace("$SQM$", FbPostStage.BuildQuality.ToString());
}
