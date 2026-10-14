using System.Collections.Generic;
using CX.Ui;

using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

/// <summary>One piece of the native look: a <see cref="NativeChrome"/> part over a rect, tinted <c>rrggbbaa</c>.
/// Laid out cold with the rest of the frame; the hot draw replays it through the live <see cref="Skin"/>.</summary>
public readonly record struct SkinCmd(int Part, float X0, float Y0, float X1, float Y1, uint Rgba);

/// <summary>One built chatbox frame: the interleaved verts + the metadata the animated draw needs. The
/// body slice [<see cref="BodyStart"/>,<see cref="BodyEnd"/>) is the only part that fades/slides and is
/// clipped to the body rect; everything outside it is static chrome. Built cold, drawn hot.</summary>
public sealed class RenderFrame
{
    public float[] Verts = System.Array.Empty<float>();
    public int Count;
    public List<PortraitQuad> Portraits = new();
    public List<LineSpanVerts> Spans = new();
    /// <summary>The native look's game art for this frame, empty in the flat look. Commands below
    /// <see cref="SkinOver"/> draw under the body, the rest over it (the open channel list).</summary>
    public SkinCmd[] Skin = System.Array.Empty<SkinCmd>();
    public int SkinOver;
    public int BodyStart, BodyEnd;    // vertex indices bounding the animated body slice
    public float Bx, By, Bw, Bh;      // body rect (px, y-down) -- the scissor for the body draw
    public bool Scrolled;             // active tab scrolled up (ScrollPx != 0): freeze the fade -- reading history
    public bool ApplyResidual;       // true for the focused window (slide/scroll ease); others snap content in
    // Per-window animation settings (carried here so the hot draw reads frame-local values, not shared statics).
    public bool AnimEnabled;
    public double HoldSec, FadeSec;
    public float TauMs;
    public System.DateTime LastScroll;
    /// <summary>Vertex range for unread-tab blink borders (start..end indices). Alpha is modulated at draw time.</summary>
    public int BlinkStart, BlinkEnd;
    /// <summary>Vertex ranges of the tab hover plates: the entered tab eases in from <see cref="HoverSince"/>, the
    /// left tab eases out from <see cref="LeftSince"/>; both over <see cref="HoverMs"/>.</summary>
    public int HoverStart, HoverEnd, LeftStart, LeftEnd;
    public System.DateTime HoverSince, LeftSince;
    /// <summary>Tab hover ease length; short so the bar feels snappy.</summary>
    public const float HoverMs = 90f;
    /// <summary>Ease progress in [0,1] of a hover slice that started at <paramref name="since"/>.</summary>
    public static float HoverT(System.DateTime now, System.DateTime since)
    {
        float t = (float)(now - since).TotalMilliseconds / HoverMs;
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }
}
