using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary><b>A GUI scene, drawn out of the database.</b> A GUI panel's controls are model objects in the
/// panel's own scene (<c>CGuiPanel::CreateSceneView</c> gives each one a scene and a camera) and its text is
/// <see cref="ObjKind.Text"/> objects whose glyph quads are built once per change. Depth goes through
/// <c>GuiCapture.DepthOf</c>, the ONE implementation of the engine's scene-name table and its clear latch.</summary>
internal static unsafe class DbGui
{
    private static readonly int[] _sceneFirst = Minus1(new int[SceneDb.MaxScenes]);
    private static readonly int[] _sceneLast = Minus1(new int[SceneDb.MaxScenes]);
    private static readonly int[] _objNext = Minus1(new int[SceneDb.MaxObjects]);
    /// <summary>Which scene this object is listed under, plus one (0 = not listed). The per-SCENE membership
    /// test R1-13 needs: "already listed" has to mean "listed HERE".</summary>
    private static readonly int[] _listedIn = new int[SceneDb.MaxObjects];
    private static int _listed;
    private static bool _latch;
    private static long _textRuns, _refusedNoLayer, _controls;
    private static ExtenderLog? _log;

    /// <summary>Inject the log, so a GUI that does not draw says why. Called from <c>GuiPass.Install</c>.</summary>
    internal static void Install(ExtenderLog log)
    {
        _log = log;
    }

    private static int[] Minus1(int[] a) { Array.Fill(a, -1); return a; }

    /// <summary>The last <see cref="Draw"/>'s outcome for the view-order line: d drew, C drew after a depth clear,
    /// x no scene, e empty, p no GUI pass, b bubble refused, l no layer.</summary>
    internal static char LastWhy;
    internal static int LastIssued;
    /// <summary>Is this a GUI panel's scene? A scene with no tile grid, which is the engine's own test
    /// (<c>Scene+0x188</c>, written by <c>InitBSP</c> alone and never by <c>CGuiPanel::CreateSceneView</c>).</summary>
    private static bool IsGuiScene(DbId scene)
        => (uint)scene.Index < SceneDb.MaxScenes && !SceneDb.SceneIsWorld[scene.Index];

    /// <summary>A new frame's GUI run: the clear latch is per RUN of GUI views, not per view, exactly as the
    /// engine's own <c>DAT_019aa3a4</c> is.</summary>
    internal static void NewFrame() { _latch = false; }

    /// <summary>Object <paramref name="o"/> joined scene <paramref name="scene"/>: append it, so the draw order
    /// is the order the game created the controls in.</summary>
    internal static void Joined(int o, DbId scene)
    {
        int s = scene.Index;
        if ((uint)s >= SceneDb.MaxScenes || (uint)o >= SceneDb.MaxObjects) return;
        // R1-12: GUI scenes ONLY. A world scene's objects are drawn from the ownership table through the area
        // index, never from this list, so listing them bought nothing and made an area teardown an O(n^2)
        // unlink over thousands of entries.
        if (!IsGuiScene(scene)) return;
        // R1-13: and the test is per SCENE, not per object -- an object listed in panel A would otherwise be
        // silently refused when it joined panel B, and keep painting in A.
        if (_listedIn[o] == s + 1) return;
        _listedIn[o] = s + 1;
        _objNext[o] = -1;
        if (_sceneLast[s] < 0) _sceneFirst[s] = o; else _objNext[_sceneLast[s]] = o;
        _sceneLast[s] = o;
        _listed++;
    }

    /// <summary>Object <paramref name="o"/> left scene <paramref name="scene"/> (destroyed, or moved to
    /// another panel).
    /// <para>ponytail: an O(n) unlink over that ONE panel's list -- tens of compares, and the singly-linked
    /// list with a tail is what keeps APPEND order. The upgrade, for thousands, is a doubly-linked list.</para></summary>
    internal static void Left(int o, DbId scene)
    {
        int s = scene.Index;
        if ((uint)s >= SceneDb.MaxScenes || (uint)o >= SceneDb.MaxObjects) return;
        if (_listedIn[o] != s + 1) return;
        _listedIn[o] = 0;
        int prev = -1;
        for (int i = _sceneFirst[s]; i >= 0; i = _objNext[i])
        {
            if (i == o)
            {
                if (prev < 0) _sceneFirst[s] = _objNext[i]; else _objNext[prev] = _objNext[i];
                if (_sceneLast[s] == o) _sceneLast[s] = prev;
                _objNext[o] = -1;
                _listed--;
                return;
            }
            prev = i;
        }
    }

