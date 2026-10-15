using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;

namespace CX.Alloy.Seam;

/// <summary>The native half of <c>CAurString</c>: one <c>[UnmanagedCallersOnly]</c> entry point per slot,
/// resolving the facade and calling <see cref="TextSlots"/> for the semantics. The shell is <c>StringGob</c>-sized
/// with the <c>CAurString</c> vptr at +0x4c0, so <c>this</c> is a sub-object pointer that <see cref="Facade.Shell"/>
/// normalises; slot 6's <c>Vector SetPosition(Vector)</c> is per-OS (Win64 sret in rdx/rax, SysV xmm0:xmm1).</summary>
public sealed unsafe class StringSlots : ISeamProvider
{
    private const SeamTable T = SeamTable.CAurString;

    /// <summary>The native copies <c>GetTextLine</c> hands out. Callers hold several at once -- the chargen lists
    /// <c>strcasecmp</c> two labels' lines to sort -- so each call takes the next buffer of a ring.</summary>
    private static readonly LineRingState _lines = new();

    /// <summary>A ring of NUL-terminated native buffers, grown per slot as needed.
    /// ponytail: a pointer stays valid for <see cref="Size"/> calls, not until its text changes; a per-shell
    /// copy is the upgrade if a caller ever holds more.</summary>
    private sealed class LineRingState
    {
        public const int Size = 16;
        public readonly nint[] Buf = new nint[Size];
        public readonly int[] Cap = new int[Size];
        public int Next;
    }

