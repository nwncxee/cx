using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class Particles
{
    /// <summary>Off-device: the parts of this file that are decisions rather than transcription -- the
    /// interpolation domain, the UV-grid stepping and its wrap, the quaternion algebra the billboard basis
    /// stands on, and the blend classifier. All of it is pure arithmetic over synthetic input, so it runs on
    /// a machine with no client.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // ---- the byte scale. 255.99, not 255: the engine truncates, so 255 would make a full-bright
        // channel come out 254 on every particle in the game.
        check.Expect((uint)(long)(1f * ByteScale) == 255 && (uint)(long)(0.5f * ByteScale) == 127,
          "a 1.0 channel truncates to 255 under the engine's 255.99 scale");

        // ---- the quaternion axes. Getting one of these transposed points every billboard the wrong way,
        // and the failure looks like "particles face oddly" rather than like a bug.
        Vector3 x = AxisX(1f, 0f, 0f, 0f), z = AxisZ(1f, 0f, 0f, 0f);
        check.Expect(x == new Vector3(1f, 0f, 0f) && z == new Vector3(0f, 0f, 1f),
          "the identity quaternion leaves the unit axes alone");
        // 90 degrees about Z: X -> Y, Z stays put
        float h = MathF.Sqrt(0.5f);
        Vector3 rx = AxisX(h, 0f, 0f, h), rz = AxisZ(h, 0f, 0f, h);
        check.Expect(MathF.Abs(rx.Y - 1f) < 1e-5f && MathF.Abs(rx.X) < 1e-5f && MathF.Abs(rz.Z - 1f) < 1e-5f,
          "a quarter turn about Z carries X onto Y and leaves Z alone -- (w,x,y,z) order, not (x,y,z,w)");
        // the product is Hamilton and in that order: rolling by nothing must leave the basis untouched
        Vector4 id = QMul(new Vector4(1f, 0f, 0f, 0f), new Vector4(h, 0f, 0f, h));
        check.Expect(MathF.Abs(id.X - h) < 1e-6f && MathF.Abs(id.W - h) < 1e-6f,
          "a zero roll composes to the camera quaternion unchanged");

        // ---- the UV grid, including the wrap in both directions.
        var pt = stackalloc byte[0x80];
        for (int i = 0; i < 0x80; i++) pt[i] = 0;
        var q = new Particle((IntPtr)pt);
        float u0, u1, v0, v1;
        q.Frame = 5;                                    // 4x2 grid: frame 5 = column 1, row 1
        Frame(q, 0f, 0f, 0f, 0.25f, 0.5f, 4, 8, out u0, out u1, out v0, out v1);
        // the inset is deliberately included in the tolerance: the cell is right, the read is pulled inside it
        float ins = 0.25f * CellInset, insV = 0.5f * CellInset;
        check.Expect(MathF.Abs(u0 - (0.25f + ins)) < 1e-6f && MathF.Abs(u1 - (0.5f - ins)) < 1e-6f
          && MathF.Abs(v1 - (0.5f - insV)) < 1e-6f && MathF.Abs(v0 - insV) < 1e-6f,
          "frame 5 of a 4x2 grid is column 1, row 1, V counting DOWN from 1 -- pulled inside its own cell");
        check.Expect(u0 > 0.25f && u1 < 0.5f && v0 > 0f && v1 < 0.5f,
          "...strictly INSIDE, on every edge: a read that touches a cell border reaches the frame beside it");

        // Containment: an index outside the frames the emitter declared can never select a cell. This is the
        // "no foreign artwork, whatever the index says" guarantee, and it has to hold in both orientations
        // and against the atlas size as well as against the declared range.
        q.Frame = 900;                                  // far past anything a 4x2 atlas could hold
        Frame(q, 0f, 2f, 5f, 0.25f, 0.5f, 4, 8, out u0, out u1, out v0, out v1);
        check.Expect(u0 >= 0.25f && u1 <= 0.5f && v0 >= 0f && v1 <= 0.5f,
          "a wild frame index lands on the emitter's LAST declared cell, never outside the atlas");
        q.Frame = -7;
        Frame(q, 0f, 2f, 5f, 0.25f, 0.5f, 4, 8, out u0, out u1, out v0, out v1);
        check.Expect(u0 >= 0.5f && u1 <= 0.75f && v0 >= 0.5f && v1 <= 1.0f,
          "...and a negative one lands on the FIRST, which for start=2 is column 2 of the top row");
        // ...but an emitter that declares NO range keeps its per-particle index, bounded only by the atlas.
        q.Frame = 6;
        Frame(q, 0f, 0f, 0f, 0.25f, 0.5f, 4, 8, out u0, out u1, out v0, out v1);
        check.Expect(u0 >= 0.5f && u1 <= 0.75f && v0 >= 0f && v1 <= 0.5f,
          "equal frame ends mean NO animation, not one frame -- the index still picks the cell");
        q.Frame = 900;
        Frame(q, 0f, 0f, 0f, 0.25f, 0.5f, 4, 8, out u0, out u1, out v0, out v1);
        check.Expect(u0 >= 0.75f && u1 <= 1.0f && v0 >= 0f && v1 <= 0.5f,
          "...and it is still bounded by the ATLAS, which is the guarantee that matters");
        q.Frame = 3; q.Age = 1f; q.FrameTime = 0f;
        Frame(q, 0.5f, 0f, 3f, 0.25f, 0.5f, 4, 8, out _, out _, out _, out _);
        check.Expect(q.Frame == 0, "an ascending grid past its end frame wraps back to the start frame");
        check.Expect(q.FrameTime == 1f, "...and the step stamps the age, or it advances every frame");
        q.Frame = 1; q.Age = 1f; q.FrameTime = 0f;
        Frame(q, 0.5f, 3f, 0f, 0.25f, 0.5f, 4, 8, out _, out _, out _, out _);
        check.Expect(q.Frame == 3, "a descending grid past its end frame wraps the other way");
        q.Frame = 2; q.Age = 0.1f; q.FrameTime = 0f;
        Frame(q, 0.5f, 0f, 3f, 0.25f, 0.5f, 4, 8, out _, out _, out _, out _);
        check.Expect(q.Frame == 2, "a particle that has not held its frame long enough does not advance");

        // ---- the zero-motion guard. rsqrt(0) is +inf, and inf * a direction is a NaN quad: the particle
        // silently vanishes rather than standing still.
        check.Expect(Rsqrt(0f) == 0f && Rsqrt(4f) == 0.5f, "a stationary motion-blurred particle normalises to zero, not NaN");

        // ---- #148's softness curve, through the stage's own SoftnessFor. The two engine branches meet at
        // S = 1, and the collapsed form has to agree with both of them or every quad either never softens
        // or softens over its whole length. The expectations are the engine's two branches written out by
        // hand: 1/(0.2*S) below S = 1, a flat 5.0 at and above it.
        check.Expect(SoftnessFor(false, 0, 0.5f) == 1f / (0.2f * 0.5f),
          "below S = 1 the curve is the engine's 1/(0.2*S)");
        check.Expect(SoftnessFor(false, 0, 4f) == 5f && SoftnessFor(false, 0, 1f) == 5f,
          "at and above S = 1 it is the engine's flat 5.0");
        check.Expect(1f / SoftnessFor(false, 0, 0.5f) == 0.1f && 1f / SoftnessFor(false, 0, 2f) == 0.2f,
          "...which is a fade distance that grows with the quad and caps at 0.2 world units");
        check.Expect(SoftnessFor(true, 0, 1f) == 0f && SoftnessFor(false, 1, 1f) == 0f
          && SoftnessFor(false, 2, 1f) == 0f && SoftnessFor(false, 0, 1f) != 0f
          && SoftnessFor(false, 6, 1f) != 0f && SoftnessFor(false, 0, 0f) == 0f,
          "splats, render types 1 and 2 and a zero-extent emitter soften not at all; nothing else is exempt");
        check.Expect(Norm(Vector3.Zero) == Vector3.Zero, "a degenerate axis normalises to zero rather than to NaN");
        // #261: the corner table, which is the CPU winding this stage replaced. A/B ride the NEAR centre
        // and C/D the far one -- get that pairing backwards and a motion-blurred streak fades across itself
        // instead of along, which reads as "particles look wrong" and nothing narrower.
        check.Expect(ParticleVsMain.Contains("vec2( 1.0,  1.0), vec2(-1.0,  1.0), vec2( 1.0, -1.0)")
          && ParticleVsMain.Contains("vec2( 1.0, -1.0), vec2(-1.0,  1.0), vec2(-1.0, -1.0)")
          && ParticleVsMain.Contains("bool near = sg.y > 0.0"),
          "the expansion keeps the engine's winding: (A, B, C) then (C, B, D), near end on +up");

        // ---- #148: the fragment stage's ORDER, which is the half of this that no arithmetic check can
        // reach. Every one of these is a real failure with a plausible-looking frame behind it: lighting a
        // fragment before the discard shades texels that are about to be thrown away; fading softness before
        // the discard makes a punch-through cutout dissolve instead of cut; fogging before either applies
        // distance twice.
        int iDiscard = ParticleFsMain.IndexOf("if (uAlphaCut >= 0.0", StringComparison.Ordinal);
        int iLit = ParticleFsMain.IndexOf("albedo *= vLight;", StringComparison.Ordinal);
        int iSoft = ParticleFsMain.IndexOf("if (uSoftOn != 0", StringComparison.Ordinal);
        int iEmis = ParticleFsMain.IndexOf("c.rgb *= uEmissive;", StringComparison.Ordinal);
        int iFog = ParticleFsMain.IndexOf("CxFogFactor(vWorld, vViewZ)", StringComparison.Ordinal);
        check.Expect(iDiscard >= 0 && iLit > iDiscard && iSoft > iLit && iEmis > iSoft && iFog > iEmis,
          "the fragment stage runs discard -> light -> soften -> emit -> fog, in that order");
        // ...and the decode, which since #132 is unconditional: the Alloy target is LINEAR, so every
        // particle's texel loses its display-space encode whether or not it is lit. A stage that kept the
        // decode inside the `uLit` branch would composite additive plumes with the wrong curve, and the
        // symptom is a plume that looks merely 'a bit off' rather than obviously broken.
        check.Expect(ParticleFsMain.Contains("vec3 albedo = CxLinear(c.rgb);")
          && !ParticleFsMain.Contains("CxRevert"),
          "the stage decodes to linear once, for every particle, and never re-encodes");
        // The three libraries the composed stage stands on. A rename upstream would otherwise surface as a
        // shader that fails to link at the first emitter, on the user's machine and not here.
        // #336: the rig runs in the vertex stage, which has no gl_FragCoord -- it must hand the cluster lookup
        // its own tile coordinate BEFORE the library that reads it composes in, and the library must take it.
        check.Expect(ParticleVsHead.Contains("#define CX_CLUSTER_XY cxClusterXY")
          && ParticleVsMain.Contains("if (uLit != 0)") && ParticleVsMain.Contains("cxClusterXY = (cc.xy")
          && ParticleVsMain.Contains("CxClusteredDiffuse(") && ParticleVsMain.Contains("CxSunShadow(")
          && AlloyLights.FragmentLib.Contains("#ifndef CX_CLUSTER_XY")
          && !ParticleFsMain.Contains("CxClusteredDiffuse(") && !ParticleFsMain.Contains("gl_FragCoord.xy - uTile"),
          "the light rig runs per vertex, on a cluster tile the vertex stage computes from its own NDC");
        check.Expect(AlloyLights.FragmentLib.Contains("vec3 CxClusteredDiffuse(")
          && AlloyLights.FragmentLib.Contains("vec3 CxLinear(")
          && AlloyLights.SunShadowLib.Contains("CxSunShadow(")
          && AlloyLights.SunShadowLib.Contains("(1.0 - uSunMedia)")
          && AlloyLights.LocalShadowLib.Contains("(1.0 - uLocalMedia)"),
          "the composed light libraries still declare what this stage calls");
        // #261: the stage expands from the record buffer, so what has to hold is the BINDINGS and the
        // record size -- a stage that names a different binding than the C# side binds draws nothing, and a
        // layout that drifts from RecFloats reads every particle's fields out of its neighbour.
        check.Expect(ParticleVsHead.Contains($"binding = {BindRecs}) readonly buffer CxParticleBuf")
          && ParticleVsHead.Contains($"binding = {BindIdx}) readonly buffer CxParticleIdx")
          && ParticleVsMain.Contains("gl_VertexID / 6"),
          "the vertex stage expands from the record and index buffers this pass binds");

        // ---- the classifier, and with it the ticket's punch-through requirement.
        check.Expect(ClsPunch < ClsBlend && ClsBlend < ClsAdd,
          "punch-through draws FIRST: it writes depth, so the blended classes get to test against it");

        // #131's ordering rule rests on exactly two things: groups are single-class, and their ranges
        // disjoint. Staged on the batching path: particles.batch=0 walks the capture order instead, so the
        // player's dial would otherwise decide whether these assertions run at all.
        bool batch0 = Batch.Boot;
        Batch.Boot = true;
        int[] cls0 = { ClsAdd, ClsBlend, ClsPunch, ClsBlend, ClsAdd, ClsPunch };
        for (int i = 0; i < cls0.Length; i++)
        {
            _sCls[i] = cls0[i]; _sTexObj[i] = (IntPtr)(1 + i); _sCull[i] = 0; _sEmis[i] = 1f;
            _sTaken[i] = false; _wrote[i] = 4 + i;
        }
        int gN = Group(cls0.Length);
        bool single = true, partition = true, monotone = true, flagged = true;
        int cur = 0, placed = 0, prevCls = -1;
        for (int gi = 0; gi < gN; gi++)
        {
            int gp = 0;
            for (int j = 0; j < _gCount[gi]; j++)
            {
                if (_sCls[_draw[_gFirst[gi] + j]] != _gCls[gi]) single = false;
                gp += _wrote[_gFirst[gi] + j];
            }
            // The index range, derived exactly as Draw derives it.
            if (_gFirst[gi] != placed) partition = false;
            placed += _gCount[gi];
            if (WillSort(_gCls[gi], gp) && _gCls[gi] != ClsBlend) flagged = false;
            if (_gCls[gi] < prevCls) monotone = false;
            prevCls = _gCls[gi];
            cur += gp;
        }
        check.Expect(single, "every draw group holds exactly one blend class -- a workgroup cannot mix additive with alpha-over");
        check.Expect(partition && placed == cls0.Length && cur > 0,
          "the groups partition the draw list: consecutive, disjoint, and covering every placed emitter");
        check.Expect(flagged
          && !WillSort(ClsPunch, 100) && !WillSort(ClsAdd, 100)
          && WillSort(ClsBlend, 2) && WillSort(ClsBlend, SortCap)
          && !WillSort(ClsBlend, 1) && !WillSort(ClsBlend, SortCap + 1),
          "only an alpha-blended group is ever reordered, and only up to the shared-memory cap -- "
          + "additive and punch-through keep their issue order, which is #131's whole ordering rule");
        check.Expect(monotone, "the groups come out class-major: punch, then blended, then additive");
        Batch.Boot = batch0;
        _drawN = 0; _orderN = 0;
        Array.Clear(_wrote, 0, cls0.Length);

        // ---- the toggle (#180). Two flags on purpose: the chat toggle must not be able to arm a client
        // where the pass never resolved, and collapsing them back into one would draw from unresolved
        // pointers.
        bool on0 = Enabled.V, off0 = _off;
        check.Expect(Enabled.Default, "the particle pass boots ON, as a plain launch did before the knob triage");
        check.Expect(Describe().Contains("grid") && Describe().Contains("guard"),
          "the bisect dials for the open bug are still reachable and reported");
        check.Expect(G.Get("nosuch") == null, "an unknown knob is refused rather than silently taken");
        _off = true; Enabled.V = true;
        check.Expect(_off && Stats(1).StartsWith("no particles (Alloy pass unavailable)"), "arming from chat cannot clear the resolution latch");
        _off = off0; Enabled.V = on0;

        // ---- the record's seventh vec4 (CX-48). The GLSL struct and the C# stride are one contract, and
        // the oracle for each is the other artifact: RecordSafe counts the declaration in the embedded
        // stage text against the stride the build writes. The negative case is a hand-written six-vec4
        // struct -- the record as it was before the previous centre joined it.
        check.Expect(ShaderContract.RecordSafe(ShaderSource.Get("cx_partrec.glsl"), "CxParticle", RecFloats),
          "the particle record's GLSL declaration adds up to the stride the build stages");
        check.Expect(RecFloats == 28, "...and that stride is seven vec4: the six the quad is expanded from, plus last frame's centre");
        check.Expect(!ShaderContract.RecordSafe(
            "struct CxParticle\n{\n vec4 a; vec4 b; vec4 c; vec4 d; vec4 e; vec4 f;\n};\n", "CxParticle", RecFloats),
          "a six-vec4 override is refused: it would read every record one vec4 short of the stride");
        // Last frame's centre, off hand-written positions: a slot with no previous frame behind it, a step,
        // and a step too large to be one particle's own movement (a recycled pool slot).
        check.Expect(PrevCentre(true, new Vector3(0f), new Vector3(5f, 6f, 7f)) == new Vector3(5f, 6f, 7f),
          "an emitter not built last frame takes this frame's centre: a zero step, not a jump from another frame's particles");
        check.Expect(PrevCentre(false, new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 4f)) == new Vector3(1f, 2f, 3f),
          "...a slot that was built last frame keeps the centre it held");
        check.Expect(PrevCentre(false, new Vector3(100f, 0f, 0f), new Vector3(1f, 2f, 3f)) == new Vector3(1f, 2f, 3f),
          "...and a step no particle could have taken is a recycled slot, which takes a zero step too");

        TintCheck(check);

        log.Info(check.Ok ? "Particles.SelfCheck: passed" : "Particles.SelfCheck: FAILED");
    }

    /// <summary>The emitter tint over a synthetic light rig. Oracle: RGB literals worked out by hand from the
    /// engine's own curve -- <c>1 - d / reach</c> per light, the area pair unattenuated, the sum clamped to 1.
    /// </summary>
    private static void TintCheck(Checker check)
    {
        bool amb0 = FrameInputs.HasAreaAmbient, dif0 = FrameInputs.HasAreaDiffuse;
        Vector3 ambWas = FrameInputs.AreaAmbient, difWas = FrameInputs.AreaDiffuse;
        FrameInputs.HasAreaAmbient = false; FrameInputs.HasAreaDiffuse = false;

        // One lamp 10 units away with 20 of reach: half its colour reaches the sample point.
        FrameInputs.BeginAreaLights(4);
        FrameInputs.RecordAreaLight((IntPtr)1, new Vector3(10f, 0f, 0f), 20f, new Vector3(0.5f, 0.25f, 0.125f));
        Vector3 t = SceneTint(Vector3.Zero);
        check.Expect(Near(t, new Vector3(0.25f, 0.125f, 0.0625f)),
          $"a lamp at half its reach tints with half its colour ({t})");

        // ...and one past its reach contributes nothing, which is what keeps a whole area's lamps off the sum.
        FrameInputs.RecordAreaLight((IntPtr)2, new Vector3(30f, 0f, 0f), 20f, new Vector3(1f, 1f, 1f));
        t = SceneTint(Vector3.Zero);
        check.Expect(Near(t, new Vector3(0.25f, 0.125f, 0.0625f)),
          $"a lamp past its own reach adds nothing ({t})");

        // The sum is clamped, not averaged: two lamps on top of the sample point cannot drive it past white.
        FrameInputs.BeginAreaLights(2);
        FrameInputs.RecordAreaLight((IntPtr)1, Vector3.Zero, 20f, new Vector3(0.8f, 0.8f, 0.8f));
        FrameInputs.RecordAreaLight((IntPtr)2, Vector3.Zero, 20f, new Vector3(0.8f, 0.8f, 0.8f));
        t = SceneTint(Vector3.Zero);
        check.Expect(Near(t, Vector3.One), $"two lamps at the sample point clamp to white rather than summing past it ({t})");

        // A reachless light: 0.9999 at its own position, and 0.01 of reach anywhere else.
        FrameInputs.BeginAreaLights(1);
        FrameInputs.RecordAreaLight((IntPtr)1, Vector3.Zero, 0f, new Vector3(1f, 0f, 0f));
        t = SceneTint(Vector3.Zero);
        check.Expect(MathF.Abs(t.X - 0.9999f) < 1e-4f, $"a reachless light at the sample point is all but full ({t.X})");
        t = SceneTint(new Vector3(0.005f, 0f, 0f));
        check.Expect(MathF.Abs(t.X - 0.5f) < 1e-3f, $"...and falls off over a hundredth of a unit ({t.X})");

        // the area ambient rides in unattenuated and authored: a linear 0.25 is 0.25^(1/2.2) = 0.5326
        FrameInputs.BeginAreaLights(0);
        FrameInputs.AreaAmbient = new Vector3(0.25f, 0f, 0f);
        FrameInputs.HasAreaAmbient = true;
        t = SceneTint(new Vector3(500f, 500f, 500f));
        check.Expect(MathF.Abs(t.X - 0.5326f) < 1e-3f && t.Y == 0f,
          $"the area ambient tints everywhere, at the authored value behind the rig's linear one ({t})");

        FrameInputs.BeginAreaLights(0);
        FrameInputs.AreaAmbient = ambWas; FrameInputs.AreaDiffuse = difWas;
        FrameInputs.HasAreaAmbient = amb0; FrameInputs.HasAreaDiffuse = dif0;
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;
}
