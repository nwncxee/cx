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
    /// <summary>A hand-written stage of the shape a dye shader has: a palette pick off a row of its atlas on
    /// <c>texUnit8</c>, the baked texel without one, and a glow edit after <c>ApplyStandardShader</c>.</summary>
    private const string GuiTwinStage =
        "#define SHADER_TYPE 2\n#define LIGHTING 1\n#include \"inc_standard\"\n"
      + "uniform float pick; uniform float glow;\nuniform lowp int texture8Bound;\nuniform sampler2D texUnit8;\n"
      + "void main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
      + "    vec3 display = RevertColorSpace(FragmentColor.rgb);\n"
      + "    if (pick >= 0.0) display = texture8Bound != 0 ? texelFetch(texUnit8, ivec2(0, int(pick)), 0).rgb : vec3(1.0, 0.0, 1.0);\n"
      + "    FragmentColor.rgb = ApplyColorSpace(display);\n    ApplyStandardShader();\n    FragmentColor.a = 1.0;\n"
      + "    FragmentColor.rgb += display * max(glow, 0.0);\n    gl_FragColor = FragmentColor;\n}\n";

    /// <summary>A hand-written stage that reads the engine's noise unit and its flag, the shape a water stage has.</summary>
    private const string GuiTwinNoiseStage =
        "#define SHADER_TYPE 2\n#define LIGHTING 1\n#include \"inc_standard\"\nuniform sampler2D texUnitNoise;\n"
      + "void main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
      + "    if (texNoiseBound == 0) FragmentColor.rgb *= texture(texUnitNoise, vec2(0.0)).rgb;\n"
      + "    ApplyStandardShader();\n    gl_FragColor = FragmentColor;\n}\n";

    /// <summary>One case: the object's values (null = its row is gone), the slot mask, and the colour worked out by
    /// hand from the three textures below.</summary>
    private readonly record struct GuiTwinCase(string What, string? Pick, string? Glow, int Slots, byte R, byte G, byte B, bool Due = false);

    /// <summary>Content draws through the whole GUI flush into an image: the stage translated into the GUI's program, its
    /// row off the live table and its maps bound by unit. Oracle: three hand-written textures and the colours above.</summary>
    private static void GuiTwinSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        int palBit = AlloyRenderer.PalSlotBit(1);
        GuiTwinCase[] cases =
        [
            new("the object's pick reads its atlas row", "1", null, palBit, 0, 64, 0),
            new("another object's pick reads another row", "2", null, palBit, 0, 0, 64),
            new("a pick of -1 keeps the baked texel", "-1", null, palBit, 200, 0, 0),
            new("a part with no row reads its uniform's GLSL default, 0: atlas row 0", null, null, palBit, 9, 9, 9),
            new("the edit after ApplyStandardShader runs on the shaded colour", "1", "1", palBit, 0, 128, 0),
            new("the slot mask reaches texture8Bound", "1", null, 0, 255, 0, 255),
            // a real recipe slot with no material behind it: its due fill states nothing, so pick falls to its default, 0
            new("a row due a fill is filled by the flush, with no world frame to do it", "1", null, palBit, 9, 9, 9, Due: true),
        ];
        const int Side = 4;
        // the opposite of the stage's own declaration order: the twin reads the lanes the rows were laid out with
        ContentStage.Param[] lanes = [new("glow", ContentStage.ParamKind.Float, 0), new("pick", ContentStage.ParamKind.Float, 1)];
        MtrDef mtr = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("parameter float pick -1\nparameter float glow -1\n"), "golden_twin");
        var chain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["golden_twin_f"] = GuiTwinStage };
        GuiContent.VkPair? pair = GuiContent.VkTwin(vk, "vslit", "golden_twin_f", lanes, n => chain.GetValueOrDefault(n), log);
        check.Expect(pair != null, "the GUI twin of a dye stage was refused (logged above)");
        if (pair == null) return;
        chain["golden_noise_f"] = GuiTwinNoiseStage;
        check.Expect(GuiContent.VkTwin(vk, "vslit", "golden_noise_f", [], n => chain.GetValueOrDefault(n), log) != null,
                     "the GUI twin of a stage reading the engine's noise unit was refused (logged above)");
        int key = GuiContent.Key(MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("customshadervs vslit\ncustomshaderfs golden_twin_f\n"), "golden_twin"));
        GuiPassVk.TwinForCheck(vk, key, pair);

        VkTextures.DeviceForCheck(vk);
        uint baked = GuiTwinTexture(vk, [200, 0, 0, 255], 1, "baked");
        uint atlas = GuiTwinTexture(vk, [9, 9, 9, 255, 0, 64, 0, 255, 0, 0, 64, 255], 3, "atlas");
        uint decoy = GuiTwinTexture(vk, [255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255], 3, "decoy");
        int w = Side * cases.Length;
        ImageId target = PostImage(vk, Format.Rgba8, w, Side, ImageUse.ColorTarget | ImageUse.CopySrc, "golden gui twin");
        BufferId rb = vk.CreateBuffer(new BufferDesc(w * Side * 4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden gui twin read"));
        var rows = new int[cases.Length];
        Span<float> tri = [-1f, -1f, 0f, 0f, 0f, 3f, -1f, 0f, 2f, 0f, -1f, 3f, 0f, 0f, 2f];
        GuiCapture.Reset();
        for (int i = 0; i < cases.Length; i++)
        {
            GuiTwinCase c = cases[i];
            rows[i] = -1;
            if (c.Pick != null)
            {
                rows[i] = ContentTable.Alloc(GuiTwinOwner(in c, i));
                var overs = new List<Seam.MaterialParams.ContentOverride> { new("", "pick", c.Pick) };
                if (c.Glow != null) overs.Add(new("", "glow", c.Glow));
                ContentTable.FillRow(rows[i], mtr, lanes, overs);
            }
            GuiCapture.BeginRawLayer(i * Side, 0, Side, Side, i * Side, 0, Side, Side);
            fixed (float* v = tri)
                GuiCapture.Raw((byte*)v, GuiCapture.TextVertexStride, GuiStream.FmtPos | GuiStream.FmtUv0, null, 3, 0, EngineTokens.TRIANGLES,
                               Matrix4x4.Identity, (int)baked, AlloyRenderer.GlOne, AlloyRenderer.GlZero);
            ref GuiCapture.Draw d = ref GuiCapture.Draws[GuiCapture.DrawN - 1];
            d.Content = key;
            d.ConAt = GuiCapture.Con.N;
            ref GuiCapture.ConDraw con = ref GuiCapture.Con.Draws[GuiCapture.Con.N++];
            con = default;
            con.Row = rows[i];
            con.Slots = c.Slots;
            con.Maps[7] = (int)atlas;
            con.Maps[8] = (int)decoy;
        }
        bool flushed = GuiPassVk.FlushOn(vk, 0, GuiCapture.LayerN, GuiCapture.LayerN, false, log, target, clearBlack: true);
        check.Expect(flushed, "the GUI flush holding the twin's draws was refused (logged above)");
        byte[]? px = flushed ? PostRead(vk, log, target, w, Side, 4, rb) : null;
        if (px != null)
            for (int i = 0; i < cases.Length; i++)
                GuiTwinTexel(check, px, w, Side, i, cases[i]);
        // the panel's case: the doll stays on screen, its values change, and the next flush shows them
        ContentTable.FillRow(rows[0], mtr, lanes, [new("", "pick", "2")]);
        bool again = GuiPassVk.FlushOn(vk, 0, GuiCapture.LayerN, GuiCapture.LayerN, false, log, target, clearBlack: true);
        px = again ? PostRead(vk, log, target, w, Side, 4, rb) : null;
        check.Expect(px != null, "the second flush was refused (logged above)");
        if (px != null) GuiTwinTexel(check, px, w, Side, 0, new("values changed since the last flush reach the next one", "2", null, palBit, 0, 0, 64));
        GuiCapture.Reset();
        for (int i = 0; i < cases.Length; i++)
            if (rows[i] >= 0) ContentTable.Free(rows[i], GuiTwinOwner(in cases[i], i));
        vk.Destroy(rb);
        vk.Destroy(target);
        GuiPassVk.ContextLost();
        VkTextures.DeviceForCheck(null);
        log.Info($"GpuGolden: GUI twin, {cases.Length} cases");
    }

    /// <summary>The recipe owning case <paramref name="i"/>'s row: past the table, so a flush leaves it as filled by hand,
    /// unless the case is about the flush's own fill.</summary>
    private static int GuiTwinOwner(in GuiTwinCase c, int i) => c.Due ? DrawRecipe.Max - 1 : int.MaxValue - 10 - i;

    /// <summary>Case <paramref name="i"/>'s column of the flushed image against its hand-worked colour.</summary>
    private static void GuiTwinTexel(Checker check, byte[] px, int w, int side, int i, in GuiTwinCase c)
    {
        int at = (1 * w + i * side + 1) * 4;
        byte r = px[at], g = px[at + 1], b = px[at + 2];
        check.Expect(Math.Abs(r - c.R) <= 2 && Math.Abs(g - c.G) <= 2 && Math.Abs(b - c.B) <= 2,
                     $"{c.What}: drew ({r}, {g}, {b}), not ({c.R}, {c.G}, {c.B})");
    }

    /// <summary>A one-wide texture of hand-written RGBA rows under a nearest sampler, as a GL name the GUI path resolves.</summary>
    private static uint GuiTwinTexture(VkBackend vk, ReadOnlySpan<byte> rgba, int h, string label)
    {
        uint name = VkTextures.NewName();
        ImageId img = VkTextures.Create(vk, name, Format.Rgba8, 1, h, 1, false,
                                        new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp), $"golden gui twin {label}");
        vk.Upload(img, 0, 0, rgba);
        return name;
    }
}
