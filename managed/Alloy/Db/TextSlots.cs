using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;
using CX.Engine;

namespace CX.Alloy.Db;

/// <summary>What a <c>CAurString</c> slot's argument list needs from the facade's thunk, where the two ABIs do not
/// agree. Everything else passes the same way on both OSes.</summary>
public enum SlotAbi : byte
{
    /// <summary>Integers and/or pointers only: the same registers on both OSes.</summary>
    Plain,
    /// <summary>Carries at least one <c>float</c>. Win64 assigns registers by ARGUMENT INDEX with <c>this</c> as
    /// index 0, so a lone float lands in <c>xmm1</c> and <c>(int, float)</c> puts the float in <c>xmm2</c>;
    /// SysV walks a separate SSE sequence from <c>xmm0</c>. Let the runtime apply the platform convention
    /// (<c>CallConvMemberFunction</c> on Windows); four of these slots also spill a float to the Win64 stack.</summary>
    FloatArgs,
    /// <summary>A <c>Vector</c> (3 floats, 12 bytes) by value: SysV passes it in <c>xmm0</c>(x,y)+<c>xmm1</c>(z),
    /// Win64 passes a hidden POINTER to a caller copy.</summary>
    VectorByValue,
    /// <summary>Returns a class by value -- here <c>Vector SetPosition(Vector)</c>. Win64 takes the
    /// <c>sret</c> buffer as argument 1, <c>(this, sret, &amp;arg)</c>; SysV returns the three floats in
    /// <c>xmm0</c>/<c>xmm1</c> with NO <c>sret</c>. One order for both hands the engine a stack frame as
    /// <c>this</c>.</summary>
    SretReturn,
}

/// <summary>One row of the <c>CAurString</c> slot table. <paramref name="Linux"/> and <paramref name="Windows"/>
/// are vtable INDICES: GCC emits the <c>D1</c>/<c>D0</c> destructor pair where MSVC emits one slot, so every
/// Windows index past 0 is one lower. <c>tools/seam/aurora-seam-vtables.tsv</c> is the authoritative
/// measurement; the self-check asserts the shape the two must share, not the file's contents.</summary>
public readonly record struct TextSlot(int Linux, int Windows, string Name, string Signature, SlotAbi Abi);

