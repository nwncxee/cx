using System;
using System.IO;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The overridable stage set: which resrefs exist and the warning header a dumped copy carries.
/// Every program build reads <see cref="Source"/> rather than composing its own text (ADR 0012), and the
/// composition lives in the GLSL: a stage is the embedded <c>&lt;resref&gt;.glsl</c>, whose <c>#include</c>
/// lines name its libraries, so <c>check-shaders.py</c> compiles the same files the extender does.</summary>
internal static class ShaderStages
{
    /// <summary>The dump folder, inside the client's own user directory (next to nwn.ini).</summary>
    internal static string DumpDir => Native.ConfigPath("cx_shader_dump");

    internal const string Banner = """
        // Dumped from NWNCX:EE's embedded default by /alloy shaderdump -- the stage THIS build
        // compiles, so it is current by construction. Drop it in a hak or override as <resref>.shd to
        // replace that stage (a hak reaches every player); the extender falls back to the embedded default if
        // it fails to build. Re-dump and diff after each extender update: that diff is your drift.
        // $CAP$ / $SORT$ / $GROUP$ / $SRGB$ / $BAND$ / $AA$ / $SKYFS$ / $PASSCS$ are expanded by the extender on load -- leave them as tokens.
        // `//@cx-lib <resref>` .. `//@cx-end <resref>` bracket each library this stage includes; keep them and a
        // <resref>.shd library override still reaches this copy. The contract an override has to keep:
        // docs/shader-overrides.md.

        """;

    internal const string LibBanner = """
        // Dumped from NWNCX:EE's embedded default by /alloy shaderdump: a LIBRARY, spliced into
        // every stage that #includes it. Drop it in a hak or override as <resref>.shd to replace it in all of
        // them at once; a program it breaks rebuilds from the embedded libraries. Its own #include lines are
        // expanded on load, and the sizing tokens too. Re-dump and diff after each extender update.
        // See docs/shader-overrides.md.

        """;

    internal const string Invariant = """
        // IMPORTANT: `invariant gl_Position;` and the clip expression below. The depth prepass writes
        // these depths and the colour pass re-tests them with LEQUAL, so any other spelling loses fragments
        // to a ULP of drift -- holes, and no log line. See docs/shader-overrides.md.

        """;

    /// <summary>The grass vertex stage is invariant over ITSELF, not over the world's clip expression:
    /// its depth prepass and its colour pass are two programs compiled from this one text, and the colour
    /// pass tests LEQUAL against the prepass's depth. It displaces, so it neither computes nor promises the
    /// <see cref="Invariant"/> expression.</summary>
    internal const string SelfInvariant = """
        // IMPORTANT: `invariant gl_Position;`. This stage is compiled into TWO programs -- the grass depth
        // prepass and the grass colour pass -- and the second re-tests the first's depth with LEQUAL, so the
        // clip position must come out bit-identical from both. Keep the declaration; the expression is yours.
        // See docs/shader-overrides.md.

        """;

    internal const string Record = """
        // IMPORTANT: the CxDraw struct below is the extender's per-draw SSBO record, and this file is a
        // COPY of a layout that lives in C#. Drop or reorder a field and every draw reads its neighbour's.
        // The extender counts the vec4s and refuses a stage that disagrees. See docs/shader-overrides.md.

        """;

    internal const string CasterRecord = """
        // IMPORTANT: this stage reads ALLOY's per-draw record (CxDraw) at gl_BaseInstance, which IS
        // the ownership slot (#303 -- the gather dispatch writes it into every surviving command). Index it
        // any other way and every caster bakes at its neighbour's matrix. The skinning branch is
        // important too: without it every creature casts its bind pose. The extender refuses a stage
        // that disagrees. See docs/shader-overrides.md.

        """;

    /// <summary>The dump name of the bindless reference copy: a resref that fits a hak like every other dumped
    /// file, and one the extender never asks for, so shipping it overrides nothing.</summary>
    internal const string BindlessRef = "cx_ownlit_bl_ref";

