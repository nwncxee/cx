using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The sky's own fragment shader, compiled as an ALLOY program: a dynamic sky IS its fragment stage, spliced
/// off the resource chain into <c>cx_sky_fs</c> (whose environment stands in for the engine's own includes,
/// so stock text drops in unedited) and linked against Alloy's sky vertex stage -- so the dome takes
/// <c>uVP</c> like any Alloy draw and renders into the probe's cube faces. Its textures stay the ENGINE's.
/// </summary>
internal static unsafe class SkyStage
{
    /// <summary>The stage's resref (an override point): the environment plus the <c>// $VANILLA-MAIN</c>
    /// region the content sky stage is spliced into. An author overriding this replaces the environment, not
    /// the content's sky.</summary>
    internal const string ShdFs = "cx_sky_fs";
    /// <summary>The token the region's resref is written into before the splice. Not a
    /// <see cref="ShaderResource.Tokens"/> entry: the splice runs BEFORE token expansion, so a marker naming
    /// a token would ask the resource chain for a file called <c>$SKYFS$</c>.</summary>
    private const string FsToken = "$SKYFS$";

    /// <summary>A sky has three gobs and they share one material in every module measured; four distinct
    /// custom fragment stages is a ceiling, not a sizing decision. Past it the sky is refused for the frame
    /// (loudly, once), and nothing else draws it, so the frame has no sky.</summary>
    private const int MaxProgs = 4;

    /// <summary>The names a Vulkan session's entries go by, one per slot: no GL program exists behind them, and the
    /// Vulkan sky builds each from <see cref="Texts"/> through <see cref="Hosted"/>.</summary>
    private const uint VkNameBase = 0xFFFFFF00;   // not-an-offset: a reserved name range

    private struct Entry
    {
        public string Ref;
        public uint Prog;
        public bool Failed;
        /// <summary>The native stage declared for this one (<see cref="StageResources"/>), or -1 where the module ships
        /// none -- kept even when it stood down.</summary>
        public int Native;
        /// <summary>Is that native stage what this program hosts? False = the stage the engine runs is hosted plainly.</summary>
        public bool NativeLive;
    }

    /// <summary>Every sky stage this session built, in one object: a device change replaces it, so neither a
    /// program name nor the failure latch taken against the one before survives as a field nobody reset.</summary>
    private sealed class SkyState
    {
        public readonly Entry[] Progs = new Entry[MaxProgs];
        public int Count;
    }

    private static SkyState _gl = new();
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Device loss: every program name is gone, and a failure latch taken against the old device is
    /// not evidence about the new one.</summary>
    internal static void Forget() => _gl = new SkyState();

    /// <summary>Module change: the spliced text came off THAT module's resource chain, so it has a module
    /// lifetime (the server-switch trap -- a program built from one server's chain running on another's). Rebuilds
    /// lazily on the next sky frame.</summary>
    internal static void DropPrograms()
    {
        _gl = new SkyState();
    }

    /// <summary>The program for a material's custom fragment stage <paramref name="fs"/>, built on first sight
    /// and cached for the module. 0 when it cannot be built (logged there), which refuses the sky rather than
    /// drawing a dome whose whole look that stage was. Staging path: once per staged sky part per frame.</summary>
    internal static uint Program(string fs)
    {
        if (fs.Length == 0) return 0;
        var g = _gl;
        for (int i = 0; i < g.Count; i++)
            if (string.Equals(fs, g.Progs[i].Ref, StringComparison.OrdinalIgnoreCase))
            {
                ref Entry hit = ref g.Progs[i];
                // The native stage this entry hosts gave up while the frame was running (its images could not
                // be remade). Replant THIS entry on the stage the engine runs -- once -- rather than rebuild
                // every sky program, which would also throw away the refusal that caused it.
                if (hit.NativeLive && !StageResources.Live(hit.Native)) Replant(ref hit);
                // Staged ahead of the frame's head, so its passes run before the dome draws.
                if (hit.NativeLive) StageResources.Use(hit.Native);
                return hit.Prog;
            }
        if (g.Count >= MaxProgs)
        {
            _log?.Always($"SkyStage: more than {MaxProgs} distinct sky fragment stages in one session -- the sky "
                       + "is NOT DRAWN for this area");
            return 0;
        }
        string resref = fs.ToLowerInvariant();
        uint vkName = VkNameBase + (uint)g.Count;
        ref Entry e = ref g.Progs[g.Count++];
        e.Ref = resref;
        e.NativeLive = TryNative(resref, vkName, out e.Native, out uint prog);
        e.Prog = e.NativeLive ? prog : Build(resref, native: false, vkName);
        if (e.Prog == 0) { e.Failed = true; return 0; }
        if (e.NativeLive) StageResources.Use(e.Native);
        _log?.Info(e.NativeLive
            ? $"SkyStage: {StageResources.Prefix}{resref}.shd hosted as the sky's fragment stage -- the native "
              + $"version of {resref}.shd, in linear HDR"
            : $"SkyStage: {resref}.shd hosted as the sky's fragment stage");
        return e.Prog;
    }