/// <summary>The <c>CAurString</c> interface, answered over <see cref="SceneDb"/> text records.
/// <para>Each method here is one vtable slot's semantic body, written against a <see cref="DbId"/> so it can
/// be tested without an engine; <see cref="Seam.Facade"/> owns the native shell, vtable and per-OS thunks and
/// binds them by <see cref="Slots"/>. A dead handle returns the engine's "nothing happened" value.</para></summary>
public static partial class TextSlots
{
    /// <summary>The 52 slots, in vtable order. The ABI column names only the slots where the two OSes differ;
    /// everything else is <see cref="SlotAbi.Plain"/>.</summary>
    public static readonly TextSlot[] Slots =
    [
        new(0,  0,  "~CAurString",              "void()",                           SlotAbi.Plain),
        new(1,  0,  "~CAurString (deleting)",   "void()",                           SlotAbi.Plain),
        new(2,  1,  "AttachToObject",           "void*(CAurObject*, char const*, int)", SlotAbi.Plain),
        new(3,  2,  "AttachToPart",             "void*(CAurObject*, CAurPart*, int)",   SlotAbi.Plain),
        new(4,  3,  "AttachCornersToObjects",   "int(CAurObject*, char const*, char const*)", SlotAbi.Plain),
        new(5,  4,  "SetPositionOffset",        "int(Vector)",                      SlotAbi.VectorByValue),
        new(6,  5,  "SetPosition",              "Vector(Vector)",                   SlotAbi.SretReturn),
        new(7,  6,  "AttachModel",              "void(CAurObject*, int)",           SlotAbi.Plain),
        new(8,  7,  "GetAttachedModel",         "CAurObject*(int)",                 SlotAbi.Plain),
        new(9,  8,  "GetExternalData",          "void*()",                          SlotAbi.Plain),
        new(10, 9,  "SetExternalData",          "void(void*)",                      SlotAbi.Plain),
        new(11, 10, "SetFont",                  "int(char const*)",                 SlotAbi.Plain),
        new(12, 11, "SetFontPassword",          "int(char const*, char)",           SlotAbi.Plain),
        new(13, 12, "GetCurrentFontIndex",      "int()",                            SlotAbi.Plain),
        new(14, 13, "SetAlignment",             "void(float)",                      SlotAbi.FloatArgs),
        new(15, 14, "SetAlignmentVertical",     "void(float)",                      SlotAbi.FloatArgs),
        new(16, 15, "SetColorDefault",          "void(float, float, float, float)", SlotAbi.FloatArgs),
        new(17, 16, "SetIndexedColor",          "void(int, float, float, float, float)", SlotAbi.FloatArgs),
        new(18, 17, "ScrollToLine",             "int(int)",                         SlotAbi.Plain),
        new(19, 18, "ScrollToPixel",            "int(int)",                         SlotAbi.Plain),
        new(20, 19, "GetLinePixelWidth",        "float(int)",                       SlotAbi.Plain),
        new(21, 20, "GetLinePixelHeight",       "float(int)",                       SlotAbi.Plain),
        new(22, 21, "SetLinePixelHeight",       "void(int, float)",                 SlotAbi.FloatArgs),
        new(23, 22, "RefreshDisplay",           "void()",                           SlotAbi.Plain),
        new(24, 23, "GetDisplayInfo",           "CAurStringDisplayInfo*()",         SlotAbi.Plain),
        new(25, 24, "SetMaxWidth",              "void(float)",                      SlotAbi.FloatArgs),
        new(26, 25, "SetMaxHeight",             "void(float)",                      SlotAbi.FloatArgs),
        new(27, 26, "SetText",                  "bool(char const*)",                SlotAbi.Plain),
        new(28, 27, "AddText",                  "bool(char const*)",                SlotAbi.Plain),
        new(29, 28, "GetTextLine",              "char const*(int)",                 SlotAbi.Plain),
        new(30, 29, "GetTextLineCount",         "int()",                            SlotAbi.Plain),
        new(31, 30, "GetLineState",             "int(int)",                         SlotAbi.Plain),
        new(32, 31, "SetLineState",             "void(int, int)",                   SlotAbi.Plain),
        new(33, 32, "SetTextWrapping",          "void(bool)",                       SlotAbi.Plain),
        new(34, 33, "SetLineColor",             "void(int, int)",                   SlotAbi.Plain),
        new(35, 34, "GetLineColor",             "int(int)",                         SlotAbi.Plain),
        new(36, 35, "GetNumColors",             "int()",                            SlotAbi.Plain),
        new(37, 36, "AddLine",                  "int(char const*)",                 SlotAbi.Plain),
        new(38, 37, "InsertLine",               "void(char const*, int)",           SlotAbi.Plain),
        new(39, 38, "DeleteLine",               "void(int)",                        SlotAbi.Plain),
        new(40, 39, "SetLine",                  "void(int, char const*)",           SlotAbi.Plain),
        new(41, 40, "InsertTextAtCaret",        "bool(char const*)",                SlotAbi.Plain),
        new(42, 41, "DeleteTextAtCaret",        "bool(int)",                        SlotAbi.Plain),
        new(43, 42, "GetCaretLinePos",          "int()",                            SlotAbi.Plain),
        new(44, 43, "SetCaretLinePos",          "bool(int)",                        SlotAbi.Plain),
        new(45, 44, "GetCaretColumnPos",        "int()",                            SlotAbi.Plain),
        new(46, 45, "SetCaretColumnPos",        "bool(int)",                        SlotAbi.Plain),
        new(47, 46, "MoveCaretTo",              "int(Vector)",                      SlotAbi.VectorByValue),
        new(48, 47, "SetCaretVisible",          "void(bool)",                       SlotAbi.Plain),
        new(49, 48, "SuggestDeleteCurrentFont", "int()",                            SlotAbi.Plain),
        new(50, 49, "GetStringsArray",          "void**()",                         SlotAbi.Plain),
        new(51, 50, "SetColorSelected",         "void(float, float, float, float)",  SlotAbi.FloatArgs),
    ];

    /// <summary>The engine's "no font" font name, returned by <c>GetFontName</c> when there is none.</summary>
    public const string NoFont = "";

    // ---- 0, 1: destruction ----

    /// <summary>Slot 0, <c>StringGob::~StringGob</c> @0x494be0 -- the COMPLETE-OBJECT destructor. It runs the
    /// destructor body and does NOT free the object: it resets both vptrs, drops the text, releases the font,
    /// frees the colour table and the line array, and runs <c>~Gob</c>. The storage stays alive, so this slot
    /// must not retire the facade -- the game legitimately re-attaches a destructed label.</summary>
    public static void DestructBody(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.DeleteText(d);
        d.Font = null;
        d.ColourCount = 1;
        if (d.Colours.Length >= 4) { d.Colours[0] = d.Colours[1] = d.Colours[2] = d.Colours[3] = 1f; }
        d.Caret = default;
        d.CaretVisible = false;
        d.SelAnchor = -1;
        SceneDb.TextOffsetBehavior[id.Index] = 0;
        SceneDb.TextPosOffset[id.Index] = default;
        TextObjects.MarkText(id, d);
        Destructed++;
    }

    /// <summary>Slot 1, <c>StringGob::~StringGob</c> @0x494c70 -- the DELETING destructor: the body above, then
    /// <c>operator delete(this, 0x558)</c>. Only this slot frees the record and lets the facade retire.
    /// <para>The shell's two heap blocks (callback list, attach list) are freed here, because a text object
    /// dies through its RECORD; the shell is found through the handle, so that must precede the record free.</para></summary>
    public static void Destruct(DbId id)
    {
        DestructBody(id);
        Seam.ObjectSlots.FreeShellLists(Seam.Facade.ObjectShell(id));
        TextObjects.Destroy(id);
    }

    /// <summary>Complete-object destructor calls -- the ones that must NOT free. Counted because a build that
    /// got the pair the wrong way round looks healthy until a panel reopens.</summary>
    public static long Destructed;

    // ---- 2..10: attachment, position, external data (the Gob half of the interface) ----