    internal const string BindlessNote = """
        // REFERENCE ONLY -- not a starting point for an override; the extender never loads this name. This is
        // the lit fragment stage after the extender's own bindless rewrite (#153), which it applies to
        // whatever cx_ownlit_fs supplies when ARB_bindless_texture is available. Override that instead.

        """;

    /// <summary>Every overridable resref with the header its dumped copy carries. <see cref="SelfCheck"/>
    /// asserts the count, so a stage added or dropped here fails the suite until the number moves with it.</summary>
    internal static readonly (string Ref, string Header)[] All =
    {
        (AlloyRenderer.ShdFullbrightVs, Banner + Invariant + Record),
        (AlloyRenderer.ShdFullbrightFs, Banner),
        (AlloyRenderer.ShdLitVs,        Banner + Invariant + Record),
        (AlloyRenderer.ShdLitFs,        Banner + Record),
        (AlloyRenderer.ShdWaterVs,      Banner + Invariant + Record),
        (AlloyRenderer.ShdWaterFs,      Banner + Record),
        (GrassField.ShdGrassVs,            Banner + SelfInvariant),
        (GrassField.ShdGrassFs,            Banner),
        (GrassField.ShdBladeVs,            Banner),
        (GrassField.ShdBladeFs,            Banner),
        (AlloyLights.ShdClusterCs,         Banner),
        (DeferredLights.ShdCs,             Banner),
        (DeferredShadows.ShdCs,            Banner),
        (PairDebt.ShdCs,                   Banner),
        (FogVolume.ShdLightCs,             Banner),
        (FogVolume.ShdSumCs,               Banner),
        (CasterPass.ShdCastVs,             Banner + Invariant + Record + CasterRecord),
        (CasterPass.ShdCastFs,             Banner),
        (PostChain.ShdPostVs,              Banner),
        (PostChain.ShdBloomDownFs,         Banner),
        (PostChain.ShdBloomUpFs,           Banner),
        (PostChain.ShdResolveFs,           Banner),
        (PostStack.ShdGammaFs,             Banner),
        (SmaaStage.ShdEdgeFs,              Banner),
        (SmaaStage.ShdWeightFs,            Banner),
        (SmaaStage.ShdBlendFs,             Banner),
        (PostStack.ShdTaaFs,               Banner),
        (PostStack.ShdToonFs,              Banner),
        (PostStack.ShdSharpenFs,           Banner),
        (PostStack.ShdVibranceFs,          Banner),
        (FbPostStage.ShdFs,                Banner),
        (SkyStage.ShdFs,                   Banner),
        (StageResources.ShdPassCs,         Banner),
        (SsaoPass.ShdSsaoFs,               Banner),
        (SsaoPass.ShdSsaoBlurFs,           Banner),
        (Halos.ShdHaloVs,                  Banner),
        (Halos.ShdHaloFs,                  Banner),
        (Weather.ShdWeatherVs,             Banner),
        (Weather.ShdRainFs,                Banner),
        (Weather.ShdSnowFs,                Banner),
        (Weather.ShdSplashVs,              Banner),
        (Weather.ShdSplashFs,              Banner),
    };

    /// <summary>The GLSL the extender compiles for one resref: the embedded <c>&lt;resref&gt;.glsl</c>
    /// with its <c>#include</c>s spliced, sizing tokens left unexpanded (<see cref="ShaderResource.Tokens"/>
    /// expands them at compile time, for an override exactly as for the embedded default). Cold: one call
    /// per program build, one per dumped file.</summary>
    internal static string Source(string resref) => ShaderSource.Get(resref + ".glsl");

    /// <summary><paramref name="src"/> with <c>#define <paramref name="define"/></c> spliced immediately after
    /// its <c>#version</c> line -- the one place GLSL takes it, and the same splice
    /// <c>check-shaders.py</c> makes, so a variant the gate compiles is the text the extender builds.</summary>
    internal static string WithDefine(string src, string define) => AfterVersion(src, $"#define {define}\n");

