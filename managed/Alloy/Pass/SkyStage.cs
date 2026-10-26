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

    private struct Entry
    {
        public string Ref;
        public uint Prog;
        public bool Failed;
        public int Vp, DrawBase, Tex, Tex1, Tint, SkyFog, ViewInv, Global, Oit, OitDepth;
        /// <summary>Every engine term this program shares with a native stage's compute passes.</summary>
        public StageEnvLoc Env;
        /// <summary>The native stage declared for this one (<see cref="StageResources"/>), or -1 where the module ships
        /// none -- kept even when it stood down, and what this program was attached as (-1: nothing per bind).</summary>
        public int Native, Rec;
        /// <summary>Is that native stage what this program hosts? False = the stage the engine runs is hosted plainly.</summary>
        public bool NativeLive;
    }

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so neither a program
    /// name nor the failure latch taken against the dead context survives as a field nobody remembered to
    /// zero.</summary>
    private sealed class SkyState
    {
        public readonly Entry[] Progs = new Entry[MaxProgs];
        public int Count;
        /// <summary>The program whose per-pass uniforms are already uploaded this pass, so a role's several
        /// parts cost one upload. Cleared by <see cref="BeginPass"/>.</summary>
        public uint Primed;
    }

    private static SkyState _gl = new();
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Context loss: every GL name is gone, and a failure latch taken against the old context is not
    /// evidence about the new one.</summary>
    internal static void Forget() => _gl = new SkyState();

    /// <summary>Module change: the spliced text came off THAT module's resource chain, so it has a module
    /// lifetime (the server-switch trap -- a program built from one server's chain running on another's). Rebuilds
    /// lazily on the next sky frame.</summary>
    internal static void DropPrograms()
    {
        for (int i = 0; i < _gl.Count; i++) if (_gl.Progs[i].Prog != 0) Gl.DeleteProgram(_gl.Progs[i].Prog);
        _gl = new SkyState();
    }

    /// <summary>The entry a program name belongs to, or -1 -- the one lookup the per-pass and per-draw calls
    /// share. Hot, and over at most <see cref="MaxProgs"/> entries.</summary>
    private static int IndexOf(uint prog)
    {
        var g = _gl;
        for (int i = 0; i < g.Count; i++) if (g.Progs[i].Prog == prog) return i;
        return -1;
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
        ref Entry e = ref g.Progs[g.Count++];
        e.Ref = resref;
        e.NativeLive = TryNative(resref, out e.Native, out uint prog);
        e.Prog = e.NativeLive ? prog : Build(resref, native: false);
        if (e.Prog == 0) { e.Failed = true; return 0; }
        // A native program that its stage's images refuse (a type that disagrees, say) is replanted on the plain one.
        if (!Locate(ref e)) Replant(ref e);
        if (e.Prog == 0) return 0;
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
        if (e.Prog != 0) Gl.DeleteProgram(e.Prog);
        e.NativeLive = false;
        e.Prog = Build(e.Ref, native: false);
        if (e.Prog == 0) { e.Failed = true; return; }
        Locate(ref e);
        _log?.Info($"SkyStage: {e.Ref}.shd is hosted plainly from here on -- its native stage stood down");
    }

    /// <summary>Try the stage's native version: <c>cx_&lt;stage&gt;</c> in the chain, its declarations taken and its
    /// passes built, and the dome's program linked from it under <c>CX_NATIVE</c>. True with <paramref name="prog"/>
    /// when that is what the sky will host; <paramref name="id"/> comes back set either way (-1 only where the module
    /// ships none), so the period line can name a refusal.</summary>
    private static bool TryNative(string resref, out int id, out uint prog)
    {
        id = -1;
        prog = 0;
        // No log line for "the module ships none": that is every module that has not written one.
        string? native = StageResources.NativeOf(resref);
        if (native == null) return false;
        id = StageResources.Declare(native, _log);
        if (!StageResources.Live(id)) return false;
        prog = Build(native, native: true);
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
        // The override lookup is done HERE, not by GlProgram's overridable overload, because the region's
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
    private static uint Build(string resref, bool native)
    {
        if (!Texts(resref, native, out var pair, out var embedded)) return 0;
        string what = What(resref, native);
        uint prog = GlProgram.Graphics(pair.Vs, pair.Fs, _log, what);
        if (prog == 0 && (pair.Vs != embedded.Vs || pair.Fs != embedded.Fs))
        {
            _log?.Info($"{what}: an override .shd failed to build -- falling back to the embedded stages");
            prog = GlProgram.Graphics(embedded.Vs, embedded.Fs, _log, what);
        }
        if (prog != 0 && GlProgram.LastStubbed)
        {
            Gl.DeleteProgram(prog);
            prog = 0;
        }
        // "NOT DRAWN" belongs to the PLAIN arm alone: a native stage that will not build is followed by this same
        // method on the stage the engine runs, and StageResources.Fail is what says so. Claiming no sky here would
        // be a line about something that is not happening.
        if (prog == 0 && !native)
            _log?.Always($"SkyStage: the sky program for {resref}.shd did not build (a stubbed stage is a black "
                       + "dome, so it is refused too) -- the sky is NOT DRAWN; see the compile log above");
        return prog;
    }

    /// <summary>The program's uniform locations, and its attachment to its native stage's images and the frame
    /// uniforms. False when the native stage refused it: the caller replants the entry.</summary>
    private static bool Locate(ref Entry e)
    {
        uint p = e.Prog;
        e.Vp = GlProgram.Loc(p, "uVP"); e.DrawBase = GlProgram.Loc(p, "uDrawBase");
        e.Tex = GlProgram.Loc(p, "uTex"); e.Tex1 = GlProgram.Loc(p, "texUnit1");
        e.Tint = GlProgram.Loc(p, "uTint"); e.SkyFog = GlProgram.Loc(p, "uSkyFog");
        e.ViewInv = GlProgram.Loc(p, "uViewInv");
        // The engine's own per-draw constant colour: PartTriMesh::Mat's SetColor4f, which every stock stage
        // seeds FragmentColor from. Declared by the CONTENT stage, so this may be -1.
        e.Global = GlProgram.Loc(p, "globalColor");
        e.Oit = GlProgram.Loc(p, "uOit"); e.OitDepth = GlProgram.Loc(p, "uOitDepth");
        e.Env = StageEnvLoc.Locate(p);
        e.Rec = StageResources.Attach(p, e.NativeLive ? e.Native : -1, -1, _log);
        return e.Rec != -2;
    }

    /// <summary>A pass is starting: nothing is primed. Cheap, and it is what stops one pass's upload being
    /// credited to the next (the probe's six faces each have their own view-projection).</summary>
    internal static void BeginPass() => _gl.Primed = 0;

    /// <summary>Everything a sky program needs that does not vary per draw, once per program per pass: the view-projection,
    /// the sampler units, the camera's world matrix (stock reads its translation column as the eye) and the engine's
    /// calendar and weather scalars. False: its native stage stood down at this frame's head, its images are gone, and
    /// the dome sits the frame out (said by <see cref="StageResources.Fail"/>) until the next staging replants it. Hot.</summary>
    internal static bool Prepare(uint prog, Matrix4x4* vp, int* viewport)
    {
        int at = IndexOf(prog);
        if (at < 0) return true;
        var g = _gl;
        ref Entry e = ref g.Progs[at];
        if (e.NativeLive && !StageResources.Live(e.Native)) return false;
        Gl.UseProgram(prog);
        if (g.Primed == prog)
        {
            Gl.UniformMatrix4fv(e.Vp, 1, 0, (float*)vp);
            return true;
        }
        g.Primed = prog;
        Gl.UniformMatrix4fv(e.Vp, 1, 0, (float*)vp);
        Gl.Uniform1i(e.Tex, 0);
        Gl.Uniform1i(e.Tex1, 1);
        // uOit is program state and the sky is never in the OIT lists; left at the accumulation arm the dome
        // would land as a near-zero weighted write (gl_FragCoord.z = 1), which is a black sky.
        Gl.Uniform1i(e.Oit, 0);
        Gl.Uniform1i(e.OitDepth, (int)TextureSlots.OitDepth);
        // ponytail: stock reads m_view_inv's TRANSLATION COLUMN only (`vPosWorld.xyz - m_view_inv[3].xyz`,
        // the view ray: inc_sunm, inc_moonm, inc_stars, inc_clouds), so a translation carries every read the
        // sky chain makes, at every pass's own eye. A content stage that wants the camera BASIS needs the real
        // inverse view threaded through from AlloyRenderer.Frame.
        Matrix4x4 viewInv = Matrix4x4.CreateTranslation(AlloyRenderer.EyePos);
        Gl.UniformMatrix4fv(e.ViewInv, 1, 0, (float*)&viewInv);
        // The engine terms -- the same upload a native stage's compute passes take, so a pass and the stage it feeds
        // never disagree about the calendar -- then its image units and frame uniforms, per pass.
        e.Env.Upload(viewport);
        StageResources.Bind(e.Rec);
        return true;
    }

    /// <summary>The per-draw terms: the record index the vertex stage reads its matrix at, the fog band, and
    /// the engine's own constant colour -- <c>(tint, alpha)</c>, which is <c>PartTriMesh::Mat</c>'s two
    /// branches exactly (white at the role's alpha, or the scene's fog colour at 1.0 for the skyfade
    /// gob).</summary>
    internal static void Draw(uint prog, int slot, float* tint, float alpha, float skyFog)
    {
        int at = IndexOf(prog);
        if (at < 0) return;
        ref Entry e = ref _gl.Progs[at];
        Gl.Uniform1i(e.DrawBase, slot);
        Gl.Uniform3fv(e.Tint, 1, tint);
        Gl.Uniform4f(e.Global, tint[0], tint[1], tint[2], alpha);
        Gl.Uniform1f(e.SkyFog, skyFog);
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