    public void Register(SeamRegistry r)
    {
        bool win = OperatingSystem.IsWindows();
        // the two destructors are NOT the same call. Slot 0 is the complete-object destructor and
        // leaves the storage alive; only slot 1 deletes. Binding both to the deleting form retires shells the
        // game still uses (the options panel destructs its labels, then re-attaches them).
        r.Slot(T, 0, (void*)(delegate* unmanaged<nint, void>)&DestructBody);
        r.Slot(T, 1, (void*)(delegate* unmanaged<nint, void>)&Destruct);
        r.Slot(T, 2, (void*)(delegate* unmanaged<nint, nint, nint, int, nint>)&AttachToObject);
        r.Slot(T, 3, (void*)(delegate* unmanaged<nint, nint, nint, int, nint>)&AttachToPart);
        r.Slot(T, 4, (void*)(delegate* unmanaged<nint, nint, nint, nint, int>)&AttachCornersToObjects);
        r.Slot(T, 5, (void*)(delegate* unmanaged<nint, EVec, int>)&SetPositionOffset);
        r.Slot(T, 6, win ? (void*)(delegate* unmanaged<nint, nint, EVec, nint>)&SetPositionWin
                         : (void*)(delegate* unmanaged<nint, EVec, EVec>)&SetPositionLin);
        r.Slot(T, 7, (void*)(delegate* unmanaged<nint, nint, int, void>)&AttachModel);
        r.Slot(T, 8, (void*)(delegate* unmanaged<nint, int, nint>)&GetAttachedModel);
        r.Slot(T, 9, (void*)(delegate* unmanaged<nint, nint>)&GetExternalData);
        r.Slot(T, 10, (void*)(delegate* unmanaged<nint, nint, void>)&SetExternalData);
        r.Slot(T, 11, (void*)(delegate* unmanaged<nint, nint, int>)&SetFont);
        r.Slot(T, 12, (void*)(delegate* unmanaged<nint, nint, byte, int>)&SetFontPassword);
        r.Slot(T, 13, (void*)(delegate* unmanaged<nint, int>)&GetCurrentFontIndex);
        r.Slot(T, 14, (void*)(delegate* unmanaged<nint, float, void>)&SetAlignment);
        r.Slot(T, 15, (void*)(delegate* unmanaged<nint, float, void>)&SetAlignmentVertical);
        r.Slot(T, 16, (void*)(delegate* unmanaged<nint, float, float, float, float, void>)&SetColorDefault);
        r.Slot(T, 17, (void*)(delegate* unmanaged<nint, int, float, float, float, float, void>)&SetIndexedColor);
        r.Slot(T, 18, (void*)(delegate* unmanaged<nint, int, int>)&ScrollToLine);
        r.Slot(T, 19, (void*)(delegate* unmanaged<nint, int, int>)&ScrollToPixel);
        r.Slot(T, 20, (void*)(delegate* unmanaged<nint, int, float>)&GetLinePixelWidth);
        r.Slot(T, 21, (void*)(delegate* unmanaged<nint, int, float>)&GetLinePixelHeight);
        r.Slot(T, 22, (void*)(delegate* unmanaged<nint, int, float, void>)&SetLinePixelHeight);
        r.Slot(T, 23, (void*)(delegate* unmanaged<nint, void>)&RefreshDisplay);
        r.Slot(T, 24, (void*)(delegate* unmanaged<nint, nint>)&GetDisplayInfo);
        r.Slot(T, 25, (void*)(delegate* unmanaged<nint, float, void>)&SetMaxWidth);
        r.Slot(T, 26, (void*)(delegate* unmanaged<nint, float, void>)&SetMaxHeight);
        r.Slot(T, 27, (void*)(delegate* unmanaged<nint, nint, byte>)&SetText);
        r.Slot(T, 28, (void*)(delegate* unmanaged<nint, nint, byte>)&AddText);
        r.Slot(T, 29, (void*)(delegate* unmanaged<nint, int, nint>)&GetTextLine);
        r.Slot(T, 30, (void*)(delegate* unmanaged<nint, int>)&GetTextLineCount);
        r.Slot(T, 31, (void*)(delegate* unmanaged<nint, int, int>)&GetLineState);
        r.Slot(T, 32, (void*)(delegate* unmanaged<nint, int, int, void>)&SetLineState);
        r.Slot(T, 33, (void*)(delegate* unmanaged<nint, byte, void>)&SetTextWrapping);
        r.Slot(T, 34, (void*)(delegate* unmanaged<nint, int, int, void>)&SetLineColor);
        r.Slot(T, 35, (void*)(delegate* unmanaged<nint, int, int>)&GetLineColor);
        r.Slot(T, 36, (void*)(delegate* unmanaged<nint, int>)&GetNumColors);
        r.Slot(T, 37, (void*)(delegate* unmanaged<nint, nint, int>)&AddLine);
        r.Slot(T, 38, (void*)(delegate* unmanaged<nint, nint, int, void>)&InsertLine);
        r.Slot(T, 39, (void*)(delegate* unmanaged<nint, int, void>)&DeleteLine);
        r.Slot(T, 40, (void*)(delegate* unmanaged<nint, int, nint, void>)&SetLine);
        r.Slot(T, 41, (void*)(delegate* unmanaged<nint, nint, byte>)&InsertTextAtCaret);
        r.Slot(T, 42, (void*)(delegate* unmanaged<nint, int, byte>)&DeleteTextAtCaret);
        r.Slot(T, 43, (void*)(delegate* unmanaged<nint, int>)&GetCaretLinePos);
        r.Slot(T, 44, (void*)(delegate* unmanaged<nint, int, byte>)&SetCaretLinePos);
        r.Slot(T, 45, (void*)(delegate* unmanaged<nint, int>)&GetCaretColumnPos);
        r.Slot(T, 46, (void*)(delegate* unmanaged<nint, int, byte>)&SetCaretColumnPos);
        r.Slot(T, 47, (void*)(delegate* unmanaged<nint, EVec, int>)&MoveCaretTo);
        r.Slot(T, 48, (void*)(delegate* unmanaged<nint, byte, void>)&SetCaretVisible);
        r.Slot(T, 49, (void*)(delegate* unmanaged<nint, int>)&SuggestDeleteCurrentFont);
        r.Slot(T, 50, (void*)(delegate* unmanaged<nint, nint>)&GetStringsArray);
        r.Slot(T, 51, (void*)(delegate* unmanaged<nint, float, float, float, float, void>)&SetColorSelected);

        // The four slots StringGob declares that are NEW virtuals rather than CAurString overrides, so they
        // live in the PRIMARY table (Linux 104-107, Windows 93-96) and not in the CAurString one. The GUI
        // reads them back after setting them -- a live run reached one -- and the engine's own bodies are
        // plain getters off the text state.
        r.Slot(SeamTable.StringGob, 104, (void*)(delegate* unmanaged<nint, float>)&GetAlignment);
        r.Slot(SeamTable.StringGob, 105, (void*)(delegate* unmanaged<nint, float>)&GetAlignmentVertical);
        r.Slot(SeamTable.StringGob, 106, (void*)(delegate* unmanaged<nint, nint>)&GetFontName);
        r.Slot(SeamTable.StringGob, 107, (void*)(delegate* unmanaged<nint, float*, float*, float*, float*, void>)&GetColorDefault);
    }