    /// <summary><paramref name="src"/> with <paramref name="lines"/> right after its <c>#version</c> line, ahead of every
    /// other token -- where an <c>#extension</c> must sit. Unchanged when it has no <c>#version</c>.</summary>
    internal static string AfterVersion(string src, string lines)
    {
        int v = src.IndexOf("#version", StringComparison.Ordinal);
        int e = v < 0 ? -1 : src.IndexOf('\n', v);
        return e < 0 ? src : src[..(e + 1)] + lines + src[(e + 1)..];
    }

    /// <summary>Write every overridable stage into <paramref name="dir"/> as an editable
    /// <c>&lt;resref&gt;.shd</c>, plus the bindless variant of the lit stage for reference. Returns the file
    /// count, or -1 if the folder could not be written (message logged).</summary>
    internal static int Dump(string dir, ExtenderLog? log)
    {
        try
        {
            Directory.CreateDirectory(dir);
            int n = 0;
            foreach ((string r, string header) in All)
            {
                File.WriteAllText(Path.Combine(dir, r + ".shd"), header + Source(r));
                n++;
            }
            foreach ((string r, string text) in ShaderSource.Libraries())
            {
                File.WriteAllText(Path.Combine(dir, r + ".shd"), LibBanner + text);
                n++;
            }
            File.WriteAllText(Path.Combine(dir, BindlessRef + ".shd"),
                              BindlessNote + AlloyRenderer.BindlessFs(Source(AlloyRenderer.ShdLitFs)));
            n++;
            log?.Info($"ShaderStages: dumped {n} stages to {dir}");
            return n;
        }
        catch (Exception e)
        {
            log?.Info($"ShaderStages: dump to {dir} failed -- {e.Message}");
            return -1;
        }
    }

    /// <summary>The headless trigger: <c>alloy.dumpshaders=1</c> in <c>cx.cfg</c> dumps once at SelfCheck, so
    /// the assembled stages -- including the bindless rewrite, which is a C# function and so out of reach of
    /// the offline checker -- can be compiled without a live client. A Restart knob: the decision has to be
    /// made before the first frame.</summary>
    internal static void DumpIfConfigured(ExtenderLog? log)
    {
        if (Renderer.DumpShaders.Boot) Dump(DumpDir, log);
    }

    /// <summary>The <c>alloy.shaderdump</c> button -- write every overridable stage to
    /// <c>&lt;user directory&gt;/cx_shader_dump/</c> as an editable .shd, plus the bindless variant of the lit
    /// stage. Dumping what is compiled in keeps the starting point current by construction (ADR 0012); the
    /// sizing tokens stay unexpanded, because the extender expands them on load for an override too.</summary>
    internal static void DumpToLog()
    {
        ExtenderLog? log = KnobGroup.Log;
        int n = Dump(DumpDir, log);
        log?.Info(n < 0 ? $"[shaders] dump to {DumpDir} failed" : $"[shaders] {n} stages written to {DumpDir}");
    }

    /// <summary>Every resref composes to something, and a header claiming the per-draw record is on a stage
    /// that declares it. A missing switch arm is otherwise invisible until a program builds from "".</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = All.Length == 42 && BindlessRef.Length <= ShaderResource.ResRefBytes;
        foreach ((string r, string header) in All)
        {
            string src = Source(r);
            ok &= src.Length > 0 && r.Length <= ShaderResource.ResRefBytes
                  && src.Contains("#version")
                  && header.Contains("docs/shader-overrides.md")
                  && header.Contains(Record) == src.Contains("struct CxDraw")
                  && header.Contains(CasterRecord) == src.Contains("uDraw[gl_BaseInstance]")
                  && (header.Contains(Invariant) || header.Contains(SelfInvariant)) == src.Contains("invariant gl_Position;");
        }
        log.Info(ok
            ? $"ShaderStages.SelfCheck: passed ({All.Length} overridable resrefs)"
            : "ShaderStages.SelfCheck: FAILED -- a resref composes to nothing, or a dump header claims a "
              + "constraint its stage does not carry");
        return ok;
    }
}
