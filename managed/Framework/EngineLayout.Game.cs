using System;

namespace CX.Framework;

/// <summary>The game-layer rows of <see cref="EngineLayout"/>: the radial menu, item icons, nameplates and consent,
/// the inventory screen, lights and behaviours.</summary>
public static unsafe partial class EngineLayout
{
    // ================= CRadialMenu =================
    // Linux from the ctor @0x583460 / SetEntryx @0x5837f0 / SetSubMenuCreatorx @0x583f50 / CreateSubMenu @0x584790 /
    // HandleMenuAction @0x585530; Windows from the same exported CRadialMenu bodies in nwmain.exe (ctor rva 0x1f7950,
    // SetEntryx 0x1fa250, SetSubMenuCreatorx 0x1fa9e0, SetSubMenu 0x1fa490, CreateSubMenu 0x1f8960, DisableEntry
    // 0x1f8b30, HandleMenuAction 0x1f9350, RecursiveAutoDelete 0x1f9da0, Activate 0x1f7e80, BuildIcon 0x1f8400),
    // read side by side -- docs/engine-layout.md "The radial rows". The per-slot entry and creator callbacks are
    // CGuiCallback member-fn pairs: {obj, fn, adj} = 0x18 on Itanium, {obj, fn} = 0x10 on MSVC, so every array
    // after them shifts. sizeof(CRadialMenu) is 0x878 / 0x7f8 (each build's deleting dtor), and every subclass
    // starts its own fields right there, so the subclass rows carry a uniform -0x80. CANDIDATES until a live
    // probe (logs=1) reads a root on each platform.

    /// <summary>CGuiCallback: the object word of one member-fn callback record. Unset = the record's own address
    /// (the self-pointer sentinel <c>SetEntryx</c> leaves and <c>HandleMenuAction</c> tests).</summary>
    public static readonly int CallbackObj = Field<IntPtr>("CGuiCallback", "obj");
    /// <summary>CGuiCallback: the member-fn word. Itanium: odd = a vtable byte offset + 1, and the this-adjust is
    /// the word right after it; MSVC: the code address, with no adjust word.</summary>
    public static readonly int CallbackFn = Field<IntPtr>("CGuiCallback", "fn");
    /// <summary>Bytes per callback record: Itanium <c>{obj, fn, adj}</c> vs MSVC <c>{obj, fn}</c>. The records of
    /// one menu lie end to end, so this is the view's indexer step.</summary>
    public static readonly int CallbackStride = Field("CGuiCallback", "stride");

    /// <summary>CRadialMenu: <c>CGuiTextBubble* m_pText[8]</c> -- the slot labels.</summary>
    public static readonly int RadialText = Field<CX.Engine.CGuiTextBubble>("CRadialMenu", "text", count: 8);
    /// <summary>CRadialMenu: entry callback[8] -- the address of the first record, indexed by
    /// <see cref="CallbackStride"/>.</summary>
    public static readonly int RadialEntry = Field<CX.Engine.CGuiCallback>("CRadialMenu", "entry", embedded: true);
    /// <summary>CRadialMenu: <c>long m_nParam[8]</c> -- shared by the entry and the creator of a slot.</summary>
    public static readonly int RadialParam = Field<long>("CRadialMenu", "param", count: 8);
    /// <summary>CRadialMenu: <c>uint flags[8]</c>; bit 0 = disabled (<c>DisableEntry</c>).</summary>
    public static readonly int RadialFlags = Field<uint>("CRadialMenu", "flags", count: 8);
    /// <summary>CRadialMenu: creator callback[8], same stride and sentinel as <see cref="RadialEntry"/>.</summary>
    public static readonly int RadialCreator = Field<CX.Engine.CGuiCallback>("CRadialMenu", "creator", embedded: true);
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pSubMenu[8]</c> -- the materialized child, null until CreateSubMenu.</summary>
    public static readonly int RadialSubMenu = Field<CX.Engine.CRadialMenu>("CRadialMenu", "subMenu", count: 8);
    /// <summary>CRadialMenu: <c>CRadialMenu* m_pParent</c>, set by HandleMenuAction on descent.</summary>
    public static readonly int RadialParent = Field<IntPtr>("CRadialMenu", "parent");
    /// <summary>CRadialMenu: <c>CGuiIcon* m_pIcon[8]</c>; the resref is the CResRef at CGuiIcon+0x8.</summary>
    public static readonly int RadialIcon = Field<CX.Engine.CGuiIcon>("CRadialMenu", "icon", count: 8);
    /// <summary>CRadialMenu: menu X / Y (int), filled by <c>Activate</c> from <c>CGuiMan::GetMousePos</c>.</summary>
    public static readonly int RadialMenuX = Field<int>("CRadialMenu", "menuX");
    /// <inheritdoc cref="RadialMenuX"/>
    public static readonly int RadialMenuY = Field<int>("CRadialMenu", "menuY");
    /// <summary>CRadialMenu: <c>m_bAutoDelete</c> (byte); every root and creator sets it 1. No <c>Byte01</c> probe:
    /// that reads a dword, and the ctor @0x583460 never writes +0x872/+0x873 (heap garbage) -- RadialService
    /// probes the byte itself.</summary>
    public static readonly int RadialAutoDelete = Field<byte>("CRadialMenu", "autoDelete");
    /// <summary>CRadialMenu: refcount (int); CreateSubMenu ++ on the child, RecursiveAutoDelete --.</summary>
    public static readonly int RadialRefCount = Field<int>("CRadialMenu", "refCount");
    /// <summary>CGuiIcon: the icon's <c>CResRef</c> (16 bytes) that <c>Initialize</c> (Linux @0x58cc20, Windows rva
    /// 0x1e4430: the first store on both, then the three size ints at +0x1c/+0x20/+0x24) and
    /// <c>ReplacePrimaryIconTexture</c> (@0x58cd30 / rva 0x1e4690) read.</summary>
    public static readonly int GuiIconResRef = Field<byte>("CGuiIcon", "resref", count: 16);
    /// <summary>CGuiIcon: the uses-left count label, a <c>CAurString*</c> (null when none). <c>SetCountText</c> (Linux
    /// @0x58d130, Windows rva 0x1e4720) tests, feeds and stores it at +0x40 on both; <c>CreateCountLabel</c> @0x58ce80
    /// makes it with <c>NewCAurString(1)</c>, so it is the same StringGob sub-object the bubble label is.</summary>
    public static readonly int GuiIconCountLabel = Field<CX.Engine.CAurString>("CGuiIcon", "countLabel");
    /// <summary>CGuiIcon: its size in 32 px inventory cells, <c>Initialize</c>'s +0x20/+0x24 stores on both.</summary>
    public static readonly int GuiIconCols = Field<int>("CGuiIcon", "cols");
    public static readonly int GuiIconRows = Field<int>("CGuiIcon", "rows");
    /// <summary>CGuiCreatureMenu / CGuiPlaceableMenu / CGuiDoorMenu / CGuiItemMenu / CClientTrapMenu: the target
    /// object id (uint), the first field after the CRadialMenu base on both builds. Read off each subclass
    /// constructor pair as the id argument's store, or the INVALID sentinel where the ctor takes none (item,
    /// whose <c>ActivateRadialMenu</c> stores the id later). Same offset in every subclass, so one row.</summary>
    public static readonly int RadialTargetId = Field<uint>("CRadialMenu", "targetId");
    /// <summary>CClientSpellMenu: the target object id (uint) the cast leaf reads. The ctor pair (Linux @0x6f1650,
    /// Windows rva 0x9a10e0) stores its <c>uint</c> argument there, right after the target <c>Vector</c>
    /// (<c>+0x900</c> / <c>+0x880</c>); <c>RadialMenuCast</c> (Linux @0x6efb10, Windows rva 0x9a4540) passes it as
    /// <c>PerformCastSpell</c>'s target on both.</summary>
    public static readonly int SpellMenuTargetId = Field<uint>("CClientSpellMenu", "targetId");
    /// <summary><c>sizeof(CGuiCreatureMenu)</c>: the <c>operator new</c> before its ctor (Linux
    /// <c>ActivatePlayerCharacterRadialMenu</c> @0x73f3a0, Windows the twin of the associate root, rva 0x98e140's caller).</summary>
    public static readonly int CreatureMenuSize = Field("CGuiCreatureMenu", "sizeof");
    /// <summary><c>sizeof(CClientSpellMenu)</c>: the <c>operator new</c> in every slot-1 cast creator (Linux
    /// <c>CreateClientSpellMenu</c> @0x73b360), the deleting dtor's size on Windows (rva 0x9a19c0).</summary>
    public static readonly int SpellMenuSize = Field("CClientSpellMenu", "sizeof");
    /// <summary>CGuiTextBubble: the <c>CAurString*</c> the label lives in -- the ctor (@0x588690 Linux, @rva 0x1fd310
    /// Windows) stores <c>NewCAurString(-1)</c> there and <c>SetText</c> @0x588790 feeds it through the CAurString
    /// vtable.</summary>
    public static readonly int TextBubbleString = Field<CX.Engine.CAurString>("CGuiTextBubble", "aurString");