    /// <summary>Draw one GUI view: open its layer with the engine's depth convention, issue every object of the
    /// scene in order, close it. <paramref name="v"/> is the snapshot's record for this view -- camera matrices,
    /// viewport rect and view angle, the other half of the GUI verdict (the engine treats ANY zero-angle camera
    /// as GUI). Returns draws issued; zero with a non-empty scene is a refusal, and the counters say which.</summary>
    internal static int Draw(DbId scene, in RenderSnapshot.ViewRec v, uint nowMs)
    {
        int s = scene.Index;
        LastWhy = 'x'; LastIssued = 0;
        if (!SceneDb.Scenes.Valid(scene) || SceneDb.SceneIsWorld[s]) return 0;
        // THE SCENE'S OWN MEMBER LIST, in its own order. Scene::RenderOnlyGobs @0x4682e0 walks Scene+0x130
        // from 0 to Scene+0x138 and calls Gob::Render on each, forwards, with no sort -- so the order a GUI
        // panel paints in is the order Gob::SetScene appended, which is exactly what DbScene.Join keeps.
        // This used to walk a list of DbGui's own, built from the feed's DIRTY order instead, and the two are
        // not the same: a panel's labels are created before its buttons, so every label painted FIRST and its
        // button's plate then painted over it. That is why the centred main-menu labels were invisible.
        DbList? members = SceneDb.SceneObjects[s];
        int head = _sceneFirst[s];
        LastWhy = 'e';
        if ((members == null || members.Count == 0) && head < 0) return 0;
        // The draw path is GuiPass's. Without it there is no GUI at all -- there is no engine traversal
        // underneath to fall back on, so this is said rather than left as an empty screen.
        LastWhy = 'p';
        if (!GuiPass.Capturing) { Refuse(); return 0; }
        Span<byte> name = stackalloc byte[GuiCapture.MaxSceneName];
        int n = SceneDb.SceneNameBytes(scene, name);
        name[n] = 0;
        float near, far;
        bool clear, lit;
        // CX's own nameplate feature holds the engine at text-bubble mode 0 so that "nothing of vanilla's
        // ever draws" (NameplateService) and draws its own bubbles on the 2D UI. The engine honours that
        // text-bubble mode by never rendering a bubble panel's scene at all -- a whole ladder run captures
        // eleven GUI scenes and not one of them is a bubble. Here the draw is CX's, so CX has to honour its own
        // suppression here, or the panel paints gui_textbubble over the world: the dark rounded rectangle in
        // .scratch/live/ladder-db2/shots/07-area2.png, which the pixel probe named as
        // sceneGUI_PNL_BUBBLE32_300 / pnl_bubble32_300 / node "Bubble09".
        // ...but the TOOLTIP is CGuiMan's own CGuiTextBubble on the same PNL_BUBBLE models, which the text-bubble
        // mode never touches (only CGuiInGamePopUpBubble reads it), so its scene is exempt by handle.
        // Before the depth latch, so a refused bubble does not consume the run's clear.
        fixed (byte* np = name)
        {
            if (Prefix(np, "sceneGUI_PNL_BUBBLE"u8) && CX.Services.Nameplate.NameplateService.Suppressing
                && scene.Value != TooltipProbe.Scene)
            { _bubblesRefused++; LastWhy = 'b'; return 0; }
            GuiCapture.DepthOf(np, ref _latch, out near, out far, out clear, v.Fovy);
            lit = GuiCapture.IsLitScene(np);
        }
        if (!GuiCapture.BeginDbLayer(v.X, v.Y, v.W, v.H, near, far, clear)) { _refusedNoLayer++; LastWhy = 'l'; return 0; }
        CX.Services.Nui.NuiModelDoll.ModelSlot? slot = CX.Services.Nui.NuiModelDoll.OfScene(SceneDb.SceneFacade[s]);
        uint redirect = slot?.TargetTex ?? 0u;
        if (redirect != 0) GuiCapture.Layers[GuiCapture.LayerN - 1].Target = redirect;
        CX.Services.Nui.ModelLight light = slot?.Light ?? CX.Services.Nui.NuiModelLook.EngineLight;

        Matrix4x4 viewProj = v.View * v.Proj;
        if (redirect != 0) viewProj *= Matrix4x4.CreateScale(1f, -1f, 1f);
        int issued = 0, walked = 0, hidden = 0, texts = 0;
        // The DumpAt'th time each scene draws. Per scene, because a panel that opens mid-session
        // -- the inventory, the character sheet -- is one nobody has seen before and a flat budget is spent on
        // the HUD long before it appears; on a LATER sighting, because on a panel's first frame its textures
        // are still loading and every part reads "untextured" whether or not it ever resolves.
        // Keyed on the scene's HANDLE: a recycled index is a new panel (the server list reuses the options'),
        // and a per-index latch never dumped it.
        if (_sceneSeenId[s] != scene.Value) { _sceneSeenId[s] = scene.Value; _sceneSeen[s] = 0; _sceneDumped[s] = false; }
        // While the load screen is up every layer is dumped once, whatever the budget the HUD already spent.
        bool load = CX.Ui.LoadScreen.PanelUp;
        if (!load && _loadDump.Any) _loadDump = new();
        bool dump = load ? (_loadDump.Any = true) && ++_loadDump.Seen[s] == LoadDumpAt
                         : _dumped < DumpLayers && !_sceneDumped[s] && ++_sceneSeen[s] >= DumpAt;
        _dumpLayer = dump;
        _dumpObjN = 0; _dumpPartN = 0;
        _lKind = _lOff = _lNoRecipe = _lNoGeom = _lNoPalette = _lNoTex = 0;
        if (members != null)
            for (int k = 0; k < members.Count; k++)
            {
                DbId m = members[k];
                if (SceneDb.Objects.Valid(m))
                    issued += DrawOne(m.Index, s, in v, in viewProj, lit, in light, nowMs, dump, ref walked, ref hidden, ref texts);
            }
        else
            for (int o = head; o >= 0; o = _objNext[o])
                if (SceneDb.Objects.LiveAt(o))
                    issued += DrawOne(o, s, in v, in viewProj, lit, in light, nowMs, dump, ref walked, ref hidden, ref texts);
        GuiCapture.EndDbLayer();
        if (redirect != 0 && issued > 0) CX.Services.Nui.NuiModelDoll.MarkRendered(slot!);
        LastWhy = clear ? 'C' : 'd'; LastIssued = issued;
        return Close(s, in v, dump, walked, hidden, texts, issued, near, far, clear, lit);
    }

