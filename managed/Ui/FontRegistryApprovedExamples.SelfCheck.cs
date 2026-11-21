using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Panels;

namespace CX.Ui;

/// <summary>The font chart's approved examples, over real fonts cut to a few glyphs (<c>Ui/Fixtures/Fonts</c>) written to
/// a folder of the check's own. Oracle: the fixtures' own name and OS/2 tables as fontTools reads them, fontTools'
/// instancer for the variable outlines, and the literals written here.</summary>
internal static partial class FontRegistryApprovedExamples
{
    /// <summary>The <c>font.&lt;name&gt;=</c> lines the check declares: <c>&lt;Mine&gt;static</c>, <c>variable</c>, <c>collection</c>.</summary>
    private const string Mine = "font.cx-fontcheck-mine-";
    private const string DejaVu = "DejaVu Sans", DejaVuCondensed = "DejaVu Sans Condensed", Noto = "Noto Sans";

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FontRegistryApprovedExamples.SelfCheck");
        UiCategory.Declare();
        string dir = Path.Combine(Path.GetTempPath(), $"cx-fonts-{Environment.ProcessId}");
        FontCatalog? was = null;
        string[] names = FontRegistry.Names;
        try
        {
            Lay(dir);
            Settings.Read($"{Mine}static={Path.Combine(dir, "DejaVuSans-ExtraLight.ttf")}\n{Mine}variable={Path.Combine(dir, "NotoSans-VF.ttf")}\n"
                        + $"{Mine}collection={Path.Combine(dir, "pair", "cambria.ttc")}\n");
            was = FontRegistry.UseCatalog(FontCatalog.Scan(null, new[] { dir }));
            Families(check, dir);
            Faces(check, dir);
            Styles(check, dir);
            Variable(check, dir);
            Scan(check, dir);
            FontRegistryRegressions.SelfCheck(check, dir);
        }
        finally
        {
            foreach (string k in new[] { "static", "variable", "collection" }) Settings.Forget(Mine + k);
            if (was != null) FontRegistry.UseCatalog(was);
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Info($"FontRegistryApprovedExamples: {dir} left behind ({ex.GetType().Name})"); }
        }
        check.Expect(FontRegistry.Names.SequenceEqual(names), "the client's own font list is back as it was");
        log.Info(check.Ok ? "FontRegistryApprovedExamples.SelfCheck: passed" : "FontRegistryApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>The fixtures as a machine holds them: the families in <paramref name="dir"/>, and three folders that
    /// would muddle them -- a second Noto Sans, a collection of the DejaVu files, and DejaVu under Windows-style names.</summary>
    private static void Lay(string dir)
    {
        foreach (string f in new[] { "DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-ExtraLight.ttf", "DejaVuSans-Oblique.ttf",
                                     "DejaVuSans-BoldOblique.ttf", "DejaVuSansCondensed.ttf", "DejaVuSansCondensed-Bold.ttf",
                                     "NotoSans-VF.ttf", "NotoSans-Italic-VF.ttf", "Cantarell-VF.otf", "Cantarell-CFF2.ttc" })
            Put(f, Path.Combine(dir, f));
        Put("NotoSans-300-700-VF.ttf", Path.Combine(dir, "narrow", "NotoSans-300-700-VF.ttf"));
        Put("DejaVuPair.ttc", Path.Combine(dir, "pair", "cambria.ttc"));
        Put("DejaVuSans.ttf", Path.Combine(dir, "win", "dejavu.ttf"));
        Put("DejaVuSans-Bold.ttf", Path.Combine(dir, "win", "dejavubd.ttf"));
    }

    private static void Put(string fixture, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.WriteAllBytes(to, Fixture(fixture));
    }

    internal static byte[] Fixture(string name)
    {
        using Stream s = typeof(FontRegistryApprovedExamples).Assembly.GetManifestResourceStream("fonts/" + name)
                         ?? throw new FileNotFoundException("fixture missing from the image", name);
        var b = new byte[s.Length];
        s.ReadExactly(b);
        return b;
    }

    /// <summary>One example: its checks, with a throw counted as a failure of that example rather than of the suite.</summary>
    internal static void Example(Checker check, string id, Func<bool> body)
    {
        bool ok;
        try { ok = body(); }
        catch (Exception ex) { check.Expect(false, $"{id}: threw {ex.GetType().Name}: {ex.Message}"); return; }
        check.Expect(ok, id);
    }

    /// <summary>The file name and weight <paramref name="name"/> draws from, or "nothing".</summary>
    internal static string Drawn(string name)
        => FontRegistry.Where(name) is FontRegistry.Place p ? $"{Path.GetFileName(p.Path)}#{p.Index}@{p.Weight}{(p.Italic ? "i" : "")}" : "nothing";

    /// <summary>A style of an unregistered group, so nothing it declares reaches the panel or cx.cfg.</summary>
    internal static TextStyle Window(string key)
    {
        var g = new KnobGroup(key, "Test", "Font check", null, null, true, Effect.Live, null, null, "", register: false);
        return new TextStyle(g, "the window", sized: false);
    }

    /// <summary>Runs <paramref name="body"/> with the UI rows at the given values, putting them back after.</summary>
    internal static bool WithUi(string typeface, int weight, int style, Func<bool> body)
    {
        string t = UiKnobs.Typeface.V;
        int w = UiKnobs.FontWeight!.V, s = UiKnobs.FontStyle!.V;
        UiKnobs.Typeface.V = typeface; UiKnobs.FontWeight.V = weight; UiKnobs.FontStyle.V = style;
        try { return body(); }
        finally { UiKnobs.Typeface.V = t; UiKnobs.FontWeight.V = w; UiKnobs.FontStyle.V = s; }
    }

    /// <summary>A chat window drawing in <paramref name="typeface"/> at the given rows (0 follows the UI).</summary>
    internal static ChatWindow Chat(string typeface, int weight = 0, int style = 0)
        => new(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) }) { Typeface = typeface, Weight = weight, Style = style };

    /// <summary>The rows a chat window's gear shows, opened as a player opens it.</summary>
    private static List<KnobPanel.Row> Gear(ChatWindow win)
    {
        var s = new SettingsWindow();
        s.OpenAt(win, 1280, 800);
        return s.Rows;
    }

    private static readonly int[] Ladder = { 100, 200, 300, 400, 500, 600, 700, 800, 900 };

    /// <summary>Families, the Default weight rows, the nearest weight, and text that asks for its own.</summary>
    private static void Families(Checker check, string dir)
    {
        // E4 Given any Typeface row in CX settings When the player opens the settings Then a Default weight dropdown sits
        // beside it, offering 100, 200, 300, 400, 500, 600, 700, 800 and 900; the UI's own row is set to 400, and an
        // element's row offers as UI first and is set to it
        Example(check, "E4 a Default weight row beside every Typeface row", () =>
        {
            TextStyle w = Window("cx-fontcheck-e4");
            KnobPanel.Row? gear = Gear(Chat("")).Find(r => r.Text == "Default weight");
            return gear?.Choices is string[] c && c[0] == "as UI" && c.Skip(1).SequenceEqual(Ladder.Select(x => x.ToString())) && gear.Selected == 0
                && UiKnobs.FontWeight!.Values.SequenceEqual(Ladder) && UiKnobs.FontWeight.Default == 400 && UiKnobs.FontWeight.Label == "Default weight"
                && w.Weight!.Key == "cx-fontcheck-e4.weight" && w.Weight.Label == "Default weight" && w.Weight.Choices[0] == "as UI"
                && w.Weight.Values.Skip(1).SequenceEqual(Ladder) && w.Weight.V == 0;
        });

        // E5 Given a family installed as several weights, as one variable file (Noto Sans) or one file per weight (DejaVu
        // Sans, DejaVu Sans Bold, DejaVu Sans ExtraLight) When the player opens a Typeface list Then the family is offered
        // once, with no weight in its name
        Example(check, "E5 a family is offered once, with no weight in its name", () =>
        {
            string[] n = FontRegistry.Names;
            return n.Count(x => x == DejaVu) == 1 && n.Count(x => x == Noto) == 1
                && !n.Any(x => x.Contains("Bold") || x.Contains("ExtraLight") || x.Contains("Light") || x.Contains("Regular"));
        });

        // E9 Given a chat window whose typeface is DejaVu Sans When the player sets its Weight to 700 Then the window draws
        // with DejaVu Sans Bold
        Example(check, "E9 weight 700 draws DejaVu Sans Bold", () =>
        {
            ChatWindow w = Chat(DejaVu, 700);
            return Drawn(ChatService.FaceName(w)) == "DejaVuSans-Bold.ttf#0@700";
        });

        // E10 Given a chat window whose typeface is DejaVu Sans, which ships weights 200, 400 and 700 When the player sets
        // its Weight to 300, 500, 600, 100 or 900 Then it draws at 200, 400, 700, 200 and 700 in turn: the weight a web
        // browser picks for the same font
        Example(check, "E10 a missing weight draws the browser's nearest", () =>
        {
            ChatWindow w = Chat(DejaVu);
            var got = new List<string>();
            foreach (int want in new[] { 300, 500, 600, 100, 900 }) { w.Weight = want; got.Add(Drawn(ChatService.FaceName(w))); }
            return got.SequenceEqual(new[] { "DejaVuSans-ExtraLight.ttf#0@200", "DejaVuSans.ttf#0@400", "DejaVuSans-Bold.ttf#0@700",
                                             "DejaVuSans-ExtraLight.ttf#0@200", "DejaVuSans-Bold.ttf#0@700" })
                && FontCatalog.Nearest(new[] { 200, 400, 700 }, 450) == 400 && FontCatalog.Nearest(new[] { 300, 500 }, 400) == 500;
        });

        // E13 Given the UI typeface is Noto Sans and the UI weight is 700 When the client restarts Then every CX window
        // that follows the UI typeface draws in Noto Sans Bold, and the settings still show Noto Sans and 700
        Example(check, "E13 the UI's family and weight survive a restart", () =>
        {
            Settings.Read("cx-fontcheck-e13.typeface=Noto Sans\ncx-fontcheck-e13.weight=700\n");
            try
            {
                TextStyle w = Window("cx-fontcheck-e13");
                return w.Typeface.V == Noto && w.Weight!.V == 700 && Drawn(w.FaceName) == "NotoSans-VF.ttf#0@700"
                    && WithUi(Noto, 700, FontRegistry.StyleNormal, () => Drawn(Window("cx-fontcheck-e13b").FaceName) == "NotoSans-VF.ttf#0@700");
            }
            finally { Settings.Forget("cx-fontcheck-e13.typeface"); Settings.Forget("cx-fontcheck-e13.weight"); }
        });

        // E14 Given a chat window drawing in DejaVu Sans at weight 400 When the player sets its Weight to 700 Then the
        // window redraws in DejaVu Sans Bold without a restart
        Example(check, "E14 a weight change redraws live", () =>
        {
            ChatWindow w = Chat(DejaVu, 400);
            var gear = new SettingsWindow();
            gear.OpenAt(w, 1280, 800);
            KnobPanel.Row? row = gear.Rows.Find(r => r.Text == "Default weight");
            string before = Drawn(ChatService.FaceName(w));
            if (row == null) return false;
            row.Selected = Array.IndexOf(row.Choices!, "700");
            gear.Panel.RowPressed!(row);
            return before == "DejaVuSans.ttf#0@400" && w.Weight == 700 && gear.FontChanged && Drawn(ChatService.FaceName(w)) == "DejaVuSans-Bold.ttf#0@700";
        });

        // E15 Given a cx.cfg written by an older CX whose ui.typeface is DejaVu Sans Bold When the client starts Then the UI
        // draws in DejaVu Sans Bold, and the settings show Typeface DejaVu Sans and Weight 700
        Example(check, "E15 an older full name migrates to family and weight", () =>
        {
            Settings.Read("cx-fontcheck-e15.typeface=DejaVu Sans Bold\n");
            try
            {
                TextStyle w = Window("cx-fontcheck-e15");
                return FontRegistry.Migrate("DejaVu Sans Bold") == (DejaVu, 700, FontRegistry.StyleNormal)
                    && w.Typeface.V == DejaVu && w.Weight!.V == 700 && Drawn(w.FaceName) == "DejaVuSans-Bold.ttf#0@700";
            }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-e15." + k); }
        });

        // E16 Given a window whose typeface the player set before this change, such as Noto Sans Regular or DejaVu Sans
        // When the player starts the client after the update and touches no Default weight or Default style row Then the
        // window draws exactly as it did before the update
        Example(check, "E16 an untouched window draws the same file, unchanged", () =>
        {
            Settings.Read("cx-fontcheck-e16a.typeface=Noto Sans Regular\ncx-fontcheck-e16b.typeface=DejaVu Sans\n");
            try
            {
                TextStyle a = Window("cx-fontcheck-e16a"), b = Window("cx-fontcheck-e16b");
                return Drawn(a.FaceName) == "NotoSans-VF.ttf#0@400" && Drawn(b.FaceName) == "DejaVuSans.ttf#0@400"
                    && FontRegistry.Bytes(a.FaceName) is byte[] bytes && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "NotoSans-VF.ttf")));
            }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) { Settings.Forget("cx-fontcheck-e16a." + k); Settings.Forget("cx-fontcheck-e16b." + k); } }
        });

        // E17 Given Windows, or the game under Proton When the player picks Arial and sets the Weight to 700 Then the window
        // draws with Arial Bold, and the Typeface list offers Arial once
        Example(check, "E17 Windows-style file names group by the name table", () =>
        {
            FontCatalog win = FontCatalog.Scan(null, new[] { Path.Combine(dir, "win") });
            return win.Families.SequenceEqual(new[] { DejaVu }) && win.Pick(DejaVu, 700, false) is ({ } f, 700) && Path.GetFileName(f.Path) == "dejavubd.ttf";
        });

        // E18 Given the UI weight is 700 and a chat window whose Weight row is as UI When the window draws its text Then it
        // draws at weight 700, and once the player sets the window's row to 400 it draws at 400 while the UI stays at 700
        Example(check, "E18 an element's as UI row follows the UI weight", () => WithUi(DejaVu, 700, FontRegistry.StyleNormal, () =>
        {
            ChatWindow w = Chat("");
            string following = Drawn(ChatService.FaceName(w));
            w.Weight = 400;
            return following == "DejaVuSans-Bold.ttf#0@700" && Drawn(ChatService.FaceName(w)) == "DejaVuSans.ttf#0@400" && UiKnobs.FontWeight!.V == 700;
        }));

        // E25 Given a window whose Default weight is 100, drawing a line of plain text and a bold heading When the window
        // draws Then the plain text is at weight 100 and the heading is at 700, as heavy as it is at Default weight 400
        Example(check, "E25 a bold heading keeps 700 under a light default", () =>
            Drawn(FontRegistry.RunFace(Noto, 100, FontRegistry.StyleNormal, 0, 0, false, out _)) == "NotoSans-VF.ttf#0@100"
            && Drawn(FontRegistry.RunFace(Noto, 100, FontRegistry.StyleNormal, 700, 0, false, out _)) == "NotoSans-VF.ttf#0@700"
            && FontRegistry.RunFace(Noto, 100, FontRegistry.StyleNormal, 700, 0, false, out _) == FontRegistry.RunFace(Noto, 400, FontRegistry.StyleNormal, 700, 0, false, out _));

        // E26 Given a window whose Default weight is 900, drawing a line of plain text and a bold heading When the window
        // draws Then the plain text is at weight 900 and the heading stays at 700: the weight text asks for is the weight it gets
        Example(check, "E26 a bold heading stays 700 under a heavy default", () =>
            Drawn(FontRegistry.RunFace(Noto, 900, FontRegistry.StyleNormal, 0, 0, false, out _)) == "NotoSans-VF.ttf#0@900"
            && Drawn(FontRegistry.RunFace(Noto, 900, FontRegistry.StyleNormal, 700, 0, false, out _)) == "NotoSans-VF.ttf#0@700");

        // E27 Given a window whose typeface is DejaVu Sans, which ships weights 200, 400 and 700, drawing a heading that
        // asks for weight 600 When the window draws Then the heading is at 700, the weight E10's rule picks for 600
        Example(check, "E27 an asked weight takes the nearest rule", () =>
            Drawn(FontRegistry.RunFace(DejaVu, 400, FontRegistry.StyleNormal, 600, 0, false, out _)) == "DejaVuSans-Bold.ttf#0@700");

        // E29 Given a card or NUI window drawing with a font a hak or NWSync supplies When the player sets any Default
        // weight or Default style row, or a card asks for <b> or <w=300> Then that font draws at its own weight and in its
        // own style, exactly as before
        Example(check, "E29 a hak's font is never re-weighted", () =>
        {
            byte[] hak = Fixture("DejaVuSans-ExtraLight.ttf");
            FontRegistry.Declare("cx-fontcheck-hak", hak, "a check's stand-in for a hak font");
            try
            {
                return ReferenceEquals(FontRegistry.Bytes(FontRegistry.FaceKey("cx-fontcheck-hak", 700, FontRegistry.StyleItalic)), hak)
                    && ReferenceEquals(FontRegistry.Bytes(FontRegistry.RunFace("cx-fontcheck-hak", 400, FontRegistry.StyleNormal, 300, 0, false, out _)), hak);
            }
            finally { FontRegistry.Undeclare("cx-fontcheck-hak"); }
        });

        // E30 Given the UI typeface is DejaVu Sans at Default weight 400, and a card whose text holds <b>bold</b>,
        // <w=600>semi</w> and a text block with "weight": 300 When the card draws Then those runs draw at 700, 700 and 200
        // (E10's rule over the weights DejaVu Sans ships), and the rest at 400
        Example(check, "E30 card markup asks for a weight", () =>
        {
            bool inline = PanelMarkup.TryParse("plain <b>bold</b> <w=600>semi</w>", out Run[] runs, out _);
            bool blocks = PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"light\",\"weight\":300}]}]", out PanelDecl[] cards, out _);
            if (!inline || !blocks || runs.Length != 4 || cards.Length != 1) return false;
            Run block = cards[0].Blocks[0].Runs[0];
            return runs[1].Weight == 700 && runs[3].Weight == 600 && block.Weight == 300 && WithUi(DejaVu, 400, FontRegistry.StyleNormal, () =>
                Drawn(PanelFonts.RunFace(runs[1], out _)) == "DejaVuSans-Bold.ttf#0@700"
                && Drawn(PanelFonts.RunFace(runs[3], out _)) == "DejaVuSans-Bold.ttf#0@700"
                && Drawn(PanelFonts.RunFace(block, out _)) == "DejaVuSans-ExtraLight.ttf#0@200"
                && Drawn(PanelFonts.RunFace(runs[0], out _)) == "DejaVuSans.ttf#0@400");
        });
    }

    /// <summary>Faces no row re-weights: the regular width's own file, a <c>font.&lt;name&gt;=</c> line, the
    /// built-in face and the console's.</summary>
    private static void Faces(Checker check, string dir)
    {
        // E32 Given DejaVu Sans and DejaVu Sans Condensed both installed When the player sets a window in DejaVu Sans to
        // Default weight 700 Then it draws with DejaVu Sans Bold, never DejaVu Sans Condensed Bold
        Example(check, "E32 the regular width keeps its own bold", () =>
            Drawn(ChatService.FaceName(Chat(DejaVu, 700))) == "DejaVuSans-Bold.ttf#0@700"
            && Drawn(ChatService.FaceName(Chat(DejaVuCondensed, 700))) == "DejaVuSansCondensed-Bold.ttf#0@700");

        // E33 Given cx.cfg holds font.mine naming one file, whether a single weight, a variable file or a collection, and
        // a window whose typeface is mine When the player moves any Default weight or Default style row, or a card asks for
        // a weight Then the window draws the same letters as before: the file's own face, its default weight if variable,
        // its first font if a collection, and the list still offers mine
        Example(check, "E33 a font a cx.cfg line names is one face", () =>
        {
            var off = new List<string>();
            foreach ((string k, string file, string at) in new[] { ("static", "DejaVuSans-ExtraLight.ttf", "DejaVuSans-ExtraLight.ttf#0@200"),
                                                                   ("variable", "NotoSans-VF.ttf", "NotoSans-VF.ttf#0@400"),
                                                                   ("collection", Path.Combine("pair", "cambria.ttc"), "cambria.ttc#0@400") })
            {
                string name = Mine["font.".Length..] + k;
                string moved = ChatService.FaceName(Chat(name, 900, FontRegistry.StyleItalic));
                string asked = FontRegistry.RunFace(name, 400, FontRegistry.StyleNormal, 700, 0, false, out _);
                bool same = FontRegistry.Bytes(moved) is byte[] b && b.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, file)))
                            && FontRegistry.Bytes(asked) is byte[] c && c.AsSpan().SequenceEqual(b);
                if (!FontRegistry.Names.Contains(name) || Drawn(moved) != at || Drawn(asked) != at || !same) off.Add(k);
            }
            return off.Count == 0;
        });

        // E34 Given the UI typeface left at default, the built-in DejaVu Sans When the player sets the UI Default weight to
        // 700 Then the UI draws the built-in face at its own weight, with no thickened letters
        Example(check, "E34 the built-in face is never thickened", () =>
            ReferenceEquals(FontRegistry.Bytes(FontRegistry.FaceKey(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal)), FontRegistry.Bytes(FontRegistry.DefaultName))
            && WithUi(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal,
                      () => ReferenceEquals(FontRegistry.Bytes(ChatService.FaceName(Chat(""))), FontRegistry.Bytes(FontRegistry.DefaultName))));

        // E35 Given the UI Default weight is 900 and its Default style Italic When the console draws Then it draws with the
        // monospace face exactly as before, and the console offers no weight or style row
        Example(check, "E35 the console keeps its monospace face", () =>
        {
            byte[]? before = FontRegistry.Bytes(FontRegistry.MonospaceName);
            return WithUi(DejaVu, 900, FontRegistry.StyleItalic, () => ReferenceEquals(FontRegistry.Bytes(FontRegistry.MonospaceName), before))
                && !UiKnobs.G.Knobs.Any(k => k.Key.Contains("console", StringComparison.Ordinal) && (k.Key.Contains("weight") || k.Key.Contains("style")))
                && UiKnobs.G.Knobs.Any(k => k.Key.Contains("consolescale", StringComparison.Ordinal));
        });
    }
}