    // ================= item icons =================
    // CGuiIcon's three subclasses, each read off its ctor on BOTH builds (Linux @0x58dcb0 / @0x58d9d0 / @0x58d5c0,
    // Windows rva 0x1e3000 / 0x1e2e40 / 0x1e2ca0): every subclass field lands at the same offset on each, because
    // the base is 0x48 on both (a single vptr, no Itanium-only padding). The model builders confirm the reads:
    // CLayeredIcon::CreateCopyIconModel @0x58c8b0 hands +0x48/+0x50 to the PLT ReplaceTexture, CCompositeIcon's
    // @0x58d320 replaces gui_replace01/02/03 with +0x8/+0x48/+0x59, CArmorIcon::LoadArmorLayeredIconModel
    // @0x58d7c0 (rva 0x1e4470) walks +0x48..+0xae by 0x11 and indexes +0xc0 by the compacted layer.

    /// <summary>CLayeredIcon: <c>int</c> palette-scheme length (10 for an item: <c>CNWCItem::UpdateIcons</c> @0x7cbc50
    /// passes 10) and the <c>unsigned short*</c> scheme the PLT is tinted with.</summary>
    public static readonly int LayeredIconSchemeCount = Field<int>("CLayeredIcon", "schemeCount");
    /// <inheritdoc cref="LayeredIconSchemeCount"/>
    public static readonly int LayeredIconScheme = Field<IntPtr>("CLayeredIcon", "scheme");
    /// <summary>CCompositeIcon: the second and third <c>CResRef</c> (17 bytes each, the first is the base's +0x8),
    /// bound to <c>gui_replace02</c> / <c>gui_replace03</c> of the <c>GUI_ICON_%dX%dLC</c> model -- bottom to top.</summary>
    public static readonly int CompositeIconResRef2 = Field<byte>("CCompositeIcon", "resref2", count: 17);
    /// <inheritdoc cref="CompositeIconResRef2"/>
    public static readonly int CompositeIconResRef3 = Field<byte>("CCompositeIcon", "resref3", count: 17);
    /// <summary>CArmorIcon: six <c>CResRef</c> layers at stride <see cref="ArmorIconResRefStride"/> (+0x48..+0xae),
    /// an empty one skipped; the shared scheme (<c>int</c> count, <c>unsigned short*</c>) and the optional per-layer
    /// scheme table (<c>count</c> shorts per EXISTING layer, null = every layer shares the one scheme).</summary>
    public static readonly int ArmorIconResRefs = Field<byte>("CArmorIcon", "resrefs", count: 102);   // ArmorIconLayers * ArmorIconResRefStride
    /// <summary><c>sizeof(CResRef)</c> as the armour layer array packs it (17 bytes, unaligned) and the layer count
    /// (the ctor's zero loop stops at +0xae = 0x48 + 6 * 0x11 on both builds). Values, not places -- no row.</summary>
    public const int ArmorIconResRefStride = 0x11;
    /// <inheritdoc cref="ArmorIconResRefStride"/>
    public const int ArmorIconLayers = 6;
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconSchemeCount = Field<int>("CArmorIcon", "schemeCount");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconScheme = Field<IntPtr>("CArmorIcon", "scheme");
    /// <inheritdoc cref="ArmorIconResRefs"/>
    public static readonly int ArmorIconLayerSchemes = Field<IntPtr>("CArmorIcon", "layerSchemes");
    /// <summary>CNWCItem -&gt; the item's own <c>CGuiIcon*</c> (plain, layered, composite or armour by vptr): built and
    /// REPLACED (old one deleted) by <c>CNWCItem::UpdateIcons</c> @0x7cbc50 on every item update, so it is re-read
    /// from the item every frame, never held. <c>CGuiItemMenu::ActivateRadialMenu</c> hands it to <c>SetCenter</c>:
    /// Linux @0x7d3720 reads <c>item+0x258</c>, Windows rva 0x9a59d0 <c>item+0x238</c>.</summary>
    public static readonly int ItemGuiIcon = Field<IntPtr>("CNWCItem", "guiIcon");
    /// <summary>CNWCItem's deleting destructor in its primary vtable: Itanium's D0 at +0x08 (<c>_ZN8CNWCItemD0Ev</c>, after
    /// the complete D1), MSVC's scalar deleting destructor, the table's only slot (vftable rva 0xe4ceb0 -&gt; 0x83c8a0:
    /// the dtor, then <c>operator delete(this, 0x2b8)</c> when <c>flags &amp; 1</c>). The two take different arguments,
    /// so NuiItemIcons calls it per OS.</summary>
    public static readonly int ItemDeleteSlot = Field("CNWCItem", "vtbl.Delete", Expect.VtblSlot);
    /// <summary>The <c>CNWCObject</c> inside a <c>CNWCItem</c>: what the game-object array registers.</summary>
    public static readonly int ItemAsObject = Field<CX.Engine.CNWCObject>("CNWCItem", "asObject", embedded: true);
    /// <summary>CGameObject: the object type byte <c>CClientExoAppInternal::GetItemByGameObjectID</c> tests for 6
    /// (item) before casting -- Linux @0x63f3a0, Windows rva 0x6d82a0, both <c>go+0xc</c>.</summary>
    public static readonly int GameObjType = Field<byte>("CGameObject", "type");
    /// <summary>Virtual slot of <c>CGameObject::AsNWCItem()</c>, the cast the same two bodies make after the type
    /// test: Linux <c>vtbl+0x70</c>, Windows <c>vtbl+0x68</c> (the usual one-destructor-slot shift).</summary>
    public static readonly int GameObjAsNWCItemSlot = Field("CGameObject", "vtbl.AsNWCItem", Expect.VtblSlot, sig: "IntPtr()");

    // ================= the last private offset constants (nameplate, consent, pose) =================
    // Three files kept their own `private const int` / `Win ? a : b` offsets. Every one is a row here now,
    // re-measured 2026-09-24 by decompiling the SAME function on both programs of $GHIDRA_PROJECT/nwn-linux.rep
    // (tools/ghidra/DecompOne.java); the anchor and its address per build is on each row and in
    // docs/engine-layout.md. Linux addresses are nm (Ghidra shows nm + 0x100000), Windows are RVAs.

    /// <summary>CNWCObject -&gt; its <c>CGuiInGamePopUpBubble*</c> (nullable, one per object).
    /// <c>CNWCObject::SetPopUpBubbleText</c> (Linux nm @0x7e8050, Windows rva 0x766450) reads
    /// <c>this[0x17]</c> and <c>new</c>s the bubble into it on both builds.</summary>
    public static readonly int ObjBubble = Field<CX.Engine.CGuiInGamePopUpBubble>("CNWCObject", "bubble");

    /// <summary>CNWCObject: 1 =&gt; the server has retired this object -- no plate and no bubble. The first test
    /// in <c>SetPopUpBubbleText</c> on both builds. No probe: its writer set is known but its semantics are
    /// not, so 0/1 is not something to assert.</summary>
    public static readonly int ObjRetired = Field<int>("CNWCObject", "retired");

    // CNWCObject's fade machine. Read off CNWCObject::FadeObject (Linux nm @0x7e7720, Windows rva 0x762e40) and
    // SetDesiredFadeState (Linux nm @0x7e79e0, Windows rva 0x765920): identical offsets on both builds.
    /// <summary>CNWCObject -&gt; the fade state <see cref="ObjVisible"/> is walking toward (byte: 0 out, 1 in).</summary>
    public static readonly int ObjFadeTarget = Field<byte>("CNWCObject", "fadeTarget");
    /// <summary>CNWCObject -&gt; nonzero = expire on fade-out (int). When the fade lands at 0, <c>FadeObject</c> stamps the
    /// time and detaches the gob (<c>CNWCAnimBase::AttachToScene(null)</c>); once <see cref="ObjFadeHoldMs"/> has
    /// passed it returns true and <c>AIUpdate</c> deletes the object.</summary>
    public static readonly int ObjFadeExpire = Field<int>("CNWCObject", "fadeExpire");
    /// <summary>CNWCObject -&gt; milliseconds a faded-out object is kept before it expires (uint). Read only while
    /// <see cref="ObjFadeExpire"/> is set; <c>SetDesiredFadeState</c> writes the two together.</summary>
    public static readonly int ObjFadeHoldMs = Field<uint>("CNWCObject", "fadeHoldMs");
    /// <summary>CNWCObject -&gt; the alpha the fade has reached (float, 0..1). <c>FadeObject</c> steps it by
    /// <c>dt_ms / 2000</c> and lands the fade the step it crosses <c>GetFinalFadeAlpha</c>.</summary>
    public static readonly int ObjFadeAlpha = Field<float>("CNWCObject", "fadeAlpha");

    /// <summary><c>ObjectTextBubbleOverride</c> -- the module's own plate text: mode <c>int</c>, then a
    /// <c>CExoString</c> (<c>char*</c> + a dword length). 1 = replace, 2 = prepend, 3 = append, anything
    /// else = leave alone. All three are read at the same offsets by both
    /// <c>ShowFeedbackBubbleText</c> overrides, the item one through the <c>CNWCObject</c> subobject.</summary>
    public static readonly int ObjOverrideMode = Field<int>("CNWCObject", "overrideMode");
    /// <inheritdoc cref="ObjOverrideMode"/>
    public static readonly int ObjOverrideText = Field<IntPtr>("CNWCObject", "overrideText");
    /// <inheritdoc cref="ObjOverrideMode"/>
    public static readonly int ObjOverrideLen = Field<int>("CNWCObject", "overrideLen");

