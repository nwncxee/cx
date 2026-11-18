using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class GpuGolden
{
    /// <summary>A hand-written stage of the shape a tint shader has: it sets its reflectivity late and, under the engine's
    /// SPECULAR_LIGHT, asks for its shine to be rebuilt off the dye it paints. A dye or reflectivity of -1 is left alone.</summary>
    private const string GuiSpecStage =
        "#define SHADER_TYPE 2\n#define LIGHTING 1\n#define ENVIRONMENT_MAP 1\n#include \"inc_standard\"\n"
      + "uniform float dye; uniform float env;\n"
      + "void main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
      + "    if (env >= 0.0) fEnvMapLevel = env;\n"
      + "    if (dye >= 0.0)\n    {\n        FragmentColor.rgb = dye < 0.5 ? vec3(0.5, 0.1, 0.0) : vec3(0.01);\n"
      + "#if SPECULAR_LIGHT == 1\n        SetupSpecularity(FragmentColor.rgb * materialFrontDiffuse.rgb);\n#endif\n    }\n"
      + "    ApplyStandardShader();\n    gl_FragColor = FragmentColor;\n}\n";

    /// <summary>One lit case: the stage's two values, the light (the engine's fixed key, or a server's), and the colour
    /// worked out by hand.</summary>
    private readonly record struct GuiSpecCase(string What, string Dye, string Env, bool Server, byte R, byte G, byte B);

    /// <summary>The fixed key's direction, and the normal halfway between it and the eye, so its highlight peaks at the
    /// sampled texel: N.H = 1, N.L = N.V = 0.947377.</summary>
    private static readonly Vector3 GuiSpecKey = Vector3.Normalize(new Vector3(0.35f, 0.5f, 0.8f)),
                                    GuiSpecHalf = Vector3.Normalize(GuiSpecKey + Vector3.UnitZ);

    /// <summary>Lit content draws through the whole GUI flush: the stage above in the GUI's program on a part the model view
    /// lights. Oracle: inc_material.shd's SetupSpecularity and inc_lighting.shd's ComputeLighting worked by hand at each
    /// case's geometry (the arithmetic is in each case's comment), over the baked texel (200, 0, 0).</summary>
    private static void GuiTwinSpecSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        GuiSpecCase[] cases =
        [
            // Given a character whose hair is dyed dark red by a shader that rebuilds its own shine
            // When the character sheet shows it under the model view's key light with Alloy on
            // Then the hair shines like metal, and its highlight is dark red
            // spec 0.98, rough 0.417188, metal 1, so the specular colour is the dye; lobe x base 1.498165 (both Smith terms at
            // 0.947377), light 0.75 x 0.947377 x it + lit 0.960533 x grey 0.4098 x 0.98 = 1.450249: (0.5, 0.1, 0) x 1.469460.
            new("a dyed metal shines in its own colour under the key light", "0", "0.125", false, 222, 107, 0),
            // Given a character wearing cloth dyed black by a shader that rebuilds its own shine
            // When the character sheet shows it under the model view's key light with Alloy on
            // Then the cloth stays black with almost no highlight, and does not read grey
            // spec 0.069490, rough 0.545833, metal 0.294902: specular colour 0.035352 off the dye's peak over mid-grey 0.2176,
            // so 0.01 x 0.893785 + 0.035352 x 0.070838 re-encodes to 33.4; a white specular colour would draw it at 80.8.
            new("a black dye takes almost no highlight", "1", "0.0039216", false, 33, 33, 33),
            // Given a part whose shader neither sets its reflectivity nor asks for its shine to be rebuilt
            // When the character sheet shows it with Alloy on, before and after this change
            // Then it looks the same
            // the baked texel under the key light alone: 200 x 0.960533^(1/2.2)
            new("a stage that touches neither draws as it did", "-1", "-1", false, 196, 0, 0),
            // N = V = L = +z, the server's colour 0.5, ambient 0.1: 0.5 x 1.407679 + 0.6 x 0.4098 x 0.98 = 0.944802, (0.5, 0.1, 0) x 0.956802.
            new("a server's light makes the highlight from its own direction and colour", "0", "0.125", true, 182, 88, 0),
        ];
        const int Side = 4;
        ContentStage.Param[] lanes = [new("dye", ContentStage.ParamKind.Float, 0), new("env", ContentStage.ParamKind.Float, 1)];
        MtrDef mtr = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("parameter float dye -1\nparameter float env -1\n"), "golden_spec");
        var chain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["golden_spec_f"] = GuiSpecStage };
        GuiContent.VkPair? pair = GuiContent.VkTwin(vk, "vslit", "golden_spec_f", lanes, n => chain.GetValueOrDefault(n), log);
        check.Expect(pair != null, "the GUI twin of a stage that rebuilds its shine was refused (logged above)");
        if (pair == null) return;
        int key = GuiContent.Key(MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("customshadervs vslit\ncustomshaderfs golden_spec_f\n"), "golden_spec"));
        GuiPassVk.TwinForCheck(vk, key, pair);

        VkTextures.DeviceForCheck(vk);
        uint baked = GuiTwinTexture(vk, [200, 0, 0, 255], 1, "spec baked");
        int w = Side * cases.Length;
        ImageId target = PostImage(vk, Format.Rgba8, w, Side, ImageUse.ColorTarget | ImageUse.CopySrc, "golden gui twin spec");
        BufferId rb = vk.CreateBuffer(new BufferDesc(w * Side * 4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden gui twin spec read"));
        var rows = new int[cases.Length];
        // normal, position, uv per vertex: the engine's interleave for FmtNormal | FmtPos | FmtUv0
        Span<float> tri = [0f, 0f, 1f, -1f, -1f, 0f, 0f, 0f, 0f, 0f, 1f, 3f, -1f, 0f, 2f, 0f, 0f, 0f, 1f, -1f, 3f, 0f, 0f, 2f];
        GuiCapture.Reset();
        for (int i = 0; i < cases.Length; i++)
        {
            GuiSpecCase c = cases[i];
            rows[i] = ContentTable.Alloc(int.MaxValue - 30 - i);
            ContentTable.FillRow(rows[i], mtr, lanes, [new("", "dye", c.Dye), new("", "env", c.Env)]);
            GuiCapture.BeginRawLayer(i * Side, 0, Side, Side, i * Side, 0, Side, Side);
            fixed (float* v = tri)
                GuiCapture.Raw((byte*)v, 32, GuiStream.FmtNormal | GuiStream.FmtPos | GuiStream.FmtUv0, null, 3, 0, EngineTokens.TRIANGLES,
                               Matrix4x4.Identity, (int)baked, AlloyRenderer.GlOne, AlloyRenderer.GlZero);
            GuiSpecLight(ref GuiCapture.Records[GuiCapture.DrawN - 1], c.Server);
            ref GuiCapture.Draw d = ref GuiCapture.Draws[GuiCapture.DrawN - 1];
            d.Content = key;
            d.ConAt = GuiCapture.Con.N;
            ref GuiCapture.ConDraw con = ref GuiCapture.Con.Draws[GuiCapture.Con.N++];
            con = default;
            con.Row = rows[i];
        }
        bool flushed = GuiPassVk.FlushOn(vk, 0, GuiCapture.LayerN, GuiCapture.LayerN, GuiCapture.LayerN, false, log, target, clearBlack: true);
        check.Expect(flushed, "the GUI flush holding the lit twin's draws was refused (logged above)");
        byte[]? px = flushed ? PostRead(vk, log, target, w, Side, 4, rb) : null;
        if (px != null)
            for (int i = 0; i < cases.Length; i++)
            {
                GuiSpecCase c = cases[i];
                GuiTwinTexel(check, px, w, Side, i, new GuiTwinCase(c.What, null, null, 0, c.R, c.G, c.B));
            }
        GuiCapture.Reset();
        for (int i = 0; i < cases.Length; i++) ContentTable.Free(rows[i], int.MaxValue - 30 - i);
        vk.Destroy(rb);
        vk.Destroy(target);
        GuiPassVk.ContextLost();
        VkTextures.DeviceForCheck(null);
        log.Info($"GpuGolden: GUI twin specular, {cases.Length} cases");
    }

    /// <summary>Light record <paramref name="rec"/> as the model view does: the fixed key over a normal halfway to it with
    /// ambient 0 and diffuse 1, or a server's light from the eye, colour 0.5 and ambient 0.1, on a normal facing it. The
    /// model-view matrix puts every fragment one metre in front of the eye, so the view direction is +z throughout.</summary>
    private static void GuiSpecLight(ref GuiRec rec, bool server)
    {
        Vector3 n = server ? Vector3.UnitZ : GuiSpecHalf;
        rec.Mv = new Matrix4x4(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, n.X, n.Y, n.Z, 0f, 0f, 0f, -1f, 1f);
        rec.Colour = server ? new Vector4(0f, 0f, 1f, 0f) : Vector4.One;
        rec.Ambient = server ? new Vector4(0.1f, 0.1f, 0.1f, 1f) : new Vector4(0f, 0f, 0f, 1f);
        rec.Diffuse = server ? new Vector4(0.5f, 0.5f, 0.5f, 1f) : Vector4.One;
        rec.Emissive = new Vector4(0f, 0f, 0f, 1f);
        rec.Flags = (int)rec.Flags | GuiCapture.FlagLit | (server ? GuiCapture.FlagServerLight : 0);
    }
}