    /// <summary>One member of a GUI scene, in the order <c>RenderOnlyGobs</c> would have reached it. Returns
    /// the draws it issued.</summary>
    private static int DrawOne(int o, int s, in RenderSnapshot.ViewRec v, in Matrix4x4 viewProj, bool lit,
                               in CX.Services.Nui.ModelLight light, uint nowMs, bool dump, ref int walked, ref int hidden, ref int texts)
    {
        int issued = 0;
        {
            walked++;
            // Gob::Render @0x53d790's own first two gates, which DB mode otherwise drops: the game's
            // enable, and the COLOUR-SHIFT ALPHA at Gob+0x108 -- `0.0 < *(float *)(this + 0x108)`. Setting
            // that to 0 is how the game hides a gob without detaching it, and nothing else in CX reads it.
            if (!SceneDb.ObjVisible[o] || ShellAlpha(o) <= 0f) { hidden++; return 0; }
            if (dump && SceneDb.ObjAttachPart[o] < 0 && !SceneDb.ObjAttachTo[o].IsNull) NoAnchor(o);
            if (dump && _dumpObjN < DumpObjects)
            {
                _dumpObjN++;
                Matrix4x4 w = SceneDb.ObjWorld[o];
                _log?.Info($"DbGui: [obj {_dumpObjN}] scene={s} o={o} kind={SceneDb.ObjKindOf[o]} "
                    + $"model={SceneDb.ObjModel[o]?.Name ?? "-"} parts={SceneDb.ObjPartCount[o]} "
                    + $"attach={SceneDb.ObjAttachTo[o].Index}/{SceneDb.ObjAttachPart[o]} "
                    + $"beh=0x{DbAnim.Behavior(SceneDb.Objects.IdAt(o)):x}/0x{ShellBeh(o):x} "
                    + $"shellpos={Shell(o)} pos=({w.M41:F3},{w.M42:F3},{w.M43:F3}) "
                    + $"scale={SceneDb.ObjScale[o]:F3} tint={SceneDb.ObjTint[o]}{TextBox(o)}");
            }
            // The object's own transform IS the model matrix: a GUI control's place is its record, so nothing
            // here reads a matrix stack (CX-109's documented exception closes with this).
            if (SceneDb.ObjKindOf[o] == ObjKind.Text)
            {
                texts++;
                TextDoc? d = TextObjects.Of(SceneDb.Objects.IdAt(o));
                if (d?.Font == null) return 0;
                bool glyphs = TextQuads.Build(d);
                // StringGob::Render @0x5933a0: a line's attached model (a list's selection highlight) draws at
                // the line's pen under the string's matrix, BEFORE that line's glyphs.
                for (int k = 0; k < d.AttachedCount; k++)
                {
                    var (am, ax, ay) = d.Attached[k];
                    if (!SceneDb.Objects.Valid(am)) continue;
                    Matrix4x4 at = Matrix4x4.CreateTranslation(ax, ay, 0f) * SceneDb.ObjWorld[o];
                    issued += Parts(am.Index, in v, in viewProj, lit, in light, in at, true);
                }
                if (!glyphs) return issued;
                Matrix4x4 mvp = SceneDb.ObjWorld[o] * viewProj;
                // Depth-written as the engine draws text, so a control drawn later behind it stays under it.
                int runs = TextQuads.Issue(d, in mvp, d.Font.Atlas, TextObjects.CaretAlpha(nowMs),
                                           depthTest: true, depthWrite: true);
                _textRuns += runs;
                return issued + runs;
            }
            _controls++;
            issued += Parts(o, in v, in viewProj, lit, in light, Matrix4x4.Identity, false);
        }
        return issued;
    }

    private static long _lPending;

    /// <summary>This part's texture is named and on its way, but not here yet.</summary>
    private static bool Loading(int p, int recipe)
    {
        int ov = SceneDb.PartTexId[p * 4];
        TexId id = ov != 0 ? new TexId((uint)ov) : DrawRecipe.Tex(recipe, 0);
        // a preview counts as loading: the GUI waits for the full texture rather than flash a blurred icon
        return !id.IsNull && TexRegistry.StateOf(id) == TexState.Loading;
    }