    /// <summary>Move one entry off its native stage and onto the stage the engine runs, keeping its cached slot:
    /// the native program goes, the plain one is built, and the entry stops being native. Said by
    /// <see cref="StageResources.Fail"/>; a plain stage that will not build leaves the sky undrawn, as ever.</summary>
    private static void Replant(ref Entry e)
    {
        uint vkName = e.Prog;
        e.NativeLive = false;
        e.Prog = Build(e.Ref, native: false, vkName);
        if (e.Prog == 0) { e.Failed = true; return; }
        _log?.Info($"SkyStage: {e.Ref}.shd is hosted plainly from here on -- its native stage stood down");
    }

    /// <summary>Try the stage's native version: <c>cx_&lt;stage&gt;</c> in the chain, its declarations taken and its
    /// passes built, and the dome's program linked from it under <c>CX_NATIVE</c>. True with <paramref name="prog"/>
    /// when that is what the sky will host; <paramref name="id"/> comes back set either way (-1 only where the module
    /// ships none), so the period line can name a refusal.</summary>
    private static bool TryNative(string resref, uint vkName, out int id, out uint prog)
    {
        id = -1;
        prog = 0;
        // No log line for "the module ships none": that is every module that has not written one.
        string? native = StageResources.NativeOf(resref);
        if (native == null) return false;
        id = StageResources.Declare(native, _log);
        if (!StageResources.Live(id)) return false;
        prog = Build(native, native: true, vkName);
        if (prog != 0) return true;
        StageResources.Fail(id, "the sky program for it did not build (see the compile log above)", _log);
        return false;
    }

    /// <summary>The content stage GL program <paramref name="prog"/> hosts, and whether it hosts that stage's
    /// native version: what a second backend needs to build the same dome. False for a program this cache does
    /// not hold.</summary>
    internal static bool Hosted(uint prog, out string resref, out bool native, out int stage)
    {
        resref = ""; native = false; stage = -1;
        var g = _gl;
        for (int i = 0; i < g.Count; i++)
            if (g.Progs[i].Prog == prog && prog != 0)
            {
                resref = g.Progs[i].Ref;
                native = g.Progs[i].NativeLive;
                stage = native ? g.Progs[i].Native : -1;
                return true;
            }
        return false;
    }

    /// <summary>What a build of content stage <paramref name="resref"/> is called in the log.</summary>
    private static string What(string resref, bool native)
        => $"AlloyRenderer sky program ({resref}{(native ? ", native, linear HDR" : "")})";

