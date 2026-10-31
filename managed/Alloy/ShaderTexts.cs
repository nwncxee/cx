using CX.Framework;

namespace CX.Alloy;

/// <summary>The text a stage is built from, once the resource chain has had its say: a content
/// <c>&lt;resref&gt;.shd</c> override, the library overrides inside whichever text won, and the embedded
/// default back again where an override would read a uniform block's terms as loose uniforms. No compiler
/// and no device -- whoever compiles the text takes it from here.</summary>
internal static class ShaderTexts
{
    /// <summary><see cref="ShaderSource.Content"/>, refused for the embedded libraries when the swap leaves the
    /// stage reading a block's terms as loose uniforms (<see cref="ShaderContract.BlocksSafe"/>).</summary>
    internal static string Lib(string src, ExtenderLog? log, string what)
    {
        string c = ShaderSource.Content(src, log);
        if (ReferenceEquals(c, src) || ShaderContract.BlocksSafe(src, c)) return c;
        log?.Info($"{what}: a library override .shd reads a uniform block's terms outside the block -- using the embedded libraries");
        return src;
    }

    /// <summary>An overridable pair's texts: the stage override first, then the libraries inside whichever text
    /// won, back to the embedded stages when either reads a uniform block's terms outside the block
    /// (<see cref="ShaderContract.BlocksSafe"/>). True when the texts handed back are the ones given: no override reached either, or one did and was refused.</summary>
    internal static bool Pair(string vsRef, string vs, string fsRef, string fs, ExtenderLog? log, string what,
                              out string vsSource, out string fsSource)
    {
        // a dumped stage keeps its brackets, so a library override still reaches an overridden stage
        string? ovs = ShaderResource.Override(vsRef, log), ofs = ShaderResource.Override(fsRef, log);
        vsSource = ShaderSource.Content(ovs ?? vs, log); fsSource = ShaderSource.Content(ofs ?? fs, log);
        if (ShaderContract.BlocksSafe(vs, vsSource) && ShaderContract.BlocksSafe(fs, fsSource))
            return ReferenceEquals(vsSource, vs) && ReferenceEquals(fsSource, fs);
        log?.Info($"{what}: an override .shd reads a uniform block's terms outside the block -- building the embedded default");
        vsSource = vs; fsSource = fs;
        return true;
    }
}