    /// <summary>Close the layer's dump line.</summary>
    private static int Close(int s, in RenderSnapshot.ViewRec v, bool dump, int walked, int hidden, int texts,
                             int issued, float near, float far, bool clear, bool lit)
    {
        if (dump && walked > 0)
        {
            _sceneDumped[s] = true;
            _dumped++;
            _log?.Info($"DbGui: [layer {_dumped}] scene={s} \"{SceneDb.SceneName[s]}\" "
                + $"rect={v.X},{v.Y} {v.W}x{v.H} camrect={SceneDb.CamViewX[v.Camera.Index]:F0},"
                + $"{SceneDb.CamViewY[v.Camera.Index]:F0} {SceneDb.CamViewW[v.Camera.Index]:F0}x"
                + $"{SceneDb.CamViewH[v.Camera.Index]:F0} depth={near:F3}..{far:F3} clear={clear} lit={lit} "
                + $"near={v.Near:F1} far={v.Far:F1} campos=({v.CamPos.X:F2},{v.CamPos.Y:F2},{v.CamPos.Z:F2}) "
                + $"objects={walked} ({hidden} hidden, {texts} text) of {SceneDb.SceneObjects[s]?.Count ?? -1} "
                + $"scene members, draws={issued}"
                + $" skipped: {_lKind} non-mesh, {_lOff} disabled/merged, {_lNoRecipe} no-recipe, "
                + $"{_lNoGeom} no-geometry, {_lNoPalette} no-palette, {_lNoTex} untextured");
        }
        return issued;
    }

    /// <summary>Layers and objects dumped once, on the first layers that have any objects at all: the rect, the
    /// depth split, the camera's own rect and where each control actually sits are exactly what a panel that
    /// does not appear is missing, and no counter carries them.</summary>
    private const int DumpLayers = 96, DumpObjects = 56, DumpParts = 24;
    /// <summary>Sightings of a scene before its dump: long enough for its textures to land, short enough that
    /// a panel the player opens and closes is still caught.</summary>
    private const int DumpAt = 60;
    /// <summary>The same for a load screen, which presents a few frames per load step rather than sixty a second.</summary>
    private const int LoadDumpAt = 4;

    /// <summary>Per load screen: how many times each scene index has drawn while it was up.</summary>
    private sealed class LoadDumpState
    {
        public readonly int[] Seen = new int[SceneDb.MaxScenes];
        public bool Any;
    }

    private static LoadDumpState _loadDump = new();
    private static int _dumped, _dumpObjN, _dumpPartN;
    private static bool _dumpLayer;
    private static readonly bool[] _sceneDumped = new bool[SceneDb.MaxScenes];
    private static readonly int[] _sceneSeen = new int[SceneDb.MaxScenes];
    private static readonly uint[] _sceneSeenId = new uint[SceneDb.MaxScenes];
    /// <summary>Per-layer skip tallies, so a panel that draws nothing says WHICH filter ate its parts.</summary>
    private static int _lKind, _lOff, _lNoRecipe, _lNoGeom, _lNoPalette, _lNoTex;