    /// <summary>CNWCItem's "the feedback plate is currently shown for me" flag, measured from the
    /// <b>CNWCObject base subobject</b> -- the only item pointer CX ever holds, which is why the row's class
    /// is CNWCObject and not the declaring CNWCItem. Linux's own offset needs the base adjustment and Windows'
    /// does not, because MSVC hands the override the subobject directly.</summary>
    public static readonly int ObjItemPlated = Field<int>("CNWCObject", "itemPlated");

    /// <summary>CNWCCreature -&gt; the area id the plate is gated on: hidden when it differs from the player's.
    /// <c>CNWCCreature::UpdatePopUpBubble</c> (Linux nm @0x72e600, Windows rva 0x8107a0) is two reads on each
    /// build -- <c>this+area == module[currentArea][id]</c> -- and carries the class's usual Windows -0x20.</summary>
    public static readonly int CreatureArea = Field<uint>("CNWCCreature", "area");

    /// <summary>CNWCArea -&gt; its area id, the right-hand side of that same comparison (<c>area+0x118</c> on
    /// both builds).</summary>
    public static readonly int AreaId = Field<uint>("CNWCArea", "id");

    /// <summary>CNWCCreatureStats: the override name (<c>char*</c> + dword length) a disguise sets, which wins
    /// outright over first/last. <c>CNWCCreatureStats::GetFullName</c> (Linux nm @0x74db80, Windows rva
    /// 0x7f3f00) tests the length first and returns this pointer alone when it is non-zero -- same offsets,
    /// same order, on both builds.</summary>
    public static readonly int StatsOverrideName = Field<IntPtr>("CNWCCreatureStats", "overrideName");
    /// <inheritdoc cref="StatsOverrideName"/>
    public static readonly int StatsOverrideNameLen = Field<uint>("CNWCCreatureStats", "overrideNameLen");

    /// <summary>CNWCCreatureStats: damage-level byte 0..5 (5 = dead) -- a server-sent enum indexing
    /// <c>damagelevels.2da</c>, not a client HP division. <c>ShowFeedbackBubbleText</c> reads
    /// <c>*(byte*)(stats + 0x8e)</c> to pick the injury line's colour on both builds.</summary>
    public static readonly int StatsDamage = Field<byte>("CNWCCreatureStats", "damage");

    /// <summary>The 2DA table -&gt; <c>damagelevels.2da</c> (+0x238 on both builds): <c>CNWCCreature::ShowFeedbackBubbleText</c>
    /// (Linux @0x71a800, Windows rva 0x80b240) reads it as <c>*(*(g_pRules + 0x130) + 0x238)</c> and takes the
    /// <c>STRING_REF</c> cell of the row the damage byte names.</summary>
    public static readonly int TwoDimDamageLevels = Field<IntPtr>("CTwoDimArrays", "damageLevels");

    /// <summary>CNWCCreatureStats: current HP (temporary excluded), temporary HP and max HP, shorts. One
    /// creature-update flag is their only writer (Linux nm @0x7a1550, Windows rva 0x781e80: four
    /// <c>ReadSHORT</c>s beside <c>CPanelPartyBar::SetMemberHealth</c>), and the server sends it for the
    /// player's own creature and party members only. Same offsets on both builds.</summary>
    public static readonly int StatsCurHp = Field<short>("CNWCCreatureStats", "curHp");
    /// <inheritdoc cref="StatsCurHp"/>
    public static readonly int StatsTempHp = Field<short>("CNWCCreatureStats", "tempHp");
    /// <inheritdoc cref="StatsCurHp"/>
    public static readonly int StatsMaxHp = Field<short>("CNWCCreatureStats", "maxHp");

    /// <summary>CGuiInGamePopUpBubble: the projected screen anchor, whole-pixel snapped.
    /// <c>CGuiInGamePopUpBubble::Show(Vector)</c> (Linux nm @0x6a05a0, Windows rva 0x6e9820) stores the vector
    /// there -- x and y as one 8-byte store, z after. NOT the space a <c>Window</c> draws in: see
    /// <c>NameplateNative.AnchorRaw</c>.</summary>
    public static readonly int BubbleAnchorX = Field<float>("CGuiInGamePopUpBubble", "anchorX");
    /// <inheritdoc cref="BubbleAnchorX"/>
    public static readonly int BubbleAnchorY = Field<float>("CGuiInGamePopUpBubble", "anchorY");

    /// <summary>CGuiInGamePopUpBubble: 1 while live (chat or feedback) text owns the bubble. Every
    /// <c>ShowFeedbackBubbleText</c> clears it through <c>obj-&gt;bubble</c> before building a plate, on both
    /// builds; it is the field just below the mode <c>SetChatBubbleMode</c> writes.</summary>
    public static readonly int BubbleLiveText = Field<int>("CGuiInGamePopUpBubble", "liveText");

    /// <summary>Camera: the origin of the viewport rect <c>{x, y, w, h}</c> the camera projects into --
    /// <see cref="CamViewW"/> / <see cref="CamViewH"/> are its other two floats. All four are the consecutive
    /// stores of <c>Camera::SetViewPort(float,float,float,float)</c> (Linux nm @0x4a38a0, Windows rva
    /// 0x5b810, each clamping w and h up to 1.0), which is Camera's measured <c>+0x4</c> block.</summary>
    public static readonly int CamViewX = Field<float>("Camera", "viewX");
    /// <inheritdoc cref="CamViewX"/>
    public static readonly int CamViewY = Field<float>("Camera", "viewY");
    // The offset is the LIST HEAD, read off Camera::GetViewVolume(int)'s one load on both builds (Linux nm
    // @0x4a3a60, Windows rva 0x59000); the extra +4 over Camera's usual window is the pad the ViewVolumeSet
    // int pushes the pointer past.
    /// <summary>Camera -&gt; the world-space frustum as a <c>List&lt;Plane&gt;</c> head.
    /// <c>Camera::RenderScene</c> builds it every frame, so it is an INPUT to the scene walk and stays current
    /// when the walk is skipped. Untyped on purpose: a list head is not a field, so the accessor is the
    /// <see cref="CX.Engine.Camera.ViewVolume"/> concept.</summary>
    public static readonly int CamViewVolume = Field("Camera", "viewVolume");

    /// <summary>CClientExoAppInternal + 0x180: bit 0 = the "show mouse-over feedback" option, bit 1 =
    /// highlight-all (Tab) currently held. The engine tests the WHOLE word, which is why Tab plates even with
    /// the option off. The option's setter is one store on both builds
    /// (<c>*(uint*)(*(g_pAppManager) + 8) + 0x180) = *arg</c>: Linux nm @0x8ca7f0, Windows rva 0x6bdb20).</summary>
    public static readonly int InternalFeedbackWord = Field<int>("CClientExoAppInternal", "feedbackWord");
    /// <summary>Gob: the engine's in-view-volume flag, refreshed by <c>ManageSceneBSP</c>. The whole body of
    /// <c>Gob::IsInViewVolume</c> is <c>return *(this + 0x1b)</c> on both builds (Linux nm @0x444780, Windows
    /// rva 0x49110, name-exported <c>?IsInViewVolume@Gob@@UEAA_NXZ</c>). No <c>Byte01</c>: the dword at this
    /// offset spans <see cref="GobPropagateHilite"/>, so the predicate would judge two fields.</summary>
    public static readonly int GobInView = Field<byte>("Gob", "inView");
    /// <summary>Gob -&gt; the <c>LookAtAnimation</c> the engine is currently aiming this gob's head with, or null.
    /// <c>Gob::EndLookAtAnimate</c> reads it and calls a virtual on it at the same offset on both builds (Linux
    /// nm @0x4979e0, Windows rva 0x50eb0, name-exported <c>?EndLookAtAnimate@Gob@@UEAAXXZ</c>).</summary>
    public static readonly int GobLookAt = Field<CX.Engine.LookAtAnimation>("Gob", "lookAt");
    /// <summary>Model -&gt; its name, MaxTree's <c>char[64]</c>: what the model file states. <c>Gob::GetModelName</c>
    /// (Linux nm @0x537270, Windows rva 0x8f410, name-exported) answers <c>*(gob+0x68) + 0x10</c> on both builds.</summary>
    public static readonly int ModelName = Field<byte>("Model", "name");
    /// <summary><c>sizeof(CPanelOkCancel)</c>: the <c>operator new</c> argument in the engine's own spawn path
    /// -- Linux <c>COptionsPanel::HandleQuitClick</c> nm @0x6b9800 allocates 0x3a0, the Windows ExitButton twins
    /// (rva 0x7103a0, 0x84f480) allocate 0x360. The wrong size and the ctor/dtor touch out-of-bounds
    /// bytes.</summary>
    public static readonly int OkCancelSize = Field("CPanelOkCancel", "sizeof");
    /// <summary>CGuiPanel: the embedded <c>CGuiCallbackv1&lt;void,int&gt;</c> the panel invokes on close --
    /// the record CX fills in to be told the player's answer. The constructor (Linux nm @0x586fb0, Windows rva
    /// 0x1fbb80) seeds it with the unset sentinel <see cref="CX.Engine.CGuiCallback.IsSet"/> tests, and
    /// <c>Deactivate(int)</c> invokes it through the adjust word on Itanium and flat under MSVC.</summary>
    public static readonly int GuiPanelCloseCallback = Field<CX.Engine.CGuiCallback>("CGuiPanel", "closeCallback", embedded: true);
    // ================= the CX inventory screen =================
    // Every row below was PAIRED: the same function decompiled on both programs of $GHIDRA_PROJECT/nwn-linux.rep,
    // Linux addresses as nm (Ghidra = nm + 0x100000), Windows as RVAs off base 0x140000000. The uniform
    // CNWCItem / CNWCCreature Windows -0x20 held everywhere it was measured here, but no row rests on it:
    // the pairing function is named on each. The inventory data model is NOT a client-side repository -- it
    // is these per-item fields plus the creature's flat equipped-id array.