    /// <summary>The sky pair's text for content stage <paramref name="resref"/>: Alloy's own fullbright VERTEX stage
    /// and <see cref="ShdFs"/> with the content resref written into its region. <paramref name="pair"/> prefers an
    /// override of either half, <paramref name="embedded"/> is the shipped pair a failed override falls back to. False
    /// (said once) when the content stage is not in the resource chain, which is no sky for this area.</summary>
    internal static bool Texts(string resref, bool native, out (string Vs, string Fs) pair, out (string Vs, string Fs) embedded)
    {
        pair = embedded = ("", "");
        if (resref.Length > ShaderResource.ResRefBytes) return false;
        // Refused rather than stubbed: the region's fallback stub is a BLACK DOME, which is a picture, so a
        // program built from it would be a silent fallback in the exact sense CX-45 forbids. No stage in the
        // chain (or the bridge disarmed) means no sky for this area, said out loud.
        if (!VanillaShader.InChain(resref))
        {
            _log?.Always($"SkyStage: {resref}.shd is not in the resource chain (or alloy.vanillabridge=0) -- the "
                       + "sky cannot be compiled here and is NOT DRAWN (nothing else draws the sky since CX-87)");
            return false;
        }
        // The override lookup is done HERE, not inside the splice, because the region's
        // resref has to be written in before the splice reads the marker -- and an override that keeps the
        // token is entitled to the same substitution the embedded text gets.
        string evs = ShaderStages.Source(AlloyRenderer.ShdFullbrightVs), efs = ShaderStages.Source(ShdFs);
        string vs = ShaderResource.Override(AlloyRenderer.ShdFullbrightVs, _log) ?? evs;
        string fs = ShaderResource.Override(ShdFs, _log) ?? efs;
        // A native stage hands back LINEAR HDR, so the wrapper's clamp-and-encode tail is compiled out. Spliced in at
        // the #version line, which is the one place GLSL takes it, for the override exactly as for the default.
        if (native)
        {
            bool own = ReferenceEquals(fs, efs);
            efs = ShaderStages.WithDefine(efs, StageResources.NativeDefine);
            fs = own ? efs : ShaderStages.WithDefine(fs, StageResources.NativeDefine);
        }
        // The vertex half reads the per-draw record, so it owes the same two contracts the unlit program's
        // build checks -- an override that dropped them would put every dome at its neighbour's matrix.
        if (!ReferenceEquals(vs, evs) && !(ShaderContract.RecordSafe(vs) && ShaderContract.SlotIndexSafe(vs)))
        {
            _log?.Info($"{What(resref, native)}: the {AlloyRenderer.ShdFullbrightVs} override does not declare the "
                     + "per-draw record -- the sky takes the embedded vertex stage");
            vs = evs;
        }
        pair = (vs, fs.Replace(FsToken, resref, StringComparison.Ordinal));
        embedded = (evs, efs.Replace(FsToken, resref, StringComparison.Ordinal));
        return true;
    }
    /// <summary>Content stage <paramref name="resref"/>'s GL program; on Vulkan, <paramref name="vkName"/> once its texts
    /// are in the chain, the Vulkan sky compiling them itself. 0 when it cannot be built (said here).</summary>
    private static uint Build(string resref, bool native, uint vkName)
    {
        if (!Texts(resref, native, out var pair, out var embedded)) return 0;
        return vkName;
    }


    /// <summary>Diagnostic: the stages built this session and the ones that refused, by name -- "no stage named",
    /// "a stage that would not build" and "a native stage running" are different facts and read differently; why a
    /// native stage fell back is on the native stages' own clause.</summary>
    internal static string Stats()
    {
        var g = _gl;
        if (g.Count == 0) return "none";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < g.Count; i++)
        {
            ref Entry e = ref g.Progs[i];
            sb.Append(i > 0 ? "; " : "").Append(e.Ref).Append(e.Failed ? " REFUSED" : " hosted");
            if (e.NativeLive) sb.Append(" as ").Append(StageResources.Prefix).Append(e.Ref);
            else if (e.Native >= 0) sb.Append(" plainly, its native stage stood down");
        }
        return sb.ToString();
    }

    /// <summary>Off-device: the token, the place a typo would show as "there is no sky and nothing says
    /// why".</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string src = ShaderStages.Source(ShdFs);
        check.Expect(src.Contains("// $VANILLA-MAIN " + FsToken + "\n"), "the stage carries the whole-stage region, token and all");
        check.Expect(src.Contains("void " + VanillaShader.MainEntry + "()"), "...and a stub under the entry name the splice renames to");
        check.Expect(src.Contains("CxWholeTexel = texture("), "...and the wrapper samples the texel inc_standard's setup call multiplies in");
        check.Expect(!src.Contains(FsToken + "$"), "the token is not itself a sizing token (the splice runs first)");
        log.Info(check.Ok ? "SkyStage.SelfCheck: passed" : "SkyStage.SelfCheck: FAILED");
    }
}
