using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The sky's roles, from the scene database</b> -- the part of the sky that is STATE rather than
/// geometry. The branch structure is <c>Scene::RenderSkyBoxes</c>'s, term for term: the off byte, "no current
/// sky", the fade epsilon on the desired one, and a null cross-fade object; miss one and the pass paints a sky
/// into an interior. A role's geometry stages through the ordinary record path.</summary>
internal static class DbSky
{
    /// <summary>One sky role: which object draws, at what alpha, with which blend pair, keyed on a database
    /// handle.</summary>
    internal struct Role
    {
        public DbId Obj;
        public float Alpha;
        public int Src, Dst;
    }

    /// <summary><c>Aurora::AuroraBlendModes</c> as the sky uses them. The current sky REPLACES (src alpha,
    /// dst zero) scaled by (1-t); the incoming one and the cross-fade object draw as an ordinary OVER blend.
    /// Aurora 1 and 2 are easy to read the wrong way round, and the difference is a sky that brightens
    /// through every cross-fade.</summary>
    private const int BlendSrcAlpha = 0, BlendZero = 3, BlendOverAlpha = 1;

    /// <summary>Roles one frame can have: the current sky, the one fading in over it, and the cross-fade
    /// object.</summary>
    internal const int MaxRoles = 3;

    /// <summary>This scene's sky roles, in draw order. Returns the count written into
    /// <paramref name="into"/>; 0 means the scene draws no sky at all, which is an interior and not a
    /// failure.</summary>
    internal static int Roles(DbId scene, Span<Role> into)
    {
        if (!SceneDb.Scenes.Valid(scene)) return 0;
        int s = scene.Index;
        // The engine's own off byte -- note the polarity: SceneSkyOff set means the sky is NOT drawn.
        if (SceneDb.SceneSkyOff[s]) return 0;
        float t = SceneDb.SceneSkyT[s];
        int cur = SceneDb.SceneSkyCur[s], want = SceneDb.SceneSkyDesired[s];
        int n = 0;
        if (cur != -1 && n < into.Length)
        {
            DbId o = SceneDb.SceneSky(scene, cur);
            if (SceneDb.Objects.Valid(o))
                into[n++] = new Role { Obj = o, Alpha = 1f - t, Src = BlendSrcAlpha, Dst = BlendZero };
        }
        // The fade epsilon is the engine's: a desired sky at t == 0 has not started crossing yet.
        if (t > 0f && want != -1 && n < into.Length)
        {
            DbId o = SceneDb.SceneSky(scene, want);
            if (SceneDb.Objects.Valid(o))
                into[n++] = new Role { Obj = o, Alpha = t, Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        }
        DbId fade = SceneDb.SceneSkyFade[s];
        if (SceneDb.Objects.Valid(fade) && n < into.Length)
            into[n++] = new Role { Obj = fade, Alpha = SceneDb.ObjTint[fade.Index].W,
                                   Src = BlendSrcAlpha, Dst = BlendOverAlpha };
        return n;
    }

    /// <summary>Off-device: the engine's four gates, which is the whole of what this owns. Each one wrong is
    /// a specific, silent misrender -- a sky in an interior, a missing dome, a cross-fade that brightens, or
    /// a horizon band drawn at the wrong alpha.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSky.SelfCheck");
        DbId scene = SceneDb.Scenes.Alloc();
        int s = scene.Index;
        var objs = new DbId[4];
        for (int k = 0; k < 4; k++)
        {
            objs[k] = SceneDb.Objects.Alloc();
            DbFeed.InitObject(objs[k].Index);
            SceneDb.SetSceneSky(scene, k, objs[k]);
        }
        DbId fade = SceneDb.Objects.Alloc();
        DbFeed.InitObject(fade.Index);
        Span<Role> roles = stackalloc Role[MaxRoles];

        // A settled sky: one role, the current one, at full alpha and REPLACING.
        SceneDb.SceneSkyOff[s] = false;
        SceneDb.SceneSkyCur[s] = 1; SceneDb.SceneSkyDesired[s] = -1; SceneDb.SceneSkyT[s] = 0f;
        SceneDb.SceneSkyFade[s] = default;
        int n = Roles(scene, roles);
        check.Expect(n == 1 && roles[0].Obj.Value == objs[1].Value && roles[0].Alpha == 1f,
                     "a settled sky is one role, the current box, at full alpha");
        check.Expect(roles[0].Src == BlendSrcAlpha && roles[0].Dst == BlendZero,
                     "...drawn as a REPLACE, not an over-blend (an over-blend brightens every cross-fade)");

        // Mid cross-fade: two roles, (1-t) then t, the incoming one over-blended.
        SceneDb.SceneSkyDesired[s] = 2; SceneDb.SceneSkyT[s] = 0.25f;
        n = Roles(scene, roles);
        check.Expect(n == 2, "a cross-fade is two roles");
        check.Expect(MathF.Abs(roles[0].Alpha - 0.75f) < 1e-5f && MathF.Abs(roles[1].Alpha - 0.25f) < 1e-5f,
                     "...the outgoing at 1-t and the incoming at t");
        check.Expect(roles[1].Obj.Value == objs[2].Value && roles[1].Dst == BlendOverAlpha,
                     "...and the incoming one over-blends");

        // The fade epsilon: a desired sky at t == 0 has not started.
        SceneDb.SceneSkyT[s] = 0f;
        n = Roles(scene, roles);
        check.Expect(n == 1, "a desired sky at t = 0 has not started crossing");

        // The off byte wins over everything -- this is the gate that paints a sky into an interior.
        SceneDb.SceneSkyOff[s] = true;
        SceneDb.SceneSkyT[s] = 0.5f;
        check.Expect(Roles(scene, roles) == 0, "the off byte draws no sky at all, whatever else is set");
        SceneDb.SceneSkyOff[s] = false;

        // No current sky: the engine's -1, which is not index 0.
        SceneDb.SceneSkyCur[s] = -1; SceneDb.SceneSkyDesired[s] = -1; SceneDb.SceneSkyT[s] = 0f;
        check.Expect(Roles(scene, roles) == 0, "'no current sky' is -1 and draws nothing, not box 0");

        // The cross-fade object is its own role, at its own alpha, and is independent of the boxes.
        SceneDb.ObjTint[fade.Index] = new Vector4(1f, 1f, 1f, 0.4f);
        SceneDb.SceneSkyFade[s] = fade;
        n = Roles(scene, roles);
        check.Expect(n == 1 && roles[0].Obj.Value == fade.Value && MathF.Abs(roles[0].Alpha - 0.4f) < 1e-5f,
                     "the cross-fade object is a role of its own at its own alpha");

        // A destroyed skybox is not a role: the handle resolves to nothing, with no sweep.
        SceneDb.SceneSkyFade[s] = default;
        SceneDb.SceneSkyCur[s] = 1;
        SceneDb.Objects.Free(objs[1]);
        check.Expect(Roles(scene, roles) == 0, "a destroyed skybox stops being a role at once");

        for (int k = 0; k < 4; k++) if (SceneDb.Objects.Valid(objs[k])) SceneDb.Objects.Free(objs[k]);
        SceneDb.Objects.Free(fade);
        SceneDb.Scenes.Free(scene);
        SceneDb.Objects.TakeDirty(); SceneDb.Scenes.TakeDirty();

        log.Info(check.Ok ? "DbSky.SelfCheck: passed"
                          : "DbSky.SelfCheck: FAILED -- a DB-mode sky would be missing, doubled, or painted "
                          + "into an interior");
        return check.Ok;
    }
}