    /// <summary><c>StringGob::GetAlignment()</c> -- the horizontal alignment <c>SetAlignment</c> stored.</summary>
    [UnmanagedCallersOnly]
    private static float GetAlignment(nint self)
    {
        DbId id = Id(self, 0);
        return id.IsNull ? 0f : TextSlots.GetAlignment(id);
    }

    /// <summary><c>StringGob::GetAlignmentVertical()</c>.</summary>
    [UnmanagedCallersOnly]
    private static float GetAlignmentVertical(nint self)
    {
        DbId id = Id(self, 0);
        return id.IsNull ? 0f : TextSlots.GetAlignmentVertical(id);
    }

    /// <summary><c>StringGob::GetFontName()</c> returns a <c>char const*</c> the caller may hold, so the name
    /// is interned natively -- the same rule as every other name the seam hands out.</summary>
    [UnmanagedCallersOnly]
    private static nint GetFontName(nint self)
    {
        DbId id = Id(self, 0);
        return id.IsNull ? 0 : NativeNames.Of(TextSlots.GetFontName(id));
    }

    /// <summary><c>StringGob::GetColorDefault(float&amp;, float&amp;, float&amp;, float&amp;)</c>: colour index 0,
    /// through four optional out-pointers.</summary>
    [UnmanagedCallersOnly]
    private static void GetColorDefault(nint self, float* r, float* g, float* b, float* a)
    {
        DbId id = Id(self, 0);
        if (id.IsNull) return;
        TextSlots.GetIndexedColor(id, 0, out float cr, out float cg, out float cb, out float ca);
        if (r != null) *r = cr;
        if (g != null) *g = cg;
        if (b != null) *b = cb;
        if (a != null) *a = ca;
    }

    /// <summary>Resolve the facade's record, and first apply what the engine wrote through a handed-out pointer --
    /// slot 50's item array (the sort routines' permutation) and slot 24's display block (a label's box) --
    /// since neither write is announced, the next slot call is the only sync point there is.</summary>
    private static DbId Id(nint self, int linuxSlot)
    {
        DbId id = Facade.StringRecord(self, SeamVtables.Logical(T, linuxSlot));
        if (!id.IsNull) { TextSlots.SyncItemOrder(id); TextSlots.SyncDisplayInfo(id); }
        return id;
    }

    /// <summary>A NUL-terminated engine string as the codepage bytes the text layout works in -- no transcode,
    /// because the engine's text is a single-byte codepage and the span is read, not stored.</summary>
    private static ReadOnlySpan<byte> Bytes(nint p)
    {
        if (p == 0) return default;
        int n = 0;
        byte* s = (byte*)p;
        while (s[n] != 0) n++;
        return new ReadOnlySpan<byte>(s, n);
    }

