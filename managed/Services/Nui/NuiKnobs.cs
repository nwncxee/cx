using CX.Framework;

namespace CX.Services.Nui;

/// <summary>The <c>nui</c> group, declared through <see cref="CX.Ui.UiCategory.Declare"/>: speed and usability fixes
/// for server-built NUI windows -- the off-screen skip (<see cref="Cull"/>), the <c>@item:</c> images
/// (<see cref="ItemIcons"/>), the scrollbars (<see cref="Smooth"/>, <see cref="Thumb"/>, <see cref="AutoHide"/>,
/// <see cref="HoverBars"/>), the layout solve (<see cref="Solve"/>, <see cref="SolveCheck"/>, <see cref="Explain"/>).</summary>
internal static class NuiKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Cull, ItemIcons, Smooth, AutoHide, HoverBars, Solve, SolveCheck, Explain, HitDebug;
    internal static readonly Knob<float> Thumb;
    internal static readonly Knob<string> PolicyProbe;

    static NuiKnobs()
    {
        G = KnobGroup.Declare("nui", "Interface", "Windows & text", master: "Enable CX NUI fixes",
            masterHelp: "Speed and usability fixes for the windows servers build with NUI. Off, they behave exactly as in the stock client.",
            order: 204, title: "NUI windows");
        Enabled = G.Master!;
        Cull = G.Toggle("cull", "Skip widgets scrolled out of view", true, Tier.ChatOnly,
            help: "Rows of a NUI list that are outside its visible area are not processed each frame, so long lists stay smooth. Nothing on screen changes.");
        Solve = G.Toggle("solve", "Faster layout solving", true, Tier.ChatOnly,
            help: "Windows with long lists open and refresh much faster. Every element lands exactly where the stock client puts it.");
        SolveCheck = G.Toggle("solvecheck", "Check the fast layout solve", false, Tier.ChatOnly,
            help: "Solves the stock way as well and compares the fast path's bookkeeping at every step. A mismatch turns the fast solve off and says so. Slow.");
        Explain = G.Toggle("explain", "Explain broken layouts", true, Tier.Advanced,
            help: "When a server's window cannot lay out, it lists the rules that contradict each other and the elements they belong to, instead of only \"The constraint can not be satisfied.\"");
        HitDebug = G.Toggle("hitdebug", "Log the NUI element under the pointer", false, Tier.Advanced,
            help: "Writes a line to cx.log each time the pointer moves onto another element of a NUI window: its id, list row and rectangle. For people building windows.");
        Smooth = G.Toggle("smooth", "Smooth scrolling", true,
            help: "The mouse wheel glides a NUI list to its new position instead of jumping there.");
        AutoHide = G.Toggle("autohide", "Hide scrollbars with nothing to scroll", true,
            help: "A NUI list whose content fits shows no scrollbar, instead of a full-length one that does nothing.");
        HoverBars = G.Toggle("hoverbars", "Show scrollbars only under the mouse", false,
            help: "A NUI list's scrollbar fades out while the pointer is elsewhere and back in when it is over the list. Text boxes keep theirs.");
        Thumb = G.Slider("thumb", "Shortest scrollbar handle", 0f, 4f, 0.5f, 2f, "\u00d7 its width", Tier.Advanced,
            help: "A long list's scrollbar handle never gets shorter than this, so it cannot shrink out of sight. 0 = stock.");
        PolicyProbe = G.Text("policyprobe", "policyprobe", "", Tier.ChatOnly,
            help: "Debug: window render properties as key=value;key=value (a window's id, then the property), read under whatever a server sends. For trying the keys without a server.");
        ItemIcons = G.Toggle("itemicons", "Item icons in NUI windows", true,
            help: "A server's NUI window can show an item's own icon in its real colours, as the inventory does. Off, those images show the stock error sprite.");
    }
}
