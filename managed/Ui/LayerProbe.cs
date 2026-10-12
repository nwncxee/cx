using CX.Framework;

namespace CX.Ui;

/// <summary>A debug aid for the window layers: with <c>ui.layerprobe</c> on, two flat rectangles are drawn, a red one
/// on the below-NUI layer and a cyan one on the above-NUI layer, placed to overlap a NUI window opened at
/// (400, 260) 300x200. A screenshot then shows which side of that window each layer is on, with Alloy on and off.</summary>
public sealed class LayerProbe : IExtenderService
{
    private sealed class ProbeState
    {
        public VertexBatch? Batch;
    }

    private static ProbeState _state = new();

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        if (!UiKnobs.LayerProbe.Boot) return;
        new Window(0, 0, 0, 0) { Focusable = false, Layer = UiLayer.BelowNui, DrawContent = () => Draw(360, 240, 560, 340, 1f, 0.1f, 0.1f) };
        new Window(0, 0, 0, 0) { Focusable = false, Layer = UiLayer.AboveNui, DrawContent = () => Draw(540, 380, 740, 480, 0.1f, 0.9f, 0.9f) };
        log.Info("LayerProbe: a red rectangle below NUI and a cyan one above it are drawn (ui.layerprobe)");
    }

    /// <summary>One flat rectangle in screen px through the shared text program.</summary>
    private static void Draw(float x0, float y0, float x1, float y1, float r, float g, float b)
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || !TextRig.Ready) return;
        VertexBatch batch = _state.Batch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        batch.Reset(atlas.SolidU, atlas.SolidV);
        batch.Rect(x0, y0, x1, y1, r, g, b, 1f);
        TextRig.DrawBatch(batch.AsSpan(), batch.VertexCount);
    }
}
