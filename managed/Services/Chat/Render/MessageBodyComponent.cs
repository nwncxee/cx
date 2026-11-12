using System.Collections.Generic;
using System.Text;
using CX.Services.Chat.Core;

namespace CX.Services.Chat.Render;

/// <summary>The message text itself, each colour run in its parsed RGB.</summary>
// ponytail: SenderName is the next text component -- the line holds it, nothing draws it yet.
// Portrait is not a text component: it's a texture, emitted as a PortraitQuad in the gutter.
public sealed class MessageBodyComponent : ILineComponent
{
    public uint Bit => ChatToggle.MessageBody;
    public void Contribute(ChatWindow win, ChatLine line, List<Rune> runes, List<uint> cols)
    {
        foreach (ColorRun run in line.ColorRuns)
        {
            uint rgb = ((uint)(run.R * 255f) << 16) | ((uint)(run.G * 255f) << 8) | (uint)(run.B * 255f);
            foreach (Rune r in run.Text.EnumerateRunes()) { runes.Add(r); cols.Add(rgb); }
        }
    }
}