    private static string Text(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    /// <summary>Slot 0: the complete-object destructor. It runs the body and leaves the object allocated, so
    /// the facade is NOT retired -- <c>StringGob::~StringGob</c> @0x494be0 ends in <c>~Gob</c>, not in
    /// <c>operator delete</c>.</summary>
    [UnmanagedCallersOnly]
    private static void DestructBody(nint self)
    {
        DbId id = Id(self, 0);
        if (!id.IsNull) TextSlots.DestructBody(id);
    }

    /// <summary>Slot 1: the deleting destructor -- the body, then the object goes.</summary>
    [UnmanagedCallersOnly]
    private static void Destruct(nint self)
    {
        DbId id = Id(self, 1);
        if (!id.IsNull) TextSlots.Destruct(id);
        Facade.Retire(self);
    }

    /// <summary>Label attaches refused because the named part is not on the target -- the engine's own 0, which is
    /// what sends a GUI panel down its corner-attach fallback.</summary>
    public static long UnresolvedAttach;

    [UnmanagedCallersOnly]
    private static nint AttachToObject(nint self, nint to, nint partName, int mode)
    {
        DbId id = Id(self, 2);
        if (id.IsNull) return 0;
        DbId target = to == 0 ? default : Facade.Object(to, SeamVtables.Logical(T, 2));
        string? name = Marshal.PtrToStringUTF8(partName);
        int part = target.IsNull ? -1 : ObjectSlots.PartByName(target, name);
        // Gob::AttachToObject @0x43e0e0 answers 0 and leaves the placement alone when a NAMED part does not resolve;
        // CGuiPanel::CloseLayoutFile @0x589fd0 relies on that 0 to fall back to AttachCornersToObjects(text0, text1).
        if (!target.IsNull && part < 0 && !string.IsNullOrEmpty(name)) { UnresolvedAttach++; return 0; }
        ObjectSlots.UnlinkText(id);
        nint r = TextSlots.AttachToObject(id, target, part, mode);
        ObjectSlots.LinkText(id, target);
        return r;
    }

    [UnmanagedCallersOnly]
    private static nint AttachToPart(nint self, nint to, nint part, int mode)
    {
        DbId id = Id(self, 3);
        if (id.IsNull) return 0;
        DbId target = to == 0 ? default : Facade.Object(to, SeamVtables.Logical(T, 3));
        int idx = PartShell.Resolve(part, out _, out int p) ? p : -1;
        ObjectSlots.UnlinkText(id);
        nint r = TextSlots.AttachToPart(id, target, idx, mode);
        ObjectSlots.LinkText(id, target);
        return r;
    }

    [UnmanagedCallersOnly]
    private static int AttachCornersToObjects(nint self, nint to, nint a, nint b)
    {
        DbId id = Id(self, 4);
        if (id.IsNull) return 0;
        DbId target = to == 0 ? default : Facade.Object(to, SeamVtables.Logical(T, 4));
        ObjectSlots.UnlinkText(id);
        int r = TextSlots.AttachCornersToObjects(id, target, Marshal.PtrToStringUTF8(a) ?? "", Marshal.PtrToStringUTF8(b) ?? "");
        ObjectSlots.LinkText(id, target);
        return r;
    }

    [UnmanagedCallersOnly]
    private static int SetPositionOffset(nint self, EVec v)
    {
        DbId id = Id(self, 5);
        return id.IsNull ? 0 : TextSlots.SetPositionOffset(id, v.V);
    }

    [UnmanagedCallersOnly]
    private static EVec SetPositionLin(nint self, EVec v) => Place(self, v);

    [UnmanagedCallersOnly]
    private static nint SetPositionWin(nint self, nint sret, EVec v)
    {
        EVec r = Place(self, v);
        if (sret != 0) *(EVec*)sret = r;
        return sret;
    }

    /// <summary>Slot 6 is both a text setter and a <c>Gob</c> setter: a text object's position is the shell's
    /// mirrored word too, so the facade's +0xa8 stays current and a <c>GetPosition</c> through the object's
    /// other vptr answers the same value.</summary>
    private static EVec Place(nint self, EVec v)
    {
        DbId id = Id(self, 6);
        if (id.IsNull) return default;
        Vector3 r = TextSlots.SetPosition(id, v.V);
        nint shell = Facade.Shell(self);
        if (shell != 0) Facade.SetXform(id, shell, r, SceneDb.ObjRot[id.Index]);
        return EVec.From(r);
    }

    [UnmanagedCallersOnly]
    private static void AttachModel(nint self, nint model, int line)
    {
        DbId id = Id(self, 7);
        if (id.IsNull) return;
        TextSlots.AttachModel(id, model == 0 ? default : Facade.Object(model, SeamVtables.Logical(T, 7)), line);
    }

    [UnmanagedCallersOnly]
    private static nint GetAttachedModel(nint self, int line)
    {
        DbId id = Id(self, 8);
        if (id.IsNull) return 0;
        DbId m = TextSlots.GetAttachedModel(id, line);
        return Facade.ObjectShell(m);
    }

    [UnmanagedCallersOnly]
    private static nint GetExternalData(nint self)
    {
        DbId id = Id(self, 9);
        return id.IsNull ? 0 : TextSlots.GetExternalData(id);
    }

    [UnmanagedCallersOnly]
    private static void SetExternalData(nint self, nint data)
    {
        DbId id = Id(self, 10);
        if (!id.IsNull) TextSlots.SetExternalData(id, data);
    }

    [UnmanagedCallersOnly]
    private static int SetFont(nint self, nint name)
    {
        DbId id = Id(self, 11);
        return id.IsNull ? 0 : TextSlots.SetFont(id, Text(name));
    }

    [UnmanagedCallersOnly]
    private static int SetFontPassword(nint self, nint name, byte mask)
    {
        DbId id = Id(self, 12);
        return id.IsNull ? 0 : TextSlots.SetFontPassword(id, Text(name), mask);
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentFontIndex(nint self)
    {
        DbId id = Id(self, 13);
        return id.IsNull ? 0 : TextSlots.GetCurrentFontIndex(id);
    }

    [UnmanagedCallersOnly]
    private static void SetAlignment(nint self, float h)
    {
        DbId id = Id(self, 14);
        if (!id.IsNull) TextSlots.SetAlignment(id, h);
    }

    [UnmanagedCallersOnly]
    private static void SetAlignmentVertical(nint self, float v)
    {
        DbId id = Id(self, 15);
        if (!id.IsNull) TextSlots.SetAlignmentVertical(id, v);
    }

    [UnmanagedCallersOnly]
    private static void SetColorDefault(nint self, float r, float g, float b, float a)
    {
        DbId id = Id(self, 16);
        if (!id.IsNull) TextSlots.SetColorDefault(id, r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void SetIndexedColor(nint self, int index, float r, float g, float b, float a)
    {
        DbId id = Id(self, 17);
        if (!id.IsNull) TextSlots.SetIndexedColor(id, index, r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static int ScrollToLine(nint self, int line)
    {
        DbId id = Id(self, 18);
        return id.IsNull ? 0 : TextSlots.ScrollToLine(id, line);
    }

    [UnmanagedCallersOnly]
    private static int ScrollToPixel(nint self, int px)
    {
        DbId id = Id(self, 19);
        return id.IsNull ? 0 : TextSlots.ScrollToPixel(id, px);
    }

    [UnmanagedCallersOnly]
    private static float GetLinePixelWidth(nint self, int line)
    {
        DbId id = Id(self, 20);
        return id.IsNull ? 0f : TextSlots.GetLinePixelWidth(id, line);
    }

    [UnmanagedCallersOnly]
    private static float GetLinePixelHeight(nint self, int line)
    {
        DbId id = Id(self, 21);
        return id.IsNull ? 0f : TextSlots.GetLinePixelHeight(id, line);
    }

    [UnmanagedCallersOnly]
    private static void SetLinePixelHeight(nint self, int line, float px)
    {
        DbId id = Id(self, 22);
        if (!id.IsNull) TextSlots.SetLinePixelHeight(id, line, px);
    }

    [UnmanagedCallersOnly]
    private static void RefreshDisplay(nint self)
    {
        DbId id = Id(self, 23);
        if (!id.IsNull) TextSlots.RefreshDisplay(id);
    }

    /// <summary>Slot 24 returns a <c>CAurStringDisplayInfo*</c> the caller reads and writes in place: the
    /// document's own block (<see cref="TextSlots.GetDisplayInfo"/>), or a zeroed scratch one with no record.</summary>
    [UnmanagedCallersOnly]
    private static nint GetDisplayInfo(nint self)
    {
        DbId id = Id(self, 24);
        nint p = id.IsNull ? 0 : TextSlots.GetDisplayInfo(id);
        if (p != 0) return p;
        if (_noInfo == 0) _noInfo = (nint)NativeMemory.AllocZeroed(TextSlots.DisplayInfoSize);
        return _noInfo;
    }

    private static nint _noInfo;

    [UnmanagedCallersOnly]
    private static void SetMaxWidth(nint self, float w)
    {
        DbId id = Id(self, 25);
        if (!id.IsNull) TextSlots.SetMaxWidth(id, w);
    }

    [UnmanagedCallersOnly]
    private static void SetMaxHeight(nint self, float h)
    {
        DbId id = Id(self, 26);
        if (!id.IsNull) TextSlots.SetMaxHeight(id, h);
    }

    [UnmanagedCallersOnly]
    private static byte SetText(nint self, nint text)
    {
        DbId id = Id(self, 27);
        return id.IsNull ? (byte)0 : TextSlots.SetText(id, Bytes(text)) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static byte AddText(nint self, nint text)
    {
        DbId id = Id(self, 28);
        return id.IsNull ? (byte)0 : TextSlots.AddText(id, Bytes(text)) ? (byte)1 : (byte)0;
    }

    /// <summary>Slot 29 returns a <c>char const*</c> the caller may hold past the call, so the line's bytes are
    /// copied into the next <see cref="LineRingState"/> buffer -- the engine's pointer was into the line's own
    /// storage, and CX's lines are managed memory a native caller must not see.</summary>
    [UnmanagedCallersOnly]
    private static nint GetTextLine(nint self, int line)
    {
        DbId id = Id(self, 29);
        if (id.IsNull) return 0;
        ReadOnlySpan<byte> src = TextSlots.GetTextLine(id, line);
        LineRingState r = _lines;
        int i = r.Next;
        r.Next = (i + 1) % LineRingState.Size;
        if (src.Length + 1 > r.Cap[i])
        {
            int want = Math.Max(256, src.Length + 1);
            void* b = NativeMemory.Realloc((void*)r.Buf[i], (nuint)want);
            if (b == null) return 0;
            r.Buf[i] = (nint)b;
            r.Cap[i] = want;
        }
        Span<byte> dst = new((void*)r.Buf[i], r.Cap[i]);
        src.CopyTo(dst);
        dst[src.Length] = 0;
        return r.Buf[i];
    }

    [UnmanagedCallersOnly]
    private static int GetTextLineCount(nint self)
    {
        DbId id = Id(self, 30);
        return id.IsNull ? 0 : TextSlots.GetTextLineCount(id);
    }

    [UnmanagedCallersOnly]
    private static int GetLineState(nint self, int line)
    {
        DbId id = Id(self, 31);
        return id.IsNull ? 0 : TextSlots.GetLineState(id, line);
    }

    [UnmanagedCallersOnly]
    private static void SetLineState(nint self, int line, int state)
    {
        DbId id = Id(self, 32);
        if (!id.IsNull) TextSlots.SetLineState(id, line, state);
    }

    [UnmanagedCallersOnly]
    private static void SetTextWrapping(nint self, byte on)
    {
        DbId id = Id(self, 33);
        if (!id.IsNull) TextSlots.SetTextWrapping(id, on != 0);
    }

    [UnmanagedCallersOnly]
    private static void SetLineColor(nint self, int line, int colour)
    {
        DbId id = Id(self, 34);
        if (!id.IsNull) TextSlots.SetLineColor(id, line, colour);
    }

    [UnmanagedCallersOnly]
    private static int GetLineColor(nint self, int line)
    {
        DbId id = Id(self, 35);
        return id.IsNull ? 0 : TextSlots.GetLineColor(id, line);
    }

    [UnmanagedCallersOnly]
    private static int GetNumColors(nint self)
    {
        DbId id = Id(self, 36);
        return id.IsNull ? 0 : TextSlots.GetNumColors(id);
    }

    [UnmanagedCallersOnly]
    private static int AddLine(nint self, nint text)
    {
        DbId id = Id(self, 37);
        return id.IsNull ? 0 : TextSlots.AddLine(id, Bytes(text));
    }

    [UnmanagedCallersOnly]
    private static void InsertLine(nint self, nint text, int at)
    {
        DbId id = Id(self, 38);
        if (!id.IsNull) TextSlots.InsertLine(id, Bytes(text), at);
    }

    [UnmanagedCallersOnly]
    private static void DeleteLine(nint self, int at)
    {
        DbId id = Id(self, 39);
        if (!id.IsNull) TextSlots.DeleteLine(id, at);
    }

    [UnmanagedCallersOnly]
    private static void SetLine(nint self, int at, nint text)
    {
        DbId id = Id(self, 40);
        if (!id.IsNull) TextSlots.SetLine(id, at, Bytes(text));
    }

    [UnmanagedCallersOnly]
    private static byte InsertTextAtCaret(nint self, nint text)
    {
        DbId id = Id(self, 41);
        return id.IsNull ? (byte)0 : TextSlots.InsertTextAtCaret(id, Bytes(text)) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static byte DeleteTextAtCaret(nint self, int count)
    {
        DbId id = Id(self, 42);
        return id.IsNull ? (byte)0 : TextSlots.DeleteTextAtCaret(id, count) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static int GetCaretLinePos(nint self)
    {
        DbId id = Id(self, 43);
        return id.IsNull ? 0 : TextSlots.GetCaretLinePos(id);
    }

    [UnmanagedCallersOnly]
    private static byte SetCaretLinePos(nint self, int line)
    {
        DbId id = Id(self, 44);
        return id.IsNull ? (byte)0 : TextSlots.SetCaretLinePos(id, line) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static int GetCaretColumnPos(nint self)
    {
        DbId id = Id(self, 45);
        return id.IsNull ? 0 : TextSlots.GetCaretColumnPos(id);
    }

    [UnmanagedCallersOnly]
    private static byte SetCaretColumnPos(nint self, int column)
    {
        DbId id = Id(self, 46);
        return id.IsNull ? (byte)0 : TextSlots.SetCaretColumnPos(id, column) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static int MoveCaretTo(nint self, EVec local)
    {
        DbId id = Id(self, 47);
        return id.IsNull ? 0 : TextSlots.MoveCaretTo(id, local.V);
    }

    [UnmanagedCallersOnly]
    private static void SetCaretVisible(nint self, byte on)
    {
        DbId id = Id(self, 48);
        if (!id.IsNull) TextSlots.SetCaretVisible(id, on != 0);
    }

    [UnmanagedCallersOnly]
    private static int SuggestDeleteCurrentFont(nint self)
    {
        DbId id = Id(self, 49);
        return id.IsNull ? 0 : TextSlots.SuggestDeleteCurrentFont(id);
    }

    [UnmanagedCallersOnly]
    private static nint GetStringsArray(nint self)
    {
        DbId id = Id(self, 50);
        return id.IsNull ? 0 : TextSlots.GetStringsArray(id);
    }

    [UnmanagedCallersOnly]
    private static void SetColorSelected(nint self, float r, float g, float b, float a)
    {
        DbId id = Id(self, 51);
        if (!id.IsNull) TextSlots.SetColorSelected(id, r, g, b, a);
    }
}