    /// <summary>One GUI control's model geometry: an ordinary object whose parts the feed seated and whose
    /// recipe it interned -- a button's states are ANIMATIONS, so the pose is whatever <c>PartWorld</c> holds
    /// this frame -- each drawable part becoming one <see cref="GuiCapture.DbPart"/> record on the panel's own
    /// ortho camera. NOT the world pass, which clears the HDR target and would overwrite the world per panel.</summary>
    private static int Parts(int o, in RenderSnapshot.ViewRec v, in Matrix4x4 viewProj, bool litScene,
                             in CX.Services.Nui.ModelLight light, in Matrix4x4 over, bool hasOver)
    {
        int first = SceneDb.ObjPartFirst[o], count = SceneDb.ObjPartCount[o];
        if (count <= 0) return 0;
        if (first < 0 || (long)first + count > SceneDb.MaxParts) { _badRange++; return 0; }
        Vector4 tint = SceneDb.ObjTint[o];
        bool loadPanel = SceneDb.ObjModel[o]?.Name.Equals(LoadPanelModel, StringComparison.OrdinalIgnoreCase) == true;
        int issued = 0;
        for (int p = first; p < first + count; p++)
        {
            if (!SceneDb.Parts.LiveAt(p) || !SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) { _lOff++; continue; }
            PartKind kind = SceneDb.PartKindOf[p];
            if (kind != PartKind.Mesh && kind != PartKind.Skin && kind != PartKind.Dangly
                && kind != PartKind.AnimMesh) { _lKind++; continue; }
            int recipe = SceneDb.PartRecipe[p];
            if (recipe < 0) { _noRecipe++; _lNoRecipe++; continue; }
            int range = DrawRecipe.Range(recipe);
            if (range < 0 || !MeshArena.Draw(range, 0, out int baseVertex, out nint off,
                                             out int idx, out _) || idx <= 0)
            { _noGeometry++; _lNoGeom++; continue; }

            RecipeFlag fl = DrawRecipe.Flags(recipe);
            int bones = -1;
            if ((fl & RecipeFlag.Skinned) != 0)
            {
                bones = SceneDb.PartBoneBase[p];
                // A skinmesh's arena vertices are in BIND space: drawn without its palette it collapses onto
                // the origin, which reads as a missing control rather than as a missing pose. A rigged dangly
                // part's are in NODE space, so no palette is its rest pose, which is a drawable thing.
                if (bones < 0 && (fl & RecipeFlag.Dangly) == 0) { _noPalette++; _lNoPalette++; continue; }
            }

            Matrix4x4 world = hasOver ? SceneDb.PartWorld[p] * over : SceneDb.PartWorld[p];
            // A matrix with a NaN or an infinity in it rasterises nothing and takes its reason with it. The
            // main menu's backdrop arrives that way (scale INF, position NaN -- the game divided by a
            // measurement CX answered as zero), so the hole is named rather than left as a black screen.
            if (!float.IsFinite(world.M41 + world.M42 + world.M43 + world.M11 + world.M22 + world.M33))
            { NotFinite(o, p); continue; }
            float alpha = SceneDb.PartAlpha[p] * SceneDb.PartFade[p] * tint.W;
            int cls = DrawRecipe.Cls(recipe);
            bool additive = cls == (int)AlloyClass.Additive;
            // A GUI scene blends EVERY part and never alpha-tests: Scene::Render @0x4762d0 enables blending
            // with SetBlendFunc(0,1) for a scene with no tile BSP, Scene::RenderOnlyGobs @0x4682e0 disables the
            // alpha test, and a part only sets its material's pair (Material::SetBlendingMode @0x4c5c50), which
            // keeps the depth mask on unless the pair is additive. Blended only when the recipe's class said so,
            // an item icon -- a texture swapped onto a model whose own texture is opaque -- painted its
            // transparent surround black, and a PLT icon its palette's first texel.
            bool plt = Plt(p, recipe);
            bool envMapped = EnvMapped(p, recipe);
            uint tex = Texture(p, recipe);
            // Still streaming: skip it this frame rather than paint the white stand-in. A texture
            // that FAILED keeps the stand-in -- that one is loud on purpose (TexRegistry says so).
            if (tex == 0 && Loading(p, recipe)) { _lPending++; continue; }
            var d = new GuiCapture.DbDraw
            {
                // CX-117's PLT, on a GUI control. An item's icon is a PAIR texture and its colour is the
                // master-palette lookup the fragment stage does; handed over raw it draws as the pair data
                // itself -- the solid red cloak the native Examine panel showed (user-shots/22.png).
                PltRows = !plt ? null : SceneDb.PartPltOwn[p] ? SceneDb.PartPltRow : SceneDb.ObjPltRow,
                PltAt = plt && SceneDb.PartPltOwn[p] ? p * SceneDb.PltLayers : o * SceneDb.PltLayers,
                Arena = true, ArenaFmt = (int)MeshArena.PubFormat(range), Prim = EngineTokens.TRIANGLES, Count = idx, Offset = off, BaseVertex = baseVertex,
                Tex = (int)tex, Bones = bones, Content = DrawRecipe.Content(recipe),
                Mvp = world * viewProj,
                Mv = world * v.View,
                Colour = loadPanel && Surround(recipe) ? new Vector4(0f, 0f, 0f, alpha) : new Vector4(tint.X, tint.Y, tint.Z, alpha),
                // The recipe's own blend pair through the ONE translation (Issue.BlendGl, which is the engine's
                // blendTranslationTable @0x12f5040). This read the GL enums directly while DrawRecipe's
                // Aurora indices were wrong; they are right now, so there is no reason for a second mapping.
                Blend = true,
                Src = Issue.BlendGl(DrawRecipe.Src(recipe)),
                Dst = Issue.BlendGl(DrawRecipe.Dst(recipe)),
                DepthTest = true, DepthWrite = DrawRecipe.Dst(recipe) != DrawRecipe.BlendOne,
                Cull = (fl & RecipeFlag.TwoSided) == 0,
                AlphaTest = false,
                Lit = litScene && (fl & RecipeFlag.Lit) != 0,
                SolidTexel = !TexelAlphaIsCoverage(litScene, plt, envMapped),
            };
            if (d.Lit)
            {
                Vector4 amb = DrawRecipe.Ambient(recipe), dif = DrawRecipe.Diffuse(recipe);
                d.Ambient = new Vector4(amb.X * tint.X, amb.Y * tint.Y, amb.Z * tint.Z, 1f);
                d.Diffuse = new Vector4(dif.X * tint.X, dif.Y * tint.Y, dif.Z * tint.Z, alpha);
                if (light.Server)
                {
                    ServerLanes(in light, tint, alpha, out d.Ambient, out d.Diffuse);
                    d.ServerLight = true;
                    d.LightDir = light.Dir;
                }
                d.EnvMap = envMapped;
                d.EnvTex = envMapped ? EnvTexOf(o) : 0;
                Vector3 emi = SceneDb.PartSelfIllum[p] + SceneDb.ObjEmissive[o];
                d.Emissive = new Vector4(emi, 1f);
            }
            if (_dumpLayer && _dumpPartN < DumpParts)
            {
                _dumpPartN++;
                Matrix4x4 pw = SceneDb.PartWorld[p];
                _log?.Info($"DbGui: [part {_dumpPartN}] o={o} p={p} kind={kind} cls={cls} "
                    + $"tex=\"{DrawRecipe.TexName(recipe, 0)}\"={tex} idx={idx} alpha={alpha:F2} "
                    + $"additive={additive} pos=({pw.M41:F3},{pw.M42:F3},{pw.M43:F3}) "
                    + $"m11={pw.M11:F3} m22={pw.M22:F3} {UvProbe(p)}");
            }
            GuiCapture.DbPart(in d);
            _parts++;
            issued++;
        }
        return issued;
    }

    /// <summary>The load screen panel's model, stock and every override of it that keeps the name.</summary>
    private const string LoadPanelModel = "pnl_loadscreen";

    /// <summary>The load screen's SURROUND: a huge quad stretching a two-texel sliver of <c>gui_boxes_ingame</c>
    /// (u 0.1289..0.1367) behind the art. The sliver's right end is the box's white border texel, which is the
    /// stock white stripe down the right of every load screen; the surround is meant to be black, and draws so.</summary>
    private static bool Surround(int recipe)
        => DrawRecipe.TexName(recipe, 0).Equals("gui_boxes_ingame", StringComparison.OrdinalIgnoreCase);

