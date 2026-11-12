using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The extender's menu in place of the server's NUI Item menu (approved examples DD1, DD4 and DD5). Oracle: the capability name
/// and version, and the <c>menu.open</c> and <c>menu.row.&lt;n&gt;</c> lines a server sends, written out by hand here from the
/// settled wire form, never read back from the code under test.
/// </summary>
internal static class NuiItemMenuApprovedExamples
{
    /// <summary>The capability a client that draws the menu advertises, and the version the contract gives it.</summary>
    private const string MenuCapability = "cx_cap_menu";
    private const int MenuVersion = 1;

    /// <summary>The policy prefix the server's menu arrives under.</summary>
    private const string MenuPrefix = "menu.";

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemMenuApprovedExamples.SelfCheck");
        Capability(check);
        Rows(check);
        Closes(check);
        log.Info(check.Ok ? "NuiItemMenuApprovedExamples.SelfCheck: passed" : "NuiItemMenuApprovedExamples.SelfCheck: FAILED");
    }

    // Given a healing potion in my pack in the hub's Inventory tab
    // When I right-click it
    // Then a menu opens at the pointer with the same actions, in the same order, as the hub's own Item menu offers for that potion
    //
    // The server opens the NUI Item menu for anyone else, and sends the rows only to a client that said it draws them,
    // so the login set carries the capability at version 1. Oracle: the name and the version typed above.
    private static void Capability(Checker check)
    {
        var sent = Capabilities.Collect("0.7.0");
        bool menu = false;
        foreach (var (name, value) in sent)
            if (name == MenuCapability && value == MenuVersion) menu = true;
        check.Expect(menu, $"DD1 the login set advertises {MenuCapability}={MenuVersion} ({sent.Count} propert(ies) sent, none of them that)");
    }

    // Given no bandolier worn, and a potion in my pack
    // When I right-click the potion
    // Then the menu offers no Add to bandolier row, as the hub's Item menu offers none
    //
    // The rows are the server's and only the server's: the menu feature parses the menu. lines itself, the way the held
    // picks parse theirs, and leaves none of them lying in the session's loose properties for a window to read as text.
    // Oracle: the three lines below, typed by hand, and the count they must leave, 0.
    private static void Rows(Checker check)
    {
        ServerPolicy.Clear();
        int applied = ServerPolicy.Apply("menu.open=120;64\nmenu.row.1=use;Use;iit_potion;1\nmenu.row.2=drop;Drop;iit_drop;1");
        check.Expect(applied == 3, $"DD5 the open line and both rows are applied ({applied} of 3)");
        check.Expect(ServerPolicy.Count == 0, $"DD5 the {MenuPrefix} lines are parsed by the menu feature, not held as loose properties ({ServerPolicy.Count} held)");
        check.Expect(!ServerPolicy.TryGet("menu.row.1", out _), "DD5 a row is no component property");
        ServerPolicy.Clear();
    }

    // Given the extender's menu open on a potion in my pack
    // When I press Escape, or click anywhere off the menu
    // Then the menu closes and the potion is still in the same cell
    //
    // A close releases the whole family at once, the way held.pick.*= does, and the feature that owns the prefix says
    // in /policy what it is holding, so a closed menu is readable as "no rows". Oracle: the release line and the word
    // the report must carry, both typed by hand.
    private static void Closes(Checker check)
    {
        ServerPolicy.Clear();
        ServerPolicy.Apply("menu.open=120;64\nmenu.row.1=use;Use;iit_potion;1");
        ServerPolicy.Apply("menu.*=");
        string report = ServerPolicy.Report();
        check.Expect(report.Contains("menu", System.StringComparison.OrdinalIgnoreCase),
            "DD4 after menu.*= the menu feature still reports its own line to /policy, so a closed menu reads as no rows");
        check.Expect(ServerPolicy.Count == 0, $"DD4 menu.*= leaves no menu key behind ({ServerPolicy.Count} held)");
        ServerPolicy.Clear();
    }
}
