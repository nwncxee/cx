using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The two camera derivations the scene feed and the shadow fit share, plus the engine's own
/// area-light uniforms for the frame diff. <c>SceneFeed.TickFromView</c> fills the rest of
/// <c>FrameInputs</c> from the scene database.</summary>
internal static unsafe class FrameRead
{
    /// <summary>Accept only clearly downward-travelling light as the sun (NWN is Z-up). A horizontal or
    /// below-horizon directional would light undersides and read as garbage in the cascades.</summary>
    internal const float DownwardZ = -0.05f;

    /// <summary>World-space direction the camera looks along: view space looks down -Z (OpenGL), and the
    /// camera quaternion is the view-&gt;world rotation (<c>SetViewTransform</c> feeds
    /// <c>MakeMatrix4x4</c> the CONJUGATE, <c>operator-</c> @0x6b5d50 negating only the scalar).</summary>
    internal static Vector3 ViewForward(Quaternion rot) => Vector3.Transform(new Vector3(0f, 0f, -1f), rot);

    /// <summary>The projection's <c>p11</c> = <c>1 / tan(fovy / 2)</c>, from a view angle in DEGREES (which
    /// is what <c>Camera::GetViewAngle</c> holds -- <c>SetPerspectiveTransform</c> takes degrees).</summary>
    internal static float P11(float fovyDegrees)
    {
        float half = fovyDegrees * (MathF.PI / 360f);
        float t = MathF.Tan(half);
        return t > 1e-6f ? 1f / t : 0f;
    }

    /// <summary>The engine's OWN area-light uniforms, straight out of the globals
    /// <c>SetAreaDiffuseLight</c>/<c>SetAreaAmbientLight</c> store into, unscaled. What the scene feed
    /// derives should equal these, and <c>/alloy framediff</c> prints the two side by side. Reads nothing of
    /// Alloy's, so it answers in an <c>alloy.enabled=0</c> session too.</summary>
    internal static string EngineAreaTerms()
    {
        float* dif = (float*)Native.Sym(SymbolMap.LightAreaDiffuse);
        float* amb = (float*)Native.Sym(SymbolMap.LightAreaAmbient);
        float* dir = (float*)Native.Sym(SymbolMap.LightAreaDiffuseDir);
        if (dif == null || amb == null || dir == null) return "area uniforms unresolved";
        return $"diffuse=({dif[0]:F3},{dif[1]:F3},{dif[2]:F3}) ambient=({amb[0]:F3},{amb[1]:F3},{amb[2]:F3}) " +
               $"dir=({dir[0]:F3},{dir[1]:F3},{dir[2]:F3})";
    }

    /// <summary>Pure-math check of the two derivations that have no other witness: the view forward the
    /// camera quaternion gives, and the projection term its view angle gives. Headless-safe.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        // Identity: the camera looks down -Z, i.e. straight down in NWN's Z-up world.
        Vector3 f = ViewForward(Quaternion.Identity);
        ok &= MathF.Abs(f.Z + 1f) < 1e-4f && MathF.Abs(f.X) < 1e-4f && MathF.Abs(f.Y) < 1e-4f;
        // A quarter turn about X tips that forward onto +Y (right-handed, Z-up).
        Vector3 t = ViewForward(Quaternion.CreateFromAxisAngle(new Vector3(1f, 0f, 0f), MathF.PI / 2f));
        ok &= MathF.Abs(t.Y - 1f) < 1e-4f && MathF.Abs(t.Z) < 1e-4f;
        // 90 degrees of vertical FOV puts tan(45) = 1 in the denominator.
        ok &= MathF.Abs(P11(90f) - 1f) < 1e-4f;
        ok &= P11(0f) == 0f;
        log.Info(ok ? "FrameRead.SelfCheck: ok (view forward, p11)"
                    : "FrameRead.SelfCheck: FAILED -- the camera quaternion or FOV convention drifted");
    }
}