    /// <summary>CNWCCreature: the flat <c>uint[18]</c> of equipped item object ids, indexed by
    /// <c>log2(INVENTORY_SLOT_*)</c> -- 0 HEAD to 17 CARMOUR, with an INVALID sentinel for empty. Measured as
    /// the whole switch of <c>GetEquippedItemID</c> (Linux nm @0x7212e0, Windows rva 0x800dd0): 18 consecutive
    /// dwords on both, so the array is the read and no call is needed.</summary>
    public static readonly int CreatureEquippedItems = Field<uint>("CNWCCreature", "equippedItems", count: 18);
    /// <summary>CNWCCreature: gold pieces, what the inventory's <c>GPLabel</c> prints. Read by
    /// <c>CPanelInventory::SetCharacterData</c> (Linux nm @0x68d810 <c>creature+0x1d8</c>, Windows rva 0x856fa0
    /// <c>creature+0x1b8</c>) as the <c>%d</c> of TLK 0x1907.</summary>
    public static readonly int CreatureGold = Field<uint>("CNWCCreature", "gold");
    /// <summary>CNWCCreature: non-zero on an associate (henchman, familiar, summon, companion), whose master's id is
    /// <see cref="CreatureMasterId"/>. <c>CGuiInvbox::HandleDropAsSource</c> (Linux @0x690990 <c>+0x294</c> / <c>+0x290</c>,
    /// Windows rva 0x8813d0 <c>+0x274</c> / <c>+0x270</c>) lets a potion dropped on the player's own associate be used on it.</summary>
    public static readonly int CreatureIsAssociate = Field<int>("CNWCCreature", "isAssociate");
    /// <inheritdoc cref="CreatureIsAssociate"/>
    public static readonly int CreatureMasterId = Field<uint>("CNWCCreature", "masterId");
    /// <summary>CPanelQuickBar: its 36 <c>CGuiQuickButton</c>s (three sets of 12) laid inline from <see cref="QuickbarButtons"/>,
    /// <see cref="QuickbarButtonStride"/> apart. <c>SetButton_Item</c> (Linux @0x6b1ab0 <c>slot*0x328 + 0xf8</c>, Windows rva
    /// 0x86b3d0 <c>slot*0x2c8 + 0xf0</c>); <c>UpdateItemButtons</c> stops at <c>+0x7298</c> (36 × 0x328 + 0xf8), and both
    /// quick-button drop handlers read the flag just past the array (<c>+0x72a4</c> / <c>+0x651c</c>).</summary>
    public static readonly int QuickbarButtons = Field("CPanelQuickBar", "buttons");
    /// <inheritdoc cref="QuickbarButtons"/>
    public static readonly int QuickbarButtonStride = Field("CPanelQuickBar", "buttonStride");
    /// <inheritdoc cref="QuickbarButtons"/>
    public const int QuickbarButtonCount = 36;
    /// <summary>CGuiQuickButton: what it holds, the <c>HandleLeftButton</c> switch (0 empty, 1 item, 2 spell, ...), and an item
    /// button's item object id. <c>SetButtonItem</c> writes both: Linux nm @0x6b1270 <c>+0x160</c> / <c>+0x10c</c>, Windows rva 0x867760 <c>+0x140</c> / <c>+0xec</c>.</summary>
    public static readonly int QuickButtonType = Field<byte>("CGuiQuickButton", "type");
    /// <inheritdoc cref="QuickButtonType"/>
    public static readonly int QuickButtonItem = Field<uint>("CGuiQuickButton", "itemOid");
    /// <summary>The rest of a button's content, as <c>SendButtonUpdateToServer</c> (Linux nm 0x6aa2e0, Windows rva 0x8602a0) reads it
    /// for the server, Windows 0x20 lower: a pair's second item, the int argument, the packed spell, an associate's target.</summary>
    public static readonly int QuickButtonItem2 = Field<uint>("CGuiQuickButton", "itemOid2");
    public static readonly int QuickButtonParam = Field<int>("CGuiQuickButton", "param");
    public static readonly int QuickButtonSpell = Field<long>("CGuiQuickButton", "spell");
    public static readonly int QuickButtonTarget = Field<uint>("CGuiQuickButton", "target");
    /// <summary>CPanelQuickBar's send gate: <c>SendButtonUpdateToServer</c> returns at 0 (the server's own set zeroes it around its writes).</summary>
    public static readonly int QuickbarSendGate = Field<int>("CPanelQuickBar", "sendGate");
    /// <summary>CGuiObject flags; bit 0 = disabled, which <c>CGuiButton::HandleLMouseDown</c> refuses on (<c>+8</c>, both builds).</summary>
    public static readonly int GuiObjectFlags = Field<uint>("CGuiObject", "flags");
    /// <summary>CGuiButton -&gt; its normal-state animation name (<c>char*</c>); quick buttons outline with <c>"qbEquip"</c> /
    /// <c>"qbMode"</c>. <c>SetButtonNormalAnimation</c> stores it at Linux <c>+0xe0</c>, Windows rva 0x1e5a00 <c>+0xc0</c>.</summary>
    public static readonly int ButtonNormalAnim = Field<IntPtr>("CGuiButton", "normalAnim");
    /// <summary>CNWCCreature: carried weight in <b>tenths of a pound</b> -- the label is
    /// <c>floor(weight / 10 + 0.5)</c>. Same two bodies (<c>+0x2ac</c> / <c>+0x28c</c>), and the same field the
    /// <c>'U'</c> case of <c>HandleServerToPlayerUpdate_GuiInventory</c> writes (Linux nm @0x79dd90, Windows rva
    /// 0x7b3f30). The max comes from <c>encumbrance.2da</c> column "Normal" at row = STR, not from the creature.
    /// </summary>
    public static readonly int CreatureCarriedWeight = Field<int>("CNWCCreature", "carriedWeight");
    /// <summary>CNWCItem: the base item id -- the index into <see cref="SymbolMap.GetBaseItem"/>'s array,
    /// and the same field on BOTH builds, because it sits below the class's divergence. Read by
    /// <c>CNWCItem::GetName</c> (Linux nm @0x7ce810, Windows rva 0x83d8a0) among others. Gold is base item 76;
    /// NOT the icon's base item, which is a second field.</summary>
    public static readonly int ItemBaseItem = Field<int>("CNWCItem", "baseItem");
    /// <summary>CNWCItem: non-zero =&gt; identified. <c>GetName</c> reads it twice on both builds
    /// (<c>+0x1e0</c> / <c>+0x1c0</c>): it gates the custom name, and when clear it prefixes TLK 0x15ac.</summary>
    public static readonly int ItemIdentified = Field<int>("CNWCItem", "identified");
    /// <summary>CNWCItem: the ghost / in-flight flag -- non-zero while a move waits on the server, which blocks a
    /// drag (<c>CGuiInvbox::CanEquipItem</c> @0x6906c0 refuses on it) and draws the icon at 0.4 alpha.
    /// <c>HandleServerToPlayerUpdate_GuiRepository</c> clears it on every confirmation (Linux nm @0x79e320
    /// <c>item+0x260</c>, Windows rva 0x7b4620 <c>item+0x240</c>).</summary>
    public static readonly int ItemGhost = Field<int>("CNWCItem", "ghost");
    /// <summary>CNWCItem: the <c>INVENTORY_SLOT_*</c> bitmask the item is equipped in, <c>-1</c> when it is not.
    /// <c>HandleServerToPlayerUpdate_GuiInventory</c> reads and writes it on both builds (Linux nm @0x79dd90
    /// <c>item+0x26c</c>, Windows rva 0x7b3f30 <c>item+0x24c</c>), beside a second slot field one dword up for
    /// two-handed / dual references.</summary>
    public static readonly int ItemEquipSlot = Field<int>("CNWCItem", "equipSlot");
    /// <summary>CNWCItem: the CONFIRMED backpack cell, two bytes. <c>y</c> is <b>global</b>
    /// (<c>page * rows + localY</c>), which is why one item's y can exceed the grid's 6 rows. Written by the
    /// <c>'A'</c> and <c>'M'</c> cases of <c>Update_GuiRepository</c> and read back by its remove step, on both
    /// builds. The pending pair the ghost writes is the next two bytes and is not declared.</summary>
    public static readonly int ItemRepoX = Field<byte>("CNWCItem", "repoX");
    /// <inheritdoc cref="ItemRepoX"/>
    public static readonly int ItemRepoY = Field<byte>("CNWCItem", "repoY");
    /// <summary>CNWCItem: the possessing creature's object id, INVALID when the item is on the ground.
    /// <c>Update_GuiRepository</c> sets it to the panel's shown oid on <c>'A'</c> and to INVALID on <c>'D'</c>
    /// (Linux nm @0x79e320, Windows rva 0x7b4620). Measured from the CNWCItem base, not the CNWCObject
    /// subobject -- contrast <see cref="ObjItemPlated"/>.</summary>
    public static readonly int ItemPossessor = Field<uint>("CNWCItem", "possessor");
    /// <summary>CNWCItem: 1 =&gt; the item sits in a container. The <c>'A'</c> of <c>Update_GuiContainer</c> sets it (Linux nm
    /// @0x79ee90 <c>item+0x288</c>, Windows rva 0x7b3790 <c>item+0x268</c>) and the <c>'A'</c> of <c>Update_GuiRepository</c>
    /// clears it (@0x79e320 / rva 0x7b4620). Both, and the container's <c>'D'</c>, stamp <see cref="ItemPossessor"/> with the
    /// panel's shown creature -- the player -- so this is what tells looted items from carried ones.</summary>
    public static readonly int ItemInContainer = Field<int>("CNWCItem", "inContainer");
    /// <summary>CNWCItem: stack size. The first store of <c>CNWCItem::SetStackSize(int)</c> on both builds
    /// (Linux nm @0x7cdbd0 <c>item+0x2a4</c>, Windows rva 0x83f190 <c>item+0x284</c>), which then suppresses the
    /// count label when <see cref="BaseItemStacking"/> is 1 or the count is below 2.</summary>
    public static readonly int ItemStackSize = Field<int>("CNWCItem", "stackSize");
    /// <summary>CNWCItem: the custom (renamed) item name as a <c>char*</c> + dword length. <c>GetName</c> tests
    /// the length and returns this text outright when the item is identified (Linux nm @0x7ce810
    /// <c>item+0x2b0/+0x2b8</c>, Windows rva 0x83d8a0 <c>item+0x290/+0x298</c>). Single-byte codepage, not UTF-8
    /// (docs: engine-text-codepage).</summary>
    public static readonly int ItemCustomName = Field<IntPtr>("CNWCItem", "customName");
    /// <inheritdoc cref="ItemCustomName"/>
    public static readonly int ItemCustomNameLen = Field<uint>("CNWCItem", "customNameLen");
    /// <summary>CNWBaseItem (a 200-byte record of <c>baseitems.2da</c>, reached through
    /// <see cref="SymbolMap.GetBaseItem"/>): the <b>equippable-slot bitmask</b>. The whole client-side equip
    /// rule is <c>(base-&gt;equipSlots &amp; invbox-&gt;slotMask) != 0</c>. Identical on both builds, paired
    /// on the shared-engine <c>CNWSCreature::CanEquipItem</c> (Linux nm @0xa7adf0, Windows rva 0x3d12c0).</summary>
    public static readonly int BaseItemEquipSlots = Field<uint>("CNWBaseItem", "equipSlots");
    /// <summary>CNWBaseItem: <c>InvSlotWidth</c> / <c>InvSlotHeight</c> in grid cells, already parsed by the client
    /// -- no 2DA read. <c>CNWCItem::UpdateIcons</c> @0x7cbc50 copies them onto the built <c>CGuiIcon</c>, which is
    /// what the grid places and reserves cells with. Identical on both builds, paired on the exported
    /// <c>CItemRepository::CheckBaseItemFits</c> (Linux nm @0xb439c0, Windows rva 0x4659e0).</summary>
    public static readonly int BaseItemInvSlotWidth = Field<byte>("CNWBaseItem", "invSlotWidth");
    /// <inheritdoc cref="BaseItemInvSlotWidth"/>
    public static readonly int BaseItemInvSlotHeight = Field<byte>("CNWBaseItem", "invSlotHeight");
    /// <summary>CNWBaseItem: the stacking limit as a <c>short</c>; <c>== 1</c> means "never show a count label".
    /// Identical on both builds -- <c>CNWCItem::SetStackSize</c> tests <c>*(short*)(base + 0x24) != 1</c> on each
    /// (Linux nm @0x7cdbd0, Windows rva 0x83f190).</summary>
    public static readonly int BaseItemStacking = Field<short>("CNWBaseItem", "stacking");
    /// <summary>CNWBaseItem: non-zero =&gt; a double-click means <see cref="SymbolMap.SendInputUseObject"/>; zero
    /// means "activate an item property" (walk the item's properties for type 0xf and open an action target).
    /// Identical on both builds -- <c>CGuiMultiInvbox::HandleDoubleClick</c> branches on
    /// <c>*(int*)(base + 0x80)</c> on each (Linux nm @0x690300, Windows rva 0x8801b0).</summary>
    public static readonly int BaseItemUsable = Field<int>("CNWBaseItem", "usable");
    /// <summary>CNWRules -&gt; its <c>CNWBaseItemArray*</c>. Every client body that needs a base item does the same
    /// two-step, <c>GetBaseItem(*(g_pRules + 0x20), item-&gt;baseItem)</c>, at the same offset on both builds
    /// (<c>CNWCItem::GetName</c> Linux nm @0x7ce810 / Windows rva 0x83d8a0, and three others).</summary>
    public static readonly int RulesBaseItems = Field<IntPtr>("CNWRules", "baseItems");
    /// <summary>CPanelInventory: non-zero =&gt; this panel is showing a CONTAINER rather than the plain
    /// backpack. <c>SetContainerGUIVisible</c> (Linux nm @0x68e5d0) is its only writer and
    /// <c>SetInventoryVisible</c>'s hide branch its reader. On a container OPEN it is still clear when
    /// <c>SetInventoryVisible(1, ...)</c> runs -- the container message calls the two in that order.</summary>
    public static readonly int InvContainerMode = Field<int>("CPanelInventory", "containerMode");
    /// <summary>CPanelInventory: whose inventory is on show. <c>SetInventoryVisible</c> resolves an INVALID
    /// argument to <c>GetPlayerCreatureId()</c> and stores it here before calling
    /// <see cref="SymbolMap.InvSetCharacterData"/> (Linux nm @0x68e760 <c>+0x31a0</c>, Windows rva 0x857cd0
    /// <c>+0x2f68</c>), and the notify tail sends it.</summary>
    public static readonly int InvShownOid = Field<uint>("CPanelInventory", "shownOid");
    /// <summary>CPanelInventory: 1 =&gt; this is my own inventory. <c>SetCharacterData</c> sets it for the player
    /// (or a DM) and CLEARS it for anyone else, stripping the gold label and gold button from the panel for good
    /// (Linux nm @0x68d810 <c>+0x31a4</c>, Windows rva 0x856fa0 <c>+0x2f6c</c>).</summary>
    public static readonly int InvIsMine = Field<int>("CPanelInventory", "isMine");
    /// <summary>CPanelInventory -&gt; its embedded <c>CPanelInvEquip</c> (the 14 equip slots), the subobject
    /// <c>SetInventoryVisible</c> adds and removes under the tag "Equip" (Linux nm @0x68e760 <c>this+0x368</c>,
    /// Windows rva 0x857cd0 <c>this+0x308</c>).</summary>
    public static readonly int InvEquipPanel = Field<CX.Engine.CPanelInvEquip>("CPanelInventory", "equipPanel", embedded: true);
    /// <summary>CPanelInventory: the three labels <c>SetCharacterData</c> fills -- gold, current weight, max
    /// weight. Each measured as the <c>m_pString</c> the body writes through, minus
    /// <see cref="LabelString"/> (Linux nm @0x68d810, Windows rva 0x856fa0). The gold one is also
    /// <c>RemoveFromPanel</c>'s target, an independent read of the same offset.</summary>
    public static readonly int InvGoldLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "goldLabel", embedded: true);
    /// <inheritdoc cref="InvGoldLabel"/>
    public static readonly int InvWeightLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "weightLabel", embedded: true);
    /// <inheritdoc cref="InvGoldLabel"/>
    public static readonly int InvMaxWeightLabel = Field<CX.Engine.CGuiLabel>("CPanelInventory", "maxWeightLabel", embedded: true);
    /// <summary>CPanelInventory -&gt; its embedded container grid (<c>CGuiContainer</c>, 7x5), the widget every container
    /// update lands in: <c>Update_GuiContainer</c> adds to <c>panel+0x2628</c> (Linux nm @0x79ee90) / <c>panel+0x24b0</c>
    /// (Windows rva 0x7b3790), and <c>HandleContainerPageUp</c> empties the same one (@0x68c520 / rva 0x855d00).</summary>
    public static readonly int InvContainerGrid = Field<CX.Engine.CGuiContainer>("CPanelInventory", "containerGrid", embedded: true);
    /// <summary>CPanelInventory -&gt; its embedded backpack grid (<c>CGuiInvRepository</c>, a <c>CGuiMultiPageMultiInvbox</c>), the
    /// box every backpack <c>'A'</c> adds to: <c>Update_GuiRepository</c> (Linux nm @0x79e320 <c>panel+0x1c50</c>, Windows rva
    /// 0x7b4620 <c>panel+0x1b08</c>), and <c>Update_GuiContainer</c>'s <c>'A'</c> takes a looted item out of the same one
    /// (@0x79ee90 / rva 0x7b3790). Its <c>CGuiMultiInvbox</c> base is the one <see cref="InvContainerGrid"/>'s view reads.</summary>
    public static readonly int InvBackpackGrid = Field<CX.Engine.CGuiContainer>("CPanelInventory", "backpackGrid", embedded: true);
    /// <summary>CPanelInventory: the open container's object id, INVALID when none. The container message stores it
    /// (Linux nm @0x766f30 <c>+0x2810</c>) and <c>HandleContainerClose</c> <c>UseObject</c>s it (@0x68be50 <c>+0x2810</c>,
    /// Windows rva 0x855ba0 <c>+0x268c</c>). It is the container grid's own target field (<c>grid+0x1e8</c> / <c>+0x1dc</c>),
    /// which <c>CGuiContainer::ProcessItemDrop</c> sends a drop to.</summary>
    public static readonly int InvContainerOid = Field<uint>("CPanelInventory", "containerOid");
    /// <summary>CPanelInventory: non-zero =&gt; the open container is an item (a bag), not a placeable.
    /// <c>SetContainerGUIVisible</c>'s third argument (Linux nm @0x68e5d0 <c>+0x2814</c>, Windows rva 0x8575f0
    /// <c>+0x2690</c>); the grid's radial flags and a world pickup into it key off it.</summary>
    public static readonly int InvContainerIsItem = Field<int>("CPanelInventory", "containerIsItem");
    /// <summary>CPanelInventory: the container's page count (a byte), from the open message and the server's <c>'P'</c>
    /// (<c>SetContainerPagenumVisible</c> @0x68e100 writes <c>+0x3198</c>); <c>HandleContainerPageUp</c> wraps at it (Linux
    /// nm @0x68c520 <c>+0x3198</c>, Windows rva 0x855d00 <c>+0x2f60</c>). The constructor sets 1.</summary>
    public static readonly int InvContainerPages = Field<byte>("CPanelInventory", "containerPages");
    /// <summary>CPanelInventory: the container's title label (tag <c>Title</c>): <c>SetContainerGraphic</c> writes the
    /// placeable, corpse or item name through its <see cref="LabelString"/> (Linux nm @0x68e280 <c>+0x2940</c>, Windows rva
    /// 0x8576d0 <c>+0x27c0</c>), so the label sits at <c>+0x28b0</c> / <c>+0x2730</c>, one <c>CGuiLabel</c> after the page
    /// label that <c>HandleContainerPageUp</c> writes (<c>+0x28a8</c> / <c>+0x2728</c>).</summary>
    public static readonly int InvContainerTitle = Field<CX.Engine.CGuiLabel>("CPanelInventory", "containerTitle", embedded: true);
    /// <summary>CGuiContainer (a <c>CGuiMultiPageMultiInvbox</c>): columns and rows of one page, as <c>CGuiMultiInvbox::SetSize</c>
    /// stores them (Linux nm @0x692bf0 <c>+0x1a0/+0x1a4</c>, Windows rva 0x883220 <c>+0x190/+0x194</c>).</summary>
    public static readonly int ContainerCols = Field<int>("CGuiContainer", "cols");
    /// <inheritdoc cref="ContainerCols"/>
    public static readonly int ContainerRows = Field<int>("CGuiContainer", "rows");
    /// <summary>CGuiContainer: <c>uint[cols*rows]</c>, the item id at each footprint's origin cell (<c>cols*localY + x</c>),
    /// INVALID elsewhere. <c>SetSize</c> allocates it (Linux nm @0x692bf0 <c>+0x1b0</c>, Windows rva 0x883220 <c>+0x1a0</c>),
    /// <c>AddItem</c> writes the origin and <c>RemoveItem</c> (@0x692760) resets it, so a scan of it is the page's items.</summary>
    public static readonly int ContainerItemIds = Field<IntPtr>("CGuiContainer", "itemIds");
    /// <summary>CGuiContainer: the page the grid holds; <c>CGuiMultiPageMultiInvbox::AddItem</c> drops any item off it (Linux
    /// nm @0x6943f0 <c>+0x1e0</c>, Windows rva 0x87f970 <c>+0x1d0</c>). For the container grid it is the server's page:
    /// <c>HandleContainerPageUp</c> moves it with its <c>SelectNextPage</c> (panel <c>+0x2808</c> / <c>+0x2680</c>).</summary>
    public static readonly int ContainerPage = Field<int>("CGuiContainer", "page");
    /// <summary>CPanelInvEquip: the character-name label (<c>TGuiLabel</c>) and the armour-class label, the other
    /// two <c>SetCharacterData</c> fills. Same derivation from the same two bodies: the panel-relative store
    /// minus the embedded equip panel's own offset, minus <see cref="LabelString"/>.</summary>
    public static readonly int InvEquipNameLabel = Field<CX.Engine.CGuiLabel>("CPanelInvEquip", "nameLabel", embedded: true);
    /// <inheritdoc cref="InvEquipNameLabel"/>
    public static readonly int InvEquipAcLabel = Field<CX.Engine.CGuiLabel>("CPanelInvEquip", "acLabel", embedded: true);
    /// <summary>CGuiLabel -&gt; <c>CAurString* m_pString</c>, the text object the label draws, at the same
    /// offset on both builds. A pointer to a live object, never an inline <c>CExoString</c>: the constructor
    /// @0x591fd0 fills it with <c>NewCAurString</c>, so it is non-null before the panel is ever shown, and
    /// every setter goes through the CAurString vtable (<see cref="AurStringSetTextSlot"/>).</summary>
    public static readonly int LabelString = Field<CX.Engine.CAurString>("CGuiLabel", "string");
    /// <summary>Nui::Layout::Layoutable: nonzero = the parent's child loop does NOT <c>nk_layout_space_push</c> this child, so
    /// <c>panel-&gt;row.item</c> still holds the previous child's rect (Linux nm @0x84c9f8 -&gt; push at 0x84dab5; Windows rva 0x951260 @0x1409515d6 -&gt; push at 0x1409515fa).</summary>
    public static readonly int NuiLayoutableNoPush = Field<byte>("Nui::Layout::Layoutable", "noPush");
    /// <summary>Virtual slot of <c>Render(nk_context*)</c> on every Nui::Layout class: the parent's child call (Linux
    /// @0x84ce7e <c>call *0x28(%rax)</c>, Windows @0x140951cfe <c>call *0x20(%rax)</c>), and the slot the
    /// <c>Widget</c>/<c>StatefulWidget</c> vtables fill with <see cref="SymbolMap.NuiWidgetRender"/>.</summary>
    public static readonly int NuiRenderSlot = Field("Nui::Layout::Layoutable", "vtbl.Render", Expect.VtblSlot, sig: "void(IntPtr ctx)");

    /// <summary>kiwi::impl::SolverImpl, the Cassowary solver each NUI LayoutGroup embeds at +0x5a8: its tableau, a vector of
    /// (Symbol key, Row*) pairs sorted by symbol id -- begin, then end. <c>substitute</c> @0x85b890 / rva 0x3458e0 reads both
    /// once and walks between them. Windows sits 8 further per Loki map before it: MSVC keeps each AssocVector's empty
    /// comparator as a member, so every SolverImpl field after m_cns moves (and Row's constant, +0x18 vs +0x20).</summary>
    public static readonly int KiwiRows = Field<IntPtr>("kiwi::impl::SolverImpl", "rows");
    /// <inheritdoc cref="KiwiRows"/>
    public static readonly int KiwiRowsEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "rowsEnd");
    /// <summary>m_infeasible_rows, the Symbol vector <c>substitute</c> appends every negative restricted row to (begin, end):
    /// the push at 0x85b9f0 / rva 0x3459f0 reads its end and capacity.</summary>
    public static readonly int KiwiInfeasible = Field<IntPtr>("kiwi::impl::SolverImpl", "infeasible");
    /// <inheritdoc cref="KiwiInfeasible"/>
    public static readonly int KiwiInfeasibleEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "infeasibleEnd");
    /// <summary>m_artificial, non-null only while <c>addWithArtificialVariable</c> @0x85daa0 / rva 0x340410 runs its feasibility
    /// optimize: that row sits in the tableau under the slack symbol <see cref="KiwiIdTick"/> - 1, inserted without a substitute.</summary>
    public static readonly int KiwiArtificial = Field<IntPtr>("kiwi::impl::SolverImpl", "artificial");
    /// <summary>m_id_tick, the next symbol id: 1 after the reset <c>LayoutGroup::SetChildren</c> @0x853db0 inlines, bumped
    /// once per new symbol (read and incremented at the head of <c>addWithArtificialVariable</c> on both builds).</summary>
    public static readonly int KiwiIdTick = Field<ulong>("kiwi::impl::SolverImpl", "idTick");
    /// <summary>kiwi::impl::Row's cell vector of (Symbol, double) pairs sorted by symbol id -- begin, then end.</summary>
    public static readonly int KiwiRowCells = Field<IntPtr>("kiwi::impl::Row", "cells");
    /// <inheritdoc cref="KiwiRowCells"/>
    public static readonly int KiwiRowCellsEnd = Field<IntPtr>("kiwi::impl::Row", "cellsEnd");
    /// <summary>The (Symbol, 8-byte value) pair both kiwi maps store -- a tableau entry's value is its Row*, a cell's its
    /// coefficient: the symbol id (the only sort key), its type (1 External, 2 Slack, 3 Error, 4 Dummy), the value, and
    /// the record size. <c>substitute</c>'s walks on both builds.</summary>
    public static readonly int KiwiPairId = Field<ulong>("kiwi::impl::SymbolPair", "id");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairType = Field<int>("kiwi::impl::SymbolPair", "type");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairValue = Field<IntPtr>("kiwi::impl::SymbolPair", "value");
    /// <inheritdoc cref="KiwiPairId"/>
    public static readonly int KiwiPairStride = Field("kiwi::impl::SymbolPair", "stride");
    /// <summary>m_cns, the constraints the solver accepted: a flat vector of (Constraint, Tag) records sorted by
    /// ConstraintData* -- begin, then end. <c>addConstraint</c> inserts here only after the row is in the
    /// tableau, so a constraint that threw is never in it.</summary>
    public static readonly int KiwiCns = Field<CX.Engine.kiwi__impl__CnPair>("kiwi::impl::SolverImpl", "cns");
    /// <inheritdoc cref="KiwiCns"/>
    public static readonly int KiwiCnsEnd = Field<IntPtr>("kiwi::impl::SolverImpl", "cnsEnd");
    /// <summary>kiwi::impl::Row's constant term: <c>addConstraint</c>'s near-zero test on a new row, and
    /// <c>addWithArtificialVariable</c>'s success test on the artificial objective.</summary>
    public static readonly int KiwiRowConstant = Field<double>("kiwi::impl::Row", "constant");
    /// <summary>One m_cns record: the ConstraintData*, the ids of its Tag's marker and other symbols (each Symbol is
    /// {id, type}, 16 bytes), and the record size. The marker is the slack, dummy or error symbol the constraint left in
    /// the tableau, which is how a tableau row's symbols lead back to the rules it was built from.</summary>
    public static readonly int KiwiCnData = Field<CX.Engine.kiwi__ConstraintData>("kiwi::impl::CnPair", "data");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnMarker = Field<ulong>("kiwi::impl::CnPair", "marker");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnOther = Field<ulong>("kiwi::impl::CnPair", "other");
    /// <inheritdoc cref="KiwiCnData"/>
    public static readonly int KiwiCnStride = Field("kiwi::impl::CnPair", "stride");
    /// <summary>kiwi::Constraint::ConstraintData, the same on both builds: the expression's terms (begin, end) and constant,
    /// the strength (required = 1 001 001 000) and the operator (0 &lt;=, 1 &gt;=, 2 ==). The rule is
    /// <c>sum(coefficient * variable) + constant OP 0</c>.</summary>
    public static readonly int KiwiCnTerms = Field<CX.Engine.kiwi__Term>("kiwi::ConstraintData", "terms");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnTermsEnd = Field<IntPtr>("kiwi::ConstraintData", "termsEnd");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnConstant = Field<double>("kiwi::ConstraintData", "constant");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnStrength = Field<double>("kiwi::ConstraintData", "strength");
    /// <inheritdoc cref="KiwiCnTerms"/>
    public static readonly int KiwiCnOp = Field<int>("kiwi::ConstraintData", "op");
    /// <summary>kiwi::Term: the variable (a VariableData*, which NUI never names) and its coefficient, and the record size.</summary>
    public static readonly int KiwiTermVariable = Field<IntPtr>("kiwi::Term", "variable");
    /// <inheritdoc cref="KiwiTermVariable"/>
    public static readonly int KiwiTermCoefficient = Field<double>("kiwi::Term", "coefficient");
    /// <inheritdoc cref="KiwiTermVariable"/>
    public static readonly int KiwiTermStride = Field("kiwi::Term", "stride");
    /// <summary>A layout element's children, a vector of shared_ptr (element, control block) -- begin, then end.</summary>
    public static readonly int NuiChildren = Field<IntPtr>("Nui::Layout::Layoutable", "children");
    /// <inheritdoc cref="NuiChildren"/>
    public static readonly int NuiChildrenEnd = Field<IntPtr>("Nui::Layout::Layoutable", "childrenEnd");
    /// <summary>An element's twelve kiwi variables (VariableData*): x, y, width, height, padding l/t/r/b, margin l/t/r/b --
    /// read off the ctor's three LayoutExpressions (inner = outer minus padding, outer plus margin).</summary>
    public static readonly int NuiVars = Field<IntPtr>("Nui::Layout::Layoutable", "vars", count: 12);
    /// <summary>Layoutable -&gt; the <c>DynamicValue&lt;string&gt;*</c> the <c>tooltip</c> modifier assigns (@0x869600); Windows lambda reads +0x328 +8.</summary>
    public static readonly int NuiTooltip = Field<IntPtr>("Nui::Layout::Layoutable", "tooltip");
    /// <summary>A Span's axis, read once by <c>Span::BuildConstraintDependants</c>: 1 = vertical.</summary>
    public static readonly int NuiSpanDirection = Field<int>("Nui::Layout::Span", "direction");
    /// <summary>The kiwi solver a LayoutGroup embeds; <c>SetChildren</c> resets it and adds the subtree's constraints.</summary>
    public static readonly int NuiGroupSolver = Field<CX.Engine.kiwi__impl__SolverImpl>("Nui::Layout::LayoutGroup", "solver", embedded: true);
    /// <summary>The group's own width and height, the two edit variables <c>SetChildren</c> adds at medium strength.</summary>
    public static readonly int NuiGroupEditWidth = Field<IntPtr>("Nui::Layout::LayoutGroup", "editWidth");
    /// <inheritdoc cref="NuiGroupEditWidth"/>
    public static readonly int NuiGroupEditHeight = Field<IntPtr>("Nui::Layout::LayoutGroup", "editHeight");
    /// <summary>The vector (begin) holding the one constraint <c>SetChildren</c> adds itself: the global UI-scale variable
    /// equal to the current scale. Its single term names that variable, which has no symbol of its own.</summary>
    public static readonly int NuiGroupScaleConstraints = Field<IntPtr>("Nui::Layout::LayoutGroup", "scaleConstraints");
    /// <summary>The window's element-id map (JSON id to weak_ptr&lt;Layoutable&gt;; only elements that declared an id).
    /// Linux: the libstdc++ hashtable's before-begin next, the first node of a null-terminated list. Windows: the MSVC
    /// list's head sentinel, a circular list.</summary>
    public static readonly int NuiIdsHead = Field<IntPtr>("Nui::JSON::JsonWindow", "idsHead");
    /// <summary>An id-map node: the key std::string (its address) and the weak_ptr's element pointer.</summary>
    public static readonly int NuiIdNodeKey = Field<byte>("Nui::JSON::IdNode", "key");
    /// <inheritdoc cref="NuiIdNodeKey"/>
    public static readonly int NuiIdNodeElement = Field<IntPtr>("Nui::JSON::IdNode", "element");
    /// <summary>The window's numeric id (what <c>NuiCreate</c> returns as the token).</summary>
    public static readonly int NuiWinId = Field<int>("Nui::JSON::JsonWindow", "windowId");
    /// <summary>The window's string id as the server named it: an embedded std::string (its address).</summary>
    public static readonly int NuiWinName = Field<byte>("Nui::JSON::JsonWindow", "windowName");
    /// <summary>The window JSON the server sent: an embedded nlohmann value (its address).</summary>
    public static readonly int NuiWinJson = Field<byte>("Nui::JSON::JsonWindow", "windowJson");
    /// <summary>The deque of server bind updates waiting for the next <c>Update</c> (its address; libstdc++ and MSVC shape it differently).</summary>
    public static readonly int NuiWinIngest = Field<byte>("Nui::JSON::JsonWindow", "bindIngest");
    /// <summary>A server bind update's bind name: an embedded std::string (its address).</summary>
    public static readonly int NuiBindUpdateName = Field<byte>("Nui::JSON::BindUpdate", "name");
    /// <summary>A server bind update's new value: an embedded nlohmann value (its address).</summary>
    public static readonly int NuiBindUpdateJson = Field<byte>("Nui::JSON::BindUpdate", "json");
    /// <summary>Bytes of one <c>BindUpdate</c>; a libstdc++ deque node holds eight.</summary>
    public static readonly int NuiBindUpdateStride = Field("Nui::JSON::BindUpdate", "stride");
    /// <summary>Bytes of a <c>DynamicBinding</c>, the record <c>JsonWindow::Bind</c> fills and its destructor frees.</summary>
    public static readonly int NuiDynamicBindingSize = Field("Nui::JSON::DynamicBinding", "sizeof");

    // ---- lights ----
    // The light priority terms. MEASURED on both binaries with objdump: LightManager is name-exported on
    // Windows, so `PrioritizeLights` (rva 0x7ff60) reads there exactly as it does here.

    /// <summary>The light's own priority OVERRIDE, and zero means "use the MDL node's"
    /// (<see cref="MdlLightPriority"/>). <c>LightManager::PrioritizeLights</c> @0x4c0940 is the reader;
    /// <c>Gob::SetLightPriority</c> @0x43f760 and the tileset main-light setter (which writes 4) are the
    /// writers, and <c>PartLight::PartLight</c> zeroes it.</summary>
    public static readonly int LightPriority = Field<int>("PartLight", "priority");

    /// <summary>The MDL light node's <c>lightpriority</c> field
    /// (<c>MdlNodeLight::InternalParseField</c> @0x44baf0 parses the name into it; the constructor defaults
    /// it to 5). Lower sorts first, so a builder's 1 outranks the default.</summary>
    public static readonly int MdlLightPriority = Field<int>("MdlNodeLight", "lightPriority");

    /// <summary>The BSP node inventory's <c>CExoArrayList&lt;PartLight*&gt;</c> -- the TILE lights.
    /// <c>SceneBSPAddPart</c> @0x47ca10 files a light here, and it is the only container that holds one:
    /// <c>CollapsePartTree</c> @0x462ff0 clears every tile part's child list, so no part-tree walk can reach
    /// a tile light. Sub-offsets of the record <see cref="BspDataTriMesh"/> names.</summary>
    public static readonly int BspDataLight = Field("BSPNode", "light");
    /// <inheritdoc cref="BspDataLight"/>
    public static readonly int BspDataLightCount = Field("BSPNode", "lightCount");

    // ================= behaviours =================
    // The behaviour solvers Alloy ports write engine fields through the same virtuals the engine does, so
    // every slot below was measured on BOTH images: the Windows Gob and Part vtables were located by
    // searching .rdata for the 64-bit VA of a known exported method and walking the table (Gob's is the
    // second GetPosition hit at 0xd88ac0; the first is StringGob's). Every one is -8 for the usual reason
    // (#433: MSVC gives a virtual destructor one slot, Itanium two) -- and the DELETING destructor is
    // therefore Windows slot 0 against Linux slot 8, not -8.

    /// <summary>The weak-reference back-list every <c>CAurObject</c> keeps: data, count, capacity. A solver
    /// registers a stack <c>SafePointer</c> here for the length of its solve, so an owner destroyed inside a
    /// dependency's <c>Animate</c> nulls it instead of leaving a dangling pointer. It sits immediately before
    /// the paired <see cref="GobHighlight"/>, so it is the same place on both builds.</summary>
    public static readonly int AurObjBackRefs = Field<IntPtr>("CAurObject", "backRefs");
    /// <inheritdoc cref="AurObjBackRefs"/>
    public static readonly int AurObjBackRefCount = Field<int>("CAurObject", "backRefCount");

    /// <summary>A behaviour's dependency list, the same <c>CExoArrayList</c> shape: the objects whose
    /// <c>Animate</c> the base solve drags forward before reading their part transforms.</summary>
    public static readonly int BehaviorDeps = Field<IntPtr>("CAurBehavior", "deps");
    /// <inheritdoc cref="BehaviorDeps"/>
    public static readonly int BehaviorDepCount = Field<int>("CAurBehavior", "depCount");

    /// <summary>The attach family's parent <c>CAurPart*</c> (the part whose world transform the owner is
    /// snapped to) and its "delete me rather than detach when the parent dies" flag, set by
    /// <c>Message("diewithoutparent &lt;n&gt;")</c>.</summary>
    public static readonly int AttachPart = Field<IntPtr>("CAurBehaviorAttach", "part");
    /// <inheritdoc cref="AttachPart"/>
    public static readonly int AttachDieWithoutParent = Field<int>("CAurBehaviorAttach", "dieWithoutParent");
    /// <summary>The translate / rotate subclasses' own offset <c>Vector</c> at the same place (one subclass
    /// each, so one row): a world translation rotated by the owner's orientation, or Euler degrees
    /// <c>(pitch, roll, yaw)</c> fed to <c>YawPitchRoll(z, x, y)</c>.</summary>
    public static readonly int AttachOffset = Field<System.Numerics.Vector3>("CAurBehaviorAttach", "offset");
    /// <summary><c>CAurBehaviorCrossSceneAttachWithOffset</c>'s constant world offset, added to the owner's
    /// position after the snap.</summary>
    public static readonly int CrossAttachOffset = Field<System.Numerics.Vector3>("CAurBehaviorCrossSceneAttach", "offset");
    /// <summary>CAurBehaviorCrossSceneAttach's remembered scene (<c>SafePointer&lt;CAurScene&gt;</c>) and its
    /// StopSceneChanges latch (byte): <c>Message</c> (Linux @0x58bf30, Windows rva 0x203810) reads +0x70 and writes
    /// +0x80 on both.</summary>
    public static readonly int CrossAttachKeptScene = Field<IntPtr>("CAurBehaviorCrossSceneAttach", "keptScene");
    public static readonly int CrossAttachLatch = Field<byte>("CAurBehaviorCrossSceneAttach", "latch");

    /// <summary>The subwindow solver's two dummy parts (top-left and bottom-right of the GUI viewport) and
    /// the <c>setPosition</c> flag <c>GuiAttachViewport</c> passes.</summary>
    public static readonly int SubwindowPartA = Field<IntPtr>("CAurBehaviorSubwindow", "partA");
    /// <inheritdoc cref="SubwindowPartA"/>
    public static readonly int SubwindowPartB = Field<IntPtr>("CAurBehaviorSubwindow", "partB");
    /// <inheritdoc cref="SubwindowPartA"/>
    public static readonly int SubwindowSetPos = Field<byte>("CAurBehaviorSubwindow", "setPos");

    /// <summary>The camera-follow target offset added to the target's position before the focus lerp
    /// (ctor default <c>(0, 0, 1)</c>: chest height), and the focus lag rate that drives
    /// <c>a = 1 - 0.5^(dt*rate)</c> (default 1.0). The rest of the class is already declared above
    ///.</summary>
    public static readonly int FollowOffset = Field<System.Numerics.Vector3>("CAurBehaviorCameraFollow", "offset");
    /// <inheritdoc cref="FollowOffset"/>
    public static readonly int FollowLag = Field<float>("CAurBehaviorCameraFollow", "lag");

    /// <summary><c>CAurBehaviorNavigate</c>'s four per-frame inputs, each consumed and cleared by one solve:
    /// yaw rate, pitch rate, forward speed, strafe speed (deg/s and units/s).</summary>
    public static readonly int NavigateYawRate = Field<float>("CAurBehaviorNavigate", "yawRate");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigatePitchRate = Field<float>("CAurBehaviorNavigate", "pitchRate");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigateForward = Field<float>("CAurBehaviorNavigate", "forward");
    /// <inheritdoc cref="NavigateYawRate"/>
    public static readonly int NavigateStrafe = Field<float>("CAurBehaviorNavigate", "strafe");
    /// <summary>Cleared by every <c>Navigate::Control</c> and read by nothing; kept so the port leaves the
    /// object in the same state the engine would.</summary>
    public static readonly int NavigateSpare = Field<float>("CAurBehaviorNavigate", "spare");

    /// <summary><c>CAurBehaviorCameraOnAStick</c>'s spring: last solved position, stick length (2.5), gain
    /// (20.0), pitch (70.0), fixed height (4.0), the mode int and the direction <c>Vector</c>. UNREACHABLE
    /// in this build -- nothing constructs the class -- so these are decompiler evidence only.</summary>
    public static readonly int StickLast = Field<System.Numerics.Vector3>("CAurBehaviorCameraOnAStick", "last");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickLength = Field<float>("CAurBehaviorCameraOnAStick", "length");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickGain = Field<float>("CAurBehaviorCameraOnAStick", "gain");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickPitch = Field<float>("CAurBehaviorCameraOnAStick", "pitch");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickHeight = Field<float>("CAurBehaviorCameraOnAStick", "height");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickMode = Field<int>("CAurBehaviorCameraOnAStick", "mode");
    /// <inheritdoc cref="StickLast"/>
    public static readonly int StickDir = Field<System.Numerics.Vector3>("CAurBehaviorCameraOnAStick", "dir");

    /// <summary><c>CAurObject::SetBehavior(CAurBehavior*)</c> -- passing null is how a solver whose parent
    /// died detaches; the setter DELETES the behaviour, so nothing may touch <c>this</c> afterwards.</summary>
    public static readonly int GobSetBehaviorSlot = Field("Gob", "vtbl.SetBehavior", Expect.VtblSlot, sig: "void(IntPtr behavior)");
    /// <summary><c>CAurObject::GetBehavior(int type)</c> -- with -1, "the behaviour you have"; the
    /// identity test every solve makes before and after touching the owner.</summary>
    public static readonly int GobGetBehaviorSlot = Field("Gob", "vtbl.GetBehavior", Expect.VtblSlot, sig: "IntPtr(int type)");
    /// <summary>The DELETING destructor: <c>Gob::~Gob</c> + <c>operator delete</c>. An attach behaviour with
    /// <c>diewithoutparent</c> set deletes its owner through it. Itanium's two destructor slots are
    /// complete-then-deleting; MSVC has one -- so this row is the one slot in the table that is NOT -8, and
    /// MSVC's takes a flags argument Itanium's does not, which is why no signature is declared.</summary>
    public static readonly int GobDeleteSlot = Field("Gob", "vtbl.Delete", Expect.VtblSlot);
    /// <summary><c>CAurObject::GetName()</c> -- <c>Gob+0x24</c>, the inline name buffer. The subwindow
    /// solver keys on four exact panel names.</summary>
    public static readonly int GobGetNameSlot = Field("Gob", "vtbl.GetName", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary><c>CAurObject::GetScene()</c> and <c>CAurObject::AsCamera()</c> -- the subwindow solver's
    /// two lookups: the panel's scene, and the owner's <c>Camera</c> base (which sits 8 bytes before the
    /// <c>CAurObject</c> a <c>CAurCamera</c> hands out).</summary>
    public static readonly int GobGetSceneSlot = Field("Gob", "vtbl.GetScene", Expect.VtblSlot, sig: "IntPtr()");
    /// <inheritdoc cref="GobGetSceneSlot"/>
    public static readonly int GobAsCameraSlot = Field("Gob", "vtbl.AsCamera", Expect.VtblSlot, sig: "IntPtr()");
    /// <summary><c>CAurObject::SetPosition(Vector)</c> and <c>SetOrientation(Quaternion)</c> -- the only
    /// writers the solvers' offsets go through. No signature is declared: both RETURN their argument by
    /// value, which SysV answers in registers and Win64 through a caller buffer, so
    /// <c>CX.Alloy.BehaviorCall</c> carries both argument lists.</summary>
    public static readonly int GobSetPositionSlot = Field("Gob", "vtbl.SetPosition", Expect.VtblSlot);
    /// <inheritdoc cref="GobSetPositionSlot"/>
    public static readonly int GobSetOrientationSlot = Field("Gob", "vtbl.SetOrientation", Expect.VtblSlot);
}