    /// <summary>The texture rectangle this part's own UVs cover, for the dump line. It is the one datum that
    /// separates "the material is wrong" from "the geometry asks for the wrong part of the atlas" -- a GUI box
    /// whose quad samples a hundredth of the atlas draws as a smear whatever the material does.</summary>
    private static string UvProbe(int p)
    {
        Mdl.ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length) return "uv=-";
        Mdl.MeshDef? mesh = m.Nodes[n].Mesh;
        if (mesh == null) return "uv=nomesh";
        var a = mesh.Uv[0];
        if (a.Length == 0) return $"uvsets={mesh.UvSetCount} uv0=empty";
        float u0 = a[0].X, u1 = a[0].X, v0 = a[0].Y, v1 = a[0].Y;
        foreach (var q in a)
        {
            u0 = System.MathF.Min(u0, q.X); u1 = System.MathF.Max(u1, q.X);
            v0 = System.MathF.Min(v0, q.Y); v1 = System.MathF.Max(v1, q.Y);
        }
        return $"uvsets={mesh.UvSetCount} uv0=({u0:F4}..{u1:F4},{v0:F4}..{v1:F4}) verts={a.Length}";
    }

    private static readonly Dictionary<string, TexId> _envMaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The GL texture an env-mapped part of object <paramref name="o"/> reflects: the map the game named for the
    /// object (<c>Gob::SetEnvironmentMap</c> @0x53f530). <c>DEFAULT</c>, a name that does not load and no name at all are the
    /// engine's null texture, which has no GL name, so the part takes the grey bake. 0 also while the named map is still loading.</summary>
    private static int EnvTexOf(int o)
    {
        string? name = SceneDb.ObjEnvMap[o];
        if (name == null || name.Equals("default", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!_envMaps.TryGetValue(name, out TexId id)) _envMaps[name] = id = TexRegistry.Acquire(name);
        return !TexRegistry.Final(id) && TexRegistry.TryDrawable(id, out uint gl, out _) ? (int)gl : 0;
    }

    /// <summary>The ambient and colour lanes of a server-lit draw: the light's own ambient and colour, each scaled by the
    /// object's tint, the colour lane's alpha carrying the part's opacity.</summary>
    internal static void ServerLanes(in CX.Services.Nui.ModelLight l, Vector4 tint, float alpha, out Vector4 ambient, out Vector4 colour)
    {
        var t = new Vector3(tint.X, tint.Y, tint.Z);
        ambient = new Vector4(l.Ambient * t, 1f);
        colour = new Vector4(l.Color * t, alpha);
    }

    /// <summary>Whether a texel's alpha is coverage on a GUI part. A lit scene's (a doll's) PLT or env-mapped part
    /// draws in the engine's opaque bucket, where that alpha is reflectivity; an unlit control keeps its alpha.</summary>
    internal static bool TexelAlphaIsCoverage(bool litScene, bool plt, bool envMapped)
        => !(litScene && (plt || envMapped));

    /// <summary>Does the texture this part actually binds resolve through the master palette? The override
    /// is asked first, exactly as <see cref="Texture"/> binds it -- an item icon arrives as a
    /// <c>Gob::ReplaceTexture</c> override, not as the model's own slot.</summary>
    private static bool Plt(int p, int recipe)
    {
        int ov = SceneDb.PartTexId[p * 4];
        TexId id = ov != 0 ? new TexId((uint)ov) : DrawRecipe.Tex(recipe, 0);
        bool plt = !id.IsNull && TexRegistry.KindOf(id) == TexKind.Plt;
        if (plt) _pltParts++;
        return plt;
    }

    /// <summary>GUI parts whose texture resolved through the master palette this session -- an item icon.</summary>
    private static long _pltParts;

    /// <summary>Does this part draw with an environment map: flagged so itself, or through its texture (its per-instance override, else the recipe's)?</summary>
    private static bool EnvMapped(int p, int recipe)
    {
        int ov = SceneDb.PartTexId[p * 4];
        return SceneDb.PartEnvMapped(p) || TexRegistry.EnvMapped(ov != 0 ? new TexId((uint)ov) : DrawRecipe.Tex(recipe, 0));
    }

    /// <summary>This part's diffuse as a GL name: its per-instance override when it has one
    /// (<c>Gob::ReplaceTexture</c> writes <see cref="SceneDb.PartTexId"/>), else the recipe's. 0 is the
    /// untextured white the GUI program falls back on, and a texture that is not resident yet registers the
    /// part as a dependent so its arrival re-dirties it.</summary>
    private static uint Texture(int p, int recipe)
    {
        int ov = SceneDb.PartTexId[p * 4];
        TexId id = ov != 0 ? new TexId((uint)ov) : DrawRecipe.Tex(recipe, 0);
        if (id.IsNull) return 0;
        if (TexRegistry.TryResident(id, out uint name, out _)) return name;
        TexRegistry.AddDependent(id, p);
        _noTexture++; _lNoTex++;
        return 0;
    }

    /// <summary><c>Gob::Render</c>'s alpha gate: the colour-shift alpha the facade mirrors at the engine's
    /// own <c>Gob+0x108</c>, seeded to 1 and written only by <c>Gob::SetColorShifting</c>. An object with no
    /// shell (a tile placement, a self-check fixture) is not gated.</summary>
    private static float ShellAlpha(int o)
    {
        nint shell = SceneDb.ObjFacade[o];
        return shell == 0 ? 1f : new CX.Engine.Gob(shell).Alpha;
    }

    /// <summary>Where the FACADE says this object is -- the words <c>Gob::GetPosition</c> answers and the ones
    /// the game writes without a setter. A column that disagrees is somebody's direct write. Dump only.</summary>
    private static string Shell(int o)
    {
        nint shell = CX.Alloy.Seam.Facade.ObjectShell(SceneDb.Objects.IdAt(o));
        if (shell == 0) return "-";
        Vector3 p = CX.Alloy.Seam.Facade.ShellPos(shell);
        return $"({p.X:F3},{p.Y:F3},{p.Z:F3})";
    }

    /// <summary>Case-insensitive prefix compare against the scene's own bytes, as the engine's own scene-name
    /// tests are. The bubble panels are <c>sceneGUI_PNL_BUBBLE&lt;w&gt;_&lt;h&gt;</c>, one per bubble size.</summary>
    private static bool Prefix(byte* s, ReadOnlySpan<byte> want)
    {
        for (int i = 0; i < want.Length; i++)
        {
            byte x = s[i];
            if (x == 0) return false;
            if (x >= 'A' && x <= 'Z') x += 32;
            byte y = want[i];
            if (y >= 'A' && y <= 'Z') y += 32;
            if (x != y) return false;
        }
        return true;
    }

    /// <summary>A text object's layout box, which is the other half of where its glyphs land: TextQuads
    /// centres a line inside <c>MaxWidth</c>, so a box of zero centres it on the object's own ORIGIN however
    /// right that origin is. Dump only; empty for anything that is not text.</summary>
    private static string TextBox(int o)
    {
        if (SceneDb.ObjKindOf[o] != ObjKind.Text) return "";
        TextDoc? d = TextObjects.Of(SceneDb.Objects.IdAt(o));
        return d == null ? " box=-"
             : $" box={d.MaxWidth:F3}x{d.MaxHeight:F3} align={d.AlignH:F2}/{d.AlignV:F2} lines={d.LineCount}"
               + $" visible={d.VisibleLines}@{d.TopLine} first={(d.LineCount > 0 && d.Lines[0] != null ? d.Lines[0].VisibleLength : -1)}"
               + $" colour=({d.Colours[0]:F2},{d.Colours[1]:F2},{d.Colours[2]:F2},{d.Colours[3]:F2})x{d.ColourCount}"
               + $" font={d.Font?.Name ?? "NONE"}/{d.Font?.Atlas ?? 0} shellalpha={ShellAlpha(o):F2}";
    }

    /// <summary>The behaviour word the GAME wrote into the facade, which is what <c>SetBehavior</c> stores
    /// whether or not DbAnim's column learned about it. Dump only.</summary>
    private static nint ShellBeh(int o)
    {
        nint shell = SceneDb.ObjFacade[o];
        return shell == 0 ? 0 : new CX.Engine.Gob(shell).Attach.Ptr;
    }

    /// <summary>An attached object whose anchor part did not resolve: name the parent's nodes once per
    /// parent MODEL, because "the attach named a part this model does not have" and "the attach named no part
    /// at all" are different bugs and only the node list tells them apart.</summary>
    private static void NoAnchor(int o)
    {
        DbId up = SceneDb.ObjAttachTo[o];
        if (!SceneDb.Objects.Valid(up) || _anchorSaid >= 4) return;
        Mdl.ModelDef? m = SceneDb.ObjModel[up.Index];
        if (m == null) return;
        _anchorSaid++;
        var sb = new System.Text.StringBuilder();
        for (int n = 0; n < m.Nodes.Length && n < 40; n++) { if (n > 0) sb.Append(", "); sb.Append(m.Nodes[n].Name); }
        _log?.Always($"DbGui: o={o} ({SceneDb.ObjKindOf[o]}) is attached to o={up.Index} \"{m.Name}\" with NO "
            + $"anchor part (ObjAttachPart=-1), so it rides that object's ROOT. Its parent's nodes are: {sb}");
    }

    private static int _anchorSaid;

    /// <summary>A part whose world matrix is not a number: said once with the object that carries it, and
    /// counted. The scale and position are the evidence whoever owns that measurement needs.</summary>
    private static void NotFinite(int o, int p)
    {
        _notFinite++;
        if (_saidNotFinite) return;
        _saidNotFinite = true;
        Vector3 pos = SceneDb.ObjPos[o];
        _log?.Always($"DbGui: a GUI part's world matrix is NOT FINITE and is NOT DRAWN -- o={o} "
            + $"\"{SceneDb.ObjModel[o]?.Name ?? "-"}\" in scene \"{SceneDb.SceneName[SceneDb.ObjScene[o].Index]}\", "
            + $"objpos=({pos.X},{pos.Y},{pos.Z}) objscale={SceneDb.ObjScale[o]} part={p}. The game computed "
            + "that placement from something the seam answered -- a measurement or a bound -- so the zero is "
            + "upstream of the draw (totals on /alloy).");
    }

    private static void Refuse()
    {
        _refusedNoPass++;
        if (_saidNoPass) return;
        _saidNoPass = true;
        _log?.Always("DbGui: GuiPass is not capturing (no render device, no program, or /alloy gui 0) -- "
                         + "the game's own GUI panels are NOT DRAWN in DB mode. There is no engine traversal "
                         + "underneath to fall back on: in DB mode this IS the GUI.");
    }

    private static long _parts, _noRecipe, _noGeometry, _noPalette, _noTexture, _badRange, _refusedNoPass;
    private static long _bubblesRefused, _notFinite;
    private static bool _saidNotFinite;
    private static bool _saidNoPass;

    /// <summary>Off-device: the one property the list exists for -- a GUI panel draws its controls in the
    /// order the game created them, because a panel has no depth to sort by. A list that reversed or lost an
    /// entry is a control painted behind the panel it belongs to, which reads as a missing control.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbGui.SelfCheck");
        for (int k = 0; k < SceneDb.MaxObjects; k++) { _objNext[k] = -1; _listedIn[k] = 0; }
        Array.Fill(_sceneFirst, -1); Array.Fill(_sceneLast, -1); _listed = 0;
        var scene = DbId.Make(5, 1);
        SceneDb.SceneIsWorld[scene.Index] = false;   // a panel, which is the only thing the list holds
        for (int o = 1; o <= 4; o++) Joined(o, scene);
        check.Expect(_listed == 4, "four controls listed");
        int n = 0;
        var seen = new int[8];
        for (int i = _sceneFirst[scene.Index]; i >= 0 && n < seen.Length; i = _objNext[i]) seen[n++] = i;
        check.Expect(n == 4 && seen[0] == 1 && seen[1] == 2 && seen[2] == 3 && seen[3] == 4,
                     "...in creation order, which is the order the panel paints them");
        Joined(2, scene);
        check.Expect(_listed == 4, "joining twice does not list an object twice");
        Left(2, scene);
        n = 0;
        for (int i = _sceneFirst[scene.Index]; i >= 0 && n < seen.Length; i = _objNext[i]) seen[n++] = i;
        check.Expect(n == 3 && seen[0] == 1 && seen[1] == 3 && seen[2] == 4 && _listed == 3,
                     "a destroyed control leaves the list and the rest keep their order");
        Left(4, scene);
        Joined(9, scene);
        n = 0;
        for (int i = _sceneFirst[scene.Index]; i >= 0 && n < seen.Length; i = _objNext[i]) seen[n++] = i;
        check.Expect(n == 3 && seen[2] == 9,
                     "a control created after the tail left still appends at the end");
        Left(1, scene); Left(3, scene); Left(9, scene);
        check.Expect(_listed == 0 && _sceneFirst[scene.Index] < 0, "the list empties completely");
        check.Expect(!TexelAlphaIsCoverage(true, true, false), "a doll's PLT part (a helm) takes no opacity from its palette alpha");
        check.Expect(!TexelAlphaIsCoverage(true, false, true), "a doll's env-mapped part (a weapon) takes none from its reflectivity mask");
        check.Expect(TexelAlphaIsCoverage(true, false, false), "a doll's plain textured part keeps its alpha");
        check.Expect(TexelAlphaIsCoverage(false, true, false), "an icon's PLT keeps its alpha outside a lit scene");
        check.Expect(TexelAlphaIsCoverage(false, false, true), "an unlit env-mapped control keeps its alpha");
        // Oracle: hand-written lane values for a half-tinted object under a coloured server light.
        ServerLanes(new CX.Services.Nui.ModelLight(true, new Vector3(1f, 0.5f, 0f), Vector3.UnitX, new Vector3(0.1f, 0.2f, 0.4f)),
                    new Vector4(0.5f, 1f, 1f, 1f), 0.75f, out Vector4 sa, out Vector4 sc);
        check.Expect(sa == new Vector4(0.05f, 0.2f, 0.4f, 1f) && sc == new Vector4(0.5f, 0.5f, 0f, 0.75f), "a server light's lanes are its ambient and colour times the tint, the colour lane carrying the opacity");
        log.Info(check.Ok ? "DbGui.SelfCheck: passed"
                          : "DbGui.SelfCheck: FAILED -- a GUI panel would paint its controls out of order, or a doll its metal see-through");
        return check.Ok;
    }

    internal static string Stats()
        => $"dbgui: listed {_listed} controls {_controls} parts {_parts} ({_pltParts} plt) text-runs {_textRuns}"
         + (_refusedNoLayer > 0 ? $" LAYERS REFUSED {_refusedNoLayer}" : "")
         + (_refusedNoPass > 0 ? $" NO GUI PASS {_refusedNoPass}" : "")
         + (_bubblesRefused > 0 ? $" {_bubblesRefused} vanilla bubble view(s) refused (CX draws its own)" : "")
         + (_lPending > 0 ? $" {_lPending} part(s) skipped still streaming" : "")
         + (_notFinite > 0 ? $" {_notFinite} part(s) with a NON-FINITE matrix NOT DRAWN" : "")
         + " " + DbAnim.AttachStats()
         + (_noRecipe + _noGeometry + _noPalette + _noTexture + _badRange == 0 ? ""
            : $" NOT DRAWN: {_noRecipe} no-recipe, {_noGeometry} no-geometry, {_noPalette} no-palette, "
            + $"{_badRange} bad-range; {_noTexture} untextured");
}