    /// <summary>Slot 2. Attachment rides on the object columns, so a text object attaches exactly as a model does.
    /// <paramref name="mode"/> is the attach VARIANT (<see cref="SceneDb.ObjAttachMode"/>): the engine builds a
    /// fresh <c>CAurBehaviorAttach*</c> here, so any position offset set on the previous one is gone.</summary>
    public static nint AttachToObject(DbId id, DbId to, int part, int mode)
    {
        if (!SceneDb.Objects.Valid(id)) return 0;
        SceneDb.SetAttachTo(id.Index, to);
        SceneDb.SetAttachPart(id.Index, part);
        SceneDb.ObjAttachMode[id.Index] = (byte)((uint)mode <= 4 ? mode : SceneDb.AttachNone);
        SceneDb.TextOffsetBehavior[id.Index] = 0;
        SceneDb.TextPosOffset[id.Index] = default;
        SceneDb.Touch(id, Dirty.Xform);
        return SceneDb.ObjFacade[id.Index];
    }

    /// <summary>Slot 3.</summary>
    public static nint AttachToPart(DbId id, DbId to, int part, int mode) => AttachToObject(id, to, part, mode);

    /// <summary>Slot 4, <c>StringGob::AttachCornersToObjects</c> @0x495770. The two named dummies on
    /// <paramref name="to"/> are the box's corners: the engine takes the world delta between them, rotates it
    /// by <c>objectOrientation * conj(corner1Orientation)</c>, and writes the absolute components as
    /// <c>maxWidth</c>/<c>maxHeight</c> before re-laying out -- ignoring them leaves the label unbounded.</summary>
    public static int AttachCornersToObjects(DbId id, DbId to, string corner1, string corner2)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return 0;
        if (CornerBox(to, corner1, corner2, out float w, out float h)) { d.MaxWidth = w; d.MaxHeight = h; }
        else if (_saidCorners++ == 0)
            _log?.Always($"TextSlots: the corner dummies \"{corner1}\"/\"{corner2}\" did not resolve on the attach "
                       + "target, so the label keeps its CURRENT box (totals on /alloy)");
        // StringGob::AttachCornersToObjects @0x495770 ends in Gob::AttachToObject(this, to, corner1, 0): the
        // first corner is the label's ANCHOR, not the target's root, and the variant is the plain attach.
        int anchor = SceneDb.Objects.Valid(to) ? CX.Alloy.Seam.ObjectSlots.PartByName(to, corner1) : -1;
        AttachToObject(id, to, anchor >= 0 ? anchor : 0, 0);
        if (d.Font != null) TextObjects.Relayout(d);
        TextObjects.MarkText(id, d);
        return 1;
    }

    private static int _saidCorners;
    private static CX.Framework.ExtenderLog? _log;

    /// <summary>Inject the log so a refusal is loud.</summary>
    public static void Install(CX.Framework.ExtenderLog log) => _log = log;

    /// <summary>Corner dummies that did not resolve, so a label kept its old box -- counted for <c>/alloy</c>.</summary>
    public static long CornersMissed;

    /// <summary>The engine's corner arithmetic over the database's part tree.</summary>
    private static bool CornerBox(DbId obj, string corner1, string corner2, out float width, out float height)
    {
        width = height = 0f;
        if (!SceneDb.Objects.Valid(obj)) { CornersMissed++; return false; }
        // DbAnim.PartByName, not `ObjPartFirst + model.Find(name)`: a part range is instanced in pre-order,
        // but a reference sub-tree puts ANOTHER model's nodes in it, so a node index is not the part offset.
        // The arithmetic that assumed it read a part that was live, in range and the wrong one.
        int p1 = DbAnim.PartByName(obj, corner1), p2 = DbAnim.PartByName(obj, corner2);
        if (p1 < 0 || p2 < 0) { CornersMissed++; return false; }
        // Through the on-demand getter, not the raw column: the engine reads the dummies with Part::GetWorld,
        // which composes up the matrix stack on the spot. A panel sets its label's box in the same breath as
        // it builds the control, before any frame has composed it, and two uncomposed parts both read the
        // object's origin -- a box of no size for every corner-attached label on that panel.
        Matrix4x4 a = DbAnim.PartWorld(p1), b = DbAnim.PartWorld(p2);
        var delta = new Vector3(b.M41 - a.M41, b.M42 - a.M42, b.M43 - a.M43);
        Quaternion q = SceneDb.ObjRot[obj.Index] * Quaternion.Conjugate(Quaternion.CreateFromRotationMatrix(a));
        Vector3 r = Vector3.Transform(delta, q);
        width = MathF.Abs(r.X);
        height = MathF.Abs(r.Y);
        // Two DISTINCT dummies cannot bound a zero box, so a zero is a resolution that went wrong rather than
        // a label the panel meant to leave unbounded -- and it is invisible afterwards, because an unbounded
        // box is also what a label that never asked for one has. The value still stands (the engine writes
        // whatever its own arithmetic gives); this only refuses to let it pass unsaid.
        if (width == 0f && height == 0f && _saidFlat++ == 0)
            _log?.Always($"TextSlots: the corner pair \"{corner1}\"/\"{corner2}\" on "
                       + $"\"{SceneDb.ObjModel[obj.Index]?.Name ?? "-"}\" (parts {p1}/{p2}) bounds a box of "
                       + "NO SIZE, so the label stays unbounded and its alignment centres it on its own origin");
        return true;
    }

    private static int _saidFlat;

    /// <summary>Slot 5, <c>StringGob::SetPositionOffset</c> @0x492420. It writes NOTHING unless
    /// <c>GetBehavior(-1)</c> is a <c>CAurBehaviorAttachTranslate</c> (type <c>0x3ea</c>) -- the offset lives
    /// on that behaviour, not on the object -- so it goes to its own column keyed on that behaviour and NOT
    /// into <see cref="SceneDb.ObjPos"/>, which the attach placement overwrites every compose.</summary>
    public static int SetPositionOffset(DbId id, Vector3 v)
    {
        if (!SceneDb.Objects.Valid(id)) return 0;
        nint behavior = Seam.BehaviorSeam.Installed(id);
        // The behaviour the TEST asks about is normally one DB mode never built: the game's own attach, made
        // by Gob::AttachToObject's mode argument. That variant is a column, so a mode-1 attach edge answers
        // the test exactly as the CAurBehaviorAttachTranslate the engine would have installed.
        bool translateAttach = SceneDb.ObjAttachMode[id.Index] == SceneDb.AttachTranslate
                            && SceneDb.Objects.Valid(SceneDb.ObjAttachTo[id.Index]);
        if (behavior != 0 && Seam.BehaviorSeam.TypeOf(behavior) == Seam.BehaviorSeam.TypeAttachTranslate)
            SceneDb.TextOffsetBehavior[id.Index] = behavior;
        else if (translateAttach)
            SceneDb.TextOffsetBehavior[id.Index] = 0;
        else { _offsetRefused++; return 0; }
        SceneDb.TextPosOffset[id.Index] = v;
        SceneDb.Touch(id, Dirty.Xform);
        return 1;
    }

    /// <summary>Offsets the engine would not have applied either, because the object's behaviour is not an
    /// <c>AttachTranslate</c> -- a count, not a refusal: vanilla answers 0 here too.</summary>
    public static long OffsetRefused => _offsetRefused;
    private static long _offsetRefused;

    /// <summary>Slot 6, the one by-value return in the interface: the engine returns the position it set.</summary>
    public static Vector3 SetPosition(DbId id, Vector3 v)
    {
        if (!SceneDb.Objects.Valid(id)) return v;
        SceneDb.ObjPos[id.Index] = v;
        SceneDb.Touch(id, Dirty.Xform);
        return v;
    }

    /// <summary>Slot 7, <c>StringGob::AttachModel</c> @0x5924a0: the model rides line <paramref name="line"/>
    /// (<c>CStringItem+0x28</c>) and <c>StringGob::Render</c> @0x5933a0 draws it at that line's pen, under the
    /// glyphs -- <c>CGuiListBox::SetLineSelection</c>'s highlight. The engine indexes unchecked; this refuses.</summary>
    public static void AttachModel(DbId id, DbId model, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || (uint)line >= (uint)d.LineCount) return;
        d.Lines[line].Model = model;
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 8.</summary>
    public static DbId GetAttachedModel(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d == null || (uint)line >= (uint)d.LineCount ? default : d.Lines[line].Model;
    }

    /// <summary>Slots 9/10: the game's opaque cookie, straight through.</summary>
    public static nint GetExternalData(DbId id) => SceneDb.Objects.Valid(id) ? _external[id.Index] : 0;
    public static void SetExternalData(DbId id, nint data) { if (SceneDb.Objects.Valid(id)) _external[id.Index] = data; }
    private static readonly nint[] _external = new nint[SceneDb.MaxObjects];

    /// <summary>Drop a record's external cookie. The engine's ctor zeroes it, so a recycled index must not hand
    /// the previous object's pointer to the next one's <c>GetExternalData</c>.</summary>
    internal static void ClearExternal(int index) { if ((uint)index < (uint)_external.Length) _external[index] = 0; }

    // ---- 11..13: the font ----

    /// <summary>Slot 11. Returns the font's index, or -1 when the name does not resolve -- and on -1 the document
    /// keeps no font, so it measures and draws nothing, exactly as the engine does.</summary>
    public static int SetFont(DbId id, string name)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return -1;
        TextFont? f = TextFontBake.Resolve(name);
        if (f == null) { TextObjects.SetFont(d, null); TextObjects.MarkText(id, d); return -1; }
        TextObjects.SetFont(d, f);
        TextObjects.MarkText(id, d);
        return TextFonts.IndexOf(f);
    }

    /// <summary>Slot 12: the same font with every glyph but index 0 replaced by <paramref name="mask"/>.</summary>
    public static int SetFontPassword(DbId id, string name, byte mask)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return -1;
        _ = TextFontBake.Resolve(name);
        TextFont? f = TextFonts.Password(name, mask);
        if (f == null) return -1;
        TextObjects.SetFont(d, f);
        TextObjects.MarkText(id, d);
        return TextFonts.IndexOf(f);
    }

    /// <summary>Slot 13.</summary>
    public static int GetCurrentFontIndex(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        return d?.Font == null ? -1 : TextFonts.IndexOf(d.Font);
    }

    // ---- 14..17, 51: alignment and colour ----

    /// <summary>Slots 14/15. Pure setters in the engine: a caller that wants the new alignment on screen calls
    /// <c>RefreshDisplay</c>. CX marks the quads stale anyway, because alignment moves glyphs and a stale list is
    /// not a layout.</summary>
    public static void SetAlignment(DbId id, float h)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.AlignH = h; TextObjects.MarkText(id, d); }

    public static void SetAlignmentVertical(DbId id, float v)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.AlignV = v; TextObjects.MarkText(id, d); }

    /// <summary>Slot 16 = <c>SetIndexedColor(0, …)</c>.</summary>
    public static void SetColorDefault(DbId id, float r, float g, float b, float a) => SetIndexedColor(id, 0, r, g, b, a);

    /// <summary>Slot 51. The engine's body is a tail-call to <c>SetIndexedColor(1, …)</c> with the floats passed
    /// straight through -- invisible in the decompile, read off the disassembly at 0x595cb0.</summary>
    public static void SetColorSelected(DbId id, float r, float g, float b, float a) => SetIndexedColor(id, 1, r, g, b, a);

    /// <summary>Slot 17.</summary>
    public static void SetIndexedColor(DbId id, int index, float r, float g, float b, float a)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.SetIndexedColour(d, index, r, g, b, a);
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 36.</summary>
    public static int GetNumColors(DbId id) => TextObjects.Of(id)?.ColourCount ?? 0;

    // ---- 18..19: scrolling ----

    public static int ScrollToLine(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return 0;
        int r = TextObjects.ScrollToLine(d, line);
        TextObjects.MarkText(id, d);
        return r;
    }

    public static int ScrollToPixel(DbId id, int px)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return -1;
        int r = TextObjects.ScrollToPixel(d, px);
        TextObjects.MarkText(id, d);
        return r;
    }

    // ---- 20..26: measurement and the box ----

    /// <summary>Slot 20. DESIGN pixels: the engine divides by a hard 0.01, not by the GUI-scaled grid.</summary>
    public static float GetLinePixelWidth(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d != null && (uint)line < (uint)d.LineCount ? d.Lines[line].Width / TextObjects.UnitPerPixel : 0f;
    }

    public static float GetLinePixelHeight(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d != null && (uint)line < (uint)d.LineCount ? d.Lines[line].Height / TextObjects.UnitPerPixel : 0f;
    }

    /// <summary>Slot 22. Writes the same cached field the measure grows, so the next layout clobbers it -- an
    /// engine behaviour, kept.</summary>
    public static void SetLinePixelHeight(DbId id, int line, float px)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || (uint)line >= (uint)d.LineCount) return;
        d.Lines[line].Height = px * TextObjects.UnitPerPixel;   // a bare store: the engine recomputes nothing
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 23 = <c>RecalcDisplayData</c>: the one explicit relayout the game layer has.</summary>
    public static void RefreshDisplay(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.Relayout(d);
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 24. The engine hands out a pointer INTO the object (<c>this+0x528</c>) and the game reads
    /// the block through it up to <c>+0x28</c> -- <c>CGuiTextBubble::SetText</c> @0x688790 divides by the line
    /// height at <c>StringGob+0x550</c> -- and also WRITES the box through it
    /// (<see cref="TextDoc.InfoBuf"/>), so the block is the document's own. Returns it, or 0 for no document.</summary>
    public static nint GetDisplayInfo(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        return d == null ? 0 : DisplayInfo(d);
    }

    /// <inheritdoc cref="GetDisplayInfo(DbId)"/>
    public static unsafe nint DisplayInfo(TextDoc d)
    {
        if (d.InfoBuf == 0) d.InfoBuf = (nint)NativeMemory.AllocZeroed(DisplayInfoSize);
        d.InfoOut = true;
        FillDisplayInfo(d);
        return d.InfoBuf;
    }

    /// <summary>Rewrite the handed-out block from the document. The engine's block IS the object's fields:
    /// <c>CGuiEdit::CGuiEdit</c> @0x692460 caches the pointer once and <c>CGuiEditScroll::UpdateScroll</c>
    /// @0x691690 reads the scroll budget through it right after a <c>ScrollToLine</c>, so a snapshot is a dead
    /// scroll range (a minimum-length thumb). Run from every change gate.</summary>
    internal static unsafe void FillDisplayInfo(TextDoc d)
    {
        if (!d.InfoOut) return;
        byte* p = (byte*)d.InfoBuf;
        new Span<byte>(p, DisplayInfoSize).Clear();
        var info = (TextDisplayInfo*)p;
        info->TopLine = d.TopLine; info->VisibleLines = d.VisibleLines; info->ScrollBudget = d.ScrollBudget;
        info->Width = d.InfoW = d.MaxWidth; info->Height = d.InfoH = d.MaxHeight;
        info->TotalHeight = d.TotalHeight; info->VisibleHeight = d.VisibleHeight;
        info->SubLineScroll = d.SubLineScroll; info->PixelScroll = d.PixelScroll ? 1 : 0;
        if (d.Font != null) info->FontHeight = d.Font.FontHeight / TextObjects.UnitPerPixel;
    }

    /// <summary>Adopt a box the game wrote through the handed-out display block since the last slot call, the
    /// engine's <c>+0x534</c>/<c>+0x538</c> as a field store: no layout, the next one uses it. Runs at the head
    /// of every slot entry, as <see cref="SyncItemOrder"/> does.</summary>
    public static void SyncDisplayInfo(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d != null && ApplyDisplayInfo(d)) TextObjects.MarkText(id, d);
    }

    /// <inheritdoc cref="SyncDisplayInfo(DbId)"/>
    /// <returns>Whether the box changed.</returns>
    public static unsafe bool ApplyDisplayInfo(TextDoc d)
    {
        if (!d.InfoOut) return false;
        byte* p = (byte*)d.InfoBuf;
        float w = ((TextDisplayInfo*)p)->Width, h = ((TextDisplayInfo*)p)->Height;
        if (w == d.InfoW && h == d.InfoH) return false;
        if (w != d.InfoW) d.MaxWidth = d.InfoW = w;
        if (h != d.InfoH) d.MaxHeight = d.InfoH = h;
        return true;
    }

    /// <summary><c>StringGob+0x528</c> .. <c>+0x554</c>: the display block through the line height.</summary>
    public const int DisplayInfoSize = 0x2c;  // not-an-offset: the block size through +0x28, the same on both builds (Windows SetText @rva 0x1fd690 divides by [info+0x28] too)

    /// <summary>Slots 25/26: pure setters, as in the engine. The quads go stale because the width decides the
    /// wrap and the alignment, but NO layout runs until the caller asks for one.</summary>
    public static void SetMaxWidth(DbId id, float w)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.MaxWidth = w; TextObjects.MarkText(id, d); }

    public static void SetMaxHeight(DbId id, float h)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.MaxHeight = h; TextObjects.MarkText(id, d); }

    // ---- 27..30, 37..40: the text ----

    public static bool SetText(DbId id, ReadOnlySpan<byte> text)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return false;
        bool r = TextObjects.SetText(d, text);
        TextObjects.MarkText(id, d);
        return r;
    }

    public static bool AddText(DbId id, ReadOnlySpan<byte> text)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return false;
        bool r = TextObjects.AddText(d, text);
        TextObjects.MarkText(id, d);
        return r;
    }

    public static int AddLine(DbId id, ReadOnlySpan<byte> text)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return 0;
        bool r = TextObjects.AddLine(d, text);
        TextObjects.MarkText(id, d);
        return r ? 1 : 0;
    }

    public static void InsertLine(DbId id, ReadOnlySpan<byte> text, int at)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.InsertLine(d, text, at);
        TextObjects.MarkText(id, d);
    }

    public static void DeleteLine(DbId id, int at)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.DeleteLine(d, at);
        TextObjects.MarkText(id, d);
    }

    public static void SetLine(DbId id, int at, ReadOnlySpan<byte> text)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return;
        TextObjects.SetLine(d, at, text);
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 29. The engine returns the line's own buffer; the facade hands back the shell-owned copy it
    /// keeps for exactly this, because the game reads it as a <c>char const*</c> and may hold it across calls.</summary>
    public static ReadOnlySpan<byte> GetTextLine(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d != null && (uint)line < (uint)d.LineCount ? d.Lines[line].Span : default;
    }

    public static int GetTextLineCount(DbId id) => TextObjects.Of(id)?.LineCount ?? 0;

    // ---- 31..35: per-line state and colour ----

    public static int GetLineState(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d != null && (uint)line < (uint)d.LineCount ? d.Lines[line].State : 0;
    }

    public static void SetLineState(DbId id, int line, int state)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || (uint)line >= (uint)d.LineCount) return;
        d.Lines[line].State = (short)state;
    }

    public static int GetLineColor(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        return d != null && (uint)line < (uint)d.LineCount ? d.Lines[line].Colour : 0;
    }

    public static void SetLineColor(DbId id, int line, int colour)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || (uint)line >= (uint)d.LineCount) return;
        d.Lines[line].Colour = (short)colour;
        TextObjects.MarkText(id, d);
    }

    /// <summary>Slot 33: a pure setter in the engine, and the wrap it changes only takes effect on the next edit
    /// or <c>RefreshDisplay</c>.</summary>
    public static void SetTextWrapping(DbId id, bool on)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.Wrap = on; }

    // ---- 41..48: the caret ----

    /// <summary>Slot 41, <c>StringGob::InsertTextAtCaret</c> @0x495250. A caret past the last line APPENDS a
    /// new line (while the ceiling allows) rather than refusing; with wrapping OFF the line is truncated to
    /// what fits after the wrap pass; then the caret moves by the inserted length.</summary>
    public static bool InsertTextAtCaret(DbId id, ReadOnlySpan<byte> text)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || d.Caret.Line < 0) return false;
        if (d.Caret.Line < d.LineCount)
        {
            TextLine l = d.Lines[d.Caret.Line];
            TextObjects.InsertAt(l, text, d.Caret.Column);
            TextObjects.WrapLines(d, d.Caret.Line);
            if (!d.Wrap)
            {
                TextLine cur = d.Lines[d.Caret.Line];
                if (cur.VisibleLength < cur.Length) cur.Length = cur.VisibleLength;
            }
        }
        else
        {
            if (d.Caret.Line >= d.MaxLines) return false;
            TextLine l = TextObjects.NewLine(d);
            d.Caret.Line = d.LineCount - 1;
            l.Set(text, 0, text.Length);
            TextObjects.WrapLines(d, d.Caret.Line);
        }
        TextObjects.ShowAsManyLines(d);
        SetCaretColumnPos(id, text.Length + GetCaretColumnPos(id));
        TextObjects.MarkText(id, d);
        return true;
    }

    /// <summary>Slot 42, <c>StringGob::DeleteTextAtCaret</c> @0x495180. FORWARD delete: it removes
    /// <paramref name="count"/> bytes AT the caret's column, clamped to the line's end, and leaves the column
    /// where it is. Taking the whole line deletes the line instead.</summary>
    public static bool DeleteTextAtCaret(DbId id, int count)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return false;
        int line = d.Caret.Line;
        if (line < 0 || line >= d.LineCount) return false;
        TextLine l = d.Lines[line];
        int len = l.Length, col = d.Caret.Column;
        if (len < col + count) count = len - col;
        if (count == len) TextObjects.DeleteLine(d, line);
        else
        {
            TextObjects.DeleteChars(l, col, count);
            TextObjects.UnWrapLines(d, d.Caret.Line);
        }
        SetCaretColumnPos(id, GetCaretColumnPos(id));
        TextObjects.MarkText(id, d);
        return true;
    }

    public static int GetCaretLinePos(DbId id) => TextObjects.Of(id)?.Caret.Line ?? 0;

    /// <summary>Slot 44, <c>StringGob::SetCaretLinePos</c> @0x492fb0. <paramref name="line"/> may be ONE past
    /// the last, which is how a text box opens a new line -- but only when the previous line ends in a newline
    /// and the ceiling allows. Otherwise the caret carries its desired x across, and the view scrolls either
    /// way to keep the line visible.</summary>
    public static bool SetCaretLinePos(DbId id, int line)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || line < 0 || line > d.LineCount) return false;
        if (line == d.LineCount)
        {
            if (line > 0)
            {
                TextLine prev = d.Lines[line - 1];
                if (prev.Length < 1 || prev.Bytes[prev.Length - 1] != (byte)'\n') return false;
                if (line >= d.MaxLines) return false;
            }
            d.Caret.Line = line;
            d.Caret.Column = 0;
            d.Caret.X = 0f;
            TextObjects.MarkText(id, d);
            return true;
        }
        int was = d.Caret.Line;
        float wantX = (uint)was < (uint)d.LineCount
            ? (d.MaxWidth - d.Lines[was].Width) * d.AlignH
            : 0f;
        float newW = d.Lines[line].Width;
        d.Caret.Line = line;
        d.Caret.Column = -1;
        TextObjects.UpdateCaret(d, line, wantX + d.Caret.X - (d.MaxWidth - newW) * d.AlignH, false);
        int top = d.TopLine;
        if (line < top) d.TopLine = line;
        else if (top + d.VisibleLines <= line) d.TopLine = line - d.VisibleLines + 1;
        TextObjects.MarkText(id, d);
        return true;
    }

    public static int GetCaretColumnPos(DbId id) => TextObjects.Of(id)?.Caret.Column ?? 0;

    /// <summary>Slot 46, <c>StringGob::SetCaretColumnPos</c> @0x4946e0. A column past the line's end walks onto
    /// the next line and a negative one onto the previous, which is how a text box's arrow keys work. The
    /// overflow test also fires AT the end when the line ends in a newline, so the caret does not sit after it.</summary>
    public static bool SetCaretColumnPos(DbId id, int column)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null) return false;
        int line = d.Caret.Line;
        if (line >= d.LineCount)
        {
            // The engine zeroes the column and the x and LEAVES the line where it is.
            d.Caret.Column = 0;
            d.Caret.X = 0f;
            return true;
        }
        TextLine l = d.Lines[line];
        int len = l.Length;
        if (column < 0)
        {
            if (!SetCaretLinePos(id, line - 1)) return false;
            return SetCaretColumnPos(id, column + d.Lines[d.Caret.Line].Length);
        }
        bool past = len < column
                 || (column == len && column > 0 && l.Bytes[len - 1] == (byte)'\n');
        if (past)
        {
            if (SetCaretLinePos(id, line + 1)) return SetCaretColumnPos(id, column - len);
            d.Caret.Column = len;
        }
        else d.Caret.Column = column;
        TextObjects.UpdateCaret(d, d.Caret.Line, 1e6f, true);
        TextObjects.MarkText(id, d);
        return true;
    }

    /// <summary>Slot 47, <c>StringGob::MoveCaretTo</c> @0x4926d0: the mouse hit. The point arrives in WORLD
    /// space and the engine's first statement is <c>conj(orientation) * (p - position)</c>, scale deliberately
    /// not divided out. The line comes from accumulating line heights from the top (a click above the box
    /// selects the line BEFORE it), the column from an UNSNAPPED x with column -1. No <c>spacingB</c> term.</summary>
    public static int MoveCaretTo(DbId id, Vector3 world)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || d.Font == null) return 0;
        Vector3 local = Vector3.Transform(world - SceneDb.ObjPos[id.Index],
                                          Quaternion.Conjugate(SceneDb.ObjRot[id.Index]));
        int top = d.TopLine, n = d.LineCount;
        float head = (d.MaxHeight - d.VisibleHeight) * d.AlignV;
        float y = -(local.Y - d.SubLineScroll);
        int line;
        if (y < head)
        {
            line = top - 1;
            if (line < top) line = top;
        }
        else
        {
            line = top;
            if (top + d.VisibleLines <= top || n <= top) { /* nothing visible: fall through to the clamp */ }
            else
            {
                float run = head;
                int i = top, last = Math.Min(top + d.VisibleLines, n);
                for (; i < last; i++)
                {
                    run += d.Lines[i].Height;
                    if (y < run) break;
                }
                line = i < last ? i : i;
                if (line < top) line = top;
            }
        }
        if (line >= n)
        {
            line = n - 1;
            if (line < 0) { d.Caret.Line = 0; d.Caret.Column = 0; d.Caret.X = 0f; return 0; }
            d.Caret.Line = line;
            d.Caret.Column = -1;
            TextObjects.UpdateCaret(d, line, 1e6f, false);
            TextObjects.MarkText(id, d);
            return 0;
        }
        d.Caret.Line = line;
        d.Caret.Column = -1;
        TextObjects.UpdateCaret(d, line, local.X - (d.MaxWidth - d.Lines[line].Width) * d.AlignH, false);
        TextObjects.MarkText(id, d);
        return 1;
    }

    public static void SetCaretVisible(DbId id, bool on)
    { TextDoc? d = TextObjects.Of(id); if (d == null) return; d.CaretVisible = on; TextObjects.MarkText(id, d); }

    // ---- 49, 50 ----

    /// <summary>Slot 49: the engine drops a refcount and may free the font. CX's font table is keyed on the name
    /// and shared, so the slot answers without freeing and the font retires with the atlas.</summary>
    public static int SuggestDeleteCurrentFont(DbId id) => GetCurrentFontIndex(id);

    /// <summary>Slot 50: the engine's raw <c>CStringItem**</c> (<c>StringGob+0x500</c>). The facade hands out
    /// native storage carrying one 1-based line-index sentinel per line, never a pointer, which
    /// <see cref="SyncItemOrder"/> reads back; never 0, the engine dereferences it. Capacity rounds up to a
    /// power of two -- the sort sizes every array from the FIRST column's line count and reads past a short one.</summary>
    public static nint GetStringsArray(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        return d == null ? 0 : ItemArray(d);
    }

    /// <inheritdoc cref="GetStringsArray(DbId)"/>
    public static unsafe nint ItemArray(TextDoc d)
    {
        int n = d.LineCount;
        int cap = 4;
        while (cap < n) cap <<= 1;
        if (d.ItemCap < cap)
        {
            d.ItemBuf = (nint)NativeMemory.Realloc((void*)d.ItemBuf, (nuint)cap * (nuint)sizeof(nint));
            d.ItemCap = cap;
        }
        nint* a = (nint*)d.ItemBuf;
        for (int i = 0; i < n; i++) a[i] = i + 1;
        for (int i = n; i < cap; i++) a[i] = 0;
        d.ItemCount = n;
        return d.ItemBuf;
    }

    /// <summary>Apply whatever permutation the engine wrote into the array <see cref="GetStringsArray"/> handed
    /// out, then stand the array down. Runs at the head of every <c>CAurString</c> slot entry, because the sort
    /// routines permute and announce nothing; with no array outstanding the cost is one field test. A sentinel
    /// outside 1..n, or seen twice, leaves the order alone rather than scrambling it.</summary>
    public static void SyncItemOrder(DbId id)
    {
        TextDoc? d = TextObjects.Of(id);
        if (d == null || d.ItemCount == 0) return;
        if (ApplyItemOrder(d)) TextObjects.MarkText(id, d);
    }

    /// <inheritdoc cref="SyncItemOrder(DbId)"/>
    /// <returns>Whether the lines moved, so the caller marks the record dirty exactly when they did.</returns>
    public static unsafe bool ApplyItemOrder(TextDoc d)
    {
        if (d.ItemCount == 0) return false;
        int n = d.ItemCount;
        d.ItemCount = 0;
        if (n > d.LineCount) return false;   // lines changed under the array; it no longer describes them

        nint* a = (nint*)d.ItemBuf;
        bool moved = false;
        for (int i = 0; i < n; i++)
        {
            nint v = a[i];
            if (v < 1 || v > n) return false;
            if (v != i + 1) moved = true;
        }
        if (!moved) return false;

        TextLine[] src = new TextLine[n];
        Array.Copy(d.Lines, src, n);
        for (int i = 0; i < n; i++)
        {
            int from = (int)a[i] - 1;
            if (src[from] == null) return true;   // a duplicate sentinel: refuse the rest of the permutation
            d.Lines[i] = src[from];
            src[from] = null!;
        }
        return true;
    }
}
