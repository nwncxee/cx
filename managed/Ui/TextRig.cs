using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

using CX.Services.Chat.Core.Model;

namespace CX.Ui;

/// <summary>
/// The UI's draw recorder, not a renderer: it owns the glyph atlas (a stb_truetype coverage + distance-field bake, cold) and turns every CX
/// window's draw -- <see cref="DrawBatch"/>, <see cref="DrawPortrait"/>, the icon and skin quads -- into a
/// <see cref="UiDrawList"/> command. <see cref="Replay"/> issues a list in whichever context draws the frame:
/// Alloy's GUI pass, or the engine's present with Alloy off. Nothing here decides WHAT to draw.
/// </summary>
public static unsafe partial class TextRig
{
    /// <summary>The UI typeface's <see cref="FontRegistry"/> name: the embedded DejaVuSans (third_party/dejavu,
    /// Bitstream Vera licence) unless <c>font.default=</c> overrides it. Shipped rather than searched:
    /// a per-machine font meant per-machine metrics. Whatever it is bound to only ever decides how TEXT looks --
    /// the chrome's icons come from <see cref="Icons"/>.</summary>
    private const string DefaultFace = FontRegistry.DefaultName;

    /// <summary>The icon source every face draws the <c>icon</c> rows of <see cref="Ranges"/> from: the embedded
    /// DejaVu, so a configured typeface (Windows' Arial has none of these symbols) cannot lose the chrome.</summary>
    private static (string Name, byte[]? Font) Icons => (FontRegistry.EmbeddedName, FontRegistry.Bytes(FontRegistry.EmbeddedName));

    private const float FontPx = 18f;
    private const int AtlasSize = 512;
    private const int Floats = VertexBatch.Floats;   // x,y,u,v,r,g,b,a

    // ponytail: static ranges; dynamic on-demand atlas is the ceiling. Text rows come from the face's own
    // typeface (no CJK -- DejaVu ships none, so CJK is out of scope for v1); icon rows from Icons, always.
    // The close button × is U+00D7 inside Latin-1, so it is text: a font that covers Latin-1 has it.
    private static readonly (int lo, int hi, string name, bool icon)[] Ranges =
    {
        (0x0020, 0x007E, "Basic Latin", false),
        (0x00A0, 0x00FF, "Latin-1", false),
        (0x0100, 0x017F, "Latin Extended-A", false),
        (0x0400, 0x04FF, "Cyrillic", false),
        (0x2013, 0x2014, "– — dashes", false),
        (0x2026, 0x2026, "… ellipsis", false),
        (0x21BB, 0x21BB, "↻ restart", true),
        (0x25AA, 0x25AA, "▪ pin", true),
        (0x25B8, 0x25B8, "▸ disclosure", true),
        (0x25BE, 0x25BE, "▾ arrow", true),
        (0x2699, 0x2699, "⚙ gear", true),
    };

    private static ExtenderLog? _log;
    private static AtlasSheet? _sheet;         // the one bitmap every face lives in; swapped whole on re-bake
    private static string? _defaultName;          // the default typeface
    private static float _bakedPx;             // glyph height the default face was baked at
    /// <summary>Faces the sheet is baked from; index 0 is the default. Extra faces are appended by <see cref="Face"/>.</summary>
    private static readonly List<FaceRequest> _requests = new();

    /// <summary>The default UI face (null until <see cref="Init"/>, or when stbtt is absent under run-demo).</summary>
    public static GlyphAtlas? Atlas => _sheet?.Default;

    /// <summary>A face of the default typeface at <paramref name="px"/>. COLD on a miss: the first request re-bakes
    /// the whole sheet (allocates heavily), so ask in <c>Initialize</c>, never on the present thread. The returned
    /// object is epoch-scoped: re-fetch when <see cref="AtlasEpoch"/> changes. Null when headless or refused.</summary>
    public static GlyphAtlas? Face(float px) => _defaultName == null ? null : Face(_defaultName, px);

    /// <summary>As <see cref="Face(float)"/>, held by <paramref name="owner"/> until it calls <see cref="ReleaseFace(float, string)"/>.</summary>
    public static GlyphAtlas? Face(float px, string owner) => _defaultName == null ? null : Face(_defaultName, px, owner);

    /// <summary>Hot, alloc-free lookup of an already-baked default-typeface face; null on a miss (never bakes).</summary>
    public static GlyphAtlas? BakedFace(float px)
        => _sheet != null && _defaultName != null && _sheet.TryFace(_defaultName, px, out GlyphAtlas f) ? f : null;

    /// <summary>Hot, alloc-free lookup of an already-baked face of a registry name; null on a miss (never bakes).</summary>
    public static GlyphAtlas? BakedFace(string typeface, float px)
        => _sheet != null && _sheet.TryFace(FontRegistry.Effective(typeface), px, out GlyphAtlas f) ? f : null;

    /// <summary>A face of the <see cref="FontRegistry"/> name <paramref name="typeface"/> at <paramref name="px"/>;
    /// see <see cref="Face(float)"/>. An unknown or refused name is the default typeface.</summary>
    public static GlyphAtlas? Face(string typeface, float px) => Face(typeface, px, SessionOwner);

    /// <summary>As <see cref="Face(string, float)"/>, held by <paramref name="owner"/> until it calls
    /// <see cref="ReleaseFace(string, float, string)"/>.</summary>
    public static GlyphAtlas? Face(string typeface, float px, string owner)
    {
        lock (_bakeLock)
        {
            GlyphAtlas? f = FaceLocked(typeface, px);
            if (f == null) return null;
            var key = (FontRegistry.Effective(typeface), px);
            if (!_holders.TryGetValue(key, out HashSet<string>? who)) _holders[key] = who = new HashSet<string>(StringComparer.Ordinal);
            who.Add(owner);
            return f;
        }
    }

    /// <summary>The holder of a face asked for with no owner: it is held for the session.</summary>
    private const string SessionOwner = "";

    /// <summary>The owners holding each extra face. A face whose last owner lets go leaves the sheet at the next bake.</summary>
    private static readonly Dictionary<(string Typeface, float Px), HashSet<string>> _holders = new();

    /// <summary>Serialises the cold mutators of <see cref="_requests"/> (<see cref="Face(string, float)"/>,
    /// <see cref="ReleaseFace"/>, <see cref="SetFont"/>): the input thread and a pool-thread peak-face re-request
    /// can otherwise interleave on the list mid-bake.</summary>
    private static readonly object _bakeLock = new();

    private static GlyphAtlas? FaceLocked(string typeface, float px)
    {
        if (_sheet == null || _log == null) return null;
        typeface = FontRegistry.Effective(typeface);
        if (_sheet.TryFace(typeface, px, out GlyphAtlas f)) return f;
        byte[]? bytes = FontRegistry.Bytes(typeface);
        if (bytes == null) return null;
        _requests.Add(new FaceRequest(typeface, bytes, px));
        if (!Rebake()) { _requests.RemoveAt(_requests.Count - 1); return null; }
        return _sheet.TryFace(typeface, px, out f) ? f : null;
    }

    /// <summary>The icon font's icon rows alone at <paramref name="px"/> -- the main-menu gear's sprite face,
    /// past <see cref="AtlasMaxPx"/> where no text face is ever requested. Cold: a miss re-bakes the sheet, and a
    /// new px drops the previous icon-only request so the sheet carries one. Null when headless or refused.</summary>
    public static GlyphAtlas? IconFace(float px)
    {
        lock (_bakeLock)
        {
            if (_sheet == null || _log == null) return null;
            (string name, byte[]? font) = Icons;
            if (font == null) return null;
            if (_sheet.TryFace(name, px, out GlyphAtlas f)) return f;
            for (int i = _requests.Count - 1; i >= 1; i--) if (_requests[i].IconsOnly) _requests.RemoveAt(i);
            _requests.Add(new FaceRequest(name, font, px, IconsOnly: true));
            if (!Rebake()) { _requests.RemoveAt(_requests.Count - 1); return null; }
            return _sheet.TryFace(name, px, out f) ? f : null;
        }
    }

    /// <summary>Hot, alloc-free lookup of an already-baked <see cref="IconFace"/>; null on a miss (never bakes).</summary>
    public static GlyphAtlas? BakedIconFace(float px)
        => _sheet != null && _sheet.TryFace(Icons.Name, px, out GlyphAtlas f) ? f : null;

    /// <summary><paramref name="owner"/> no longer needs its face of <paramref name="typeface"/> at <paramref name="px"/>. When no
    /// owner holds it, the sheet stops carrying it from the next re-bake on. Cold; no bake of its own -- the face stays usable
    /// until then. The default face (index 0) is never dropped.</summary>
    public static void ReleaseFace(string typeface, float px, string owner)
    {
        typeface = FontRegistry.Effective(typeface);
        lock (_bakeLock)
        {
            if (_holders.TryGetValue((typeface, px), out HashSet<string>? who))
            {
                who.Remove(owner);
                if (who.Count > 0) return;
                _holders.Remove((typeface, px));
            }
            for (int i = _requests.Count - 1; i >= 1; i--)
                if (_requests[i].Typeface == typeface && _requests[i].Px == px) _requests.RemoveAt(i);
        }
    }

    /// <summary>As <see cref="ReleaseFace(string, float, string)"/> for the default typeface.</summary>
    public static void ReleaseFace(float px, string owner)
    {
        if (_defaultName != null) ReleaseFace(_defaultName, px, owner);
    }

    /// <summary>Raised COLD after <see cref="SetFont"/> swaps in a re-baked sheet, so a face derived from the UI size
    /// (the floaty peak face) can re-request itself at the new height. ponytail: that re-request is a second full
    /// bake per slider tick; folding derived sizes into the first bake is the upgrade if the tick ever feels slow.</summary>
    public static event Action? FontChanged;

    /// <summary>Raised after EVERY sheet swap (a UI font or typeface set, any element's or window's new face), on the
    /// baking thread, inside the bake lock: every face's UVs moved, so an owner of cached glyph geometry rebuilds.</summary>
    public static event Action? Rebaked;

    /// <summary>Baked faces of a registry name, for <c>/knobs</c>.</summary>
    private static int FacesOf(string name)
    {
        int n = 0;
        lock (_bakeLock) for (int i = 0; i < _requests.Count; i++) if (_requests[i].Typeface == name) n++;
        return n;
    }

    /// <summary>Bake <see cref="_requests"/> into a fresh sheet and swap it in; on refusal the old sheet stays.
    /// Every face's UVs move, so one epoch bump invalidates all cached glyph geometry.</summary>
    private static bool Rebake()
    {
        AtlasSheet? baked = AtlasSheet.Bake(_requests, Ranges, Icons, AtlasSize, _log!);
        if (baked == null) return false;
        _sheet = baked;
        _atlasEpoch++;    // every context re-uploads, and cached-geometry consumers rebuild: their glyph UVs went stale
        Rebaked?.Invoke();
        return true;
    }

    /// <summary>Injected log (set in <see cref="Init"/>); the chat draw path reads it via this.</summary>
    internal static ExtenderLog? Log => _log;

    /// <summary>Bumped on every atlas re-bake (<see cref="SetFont"/>), so a cached-geometry consumer
    /// (the attribution overlay) can tell its glyph UVs went stale and rebuild.</summary>
    internal static int AtlasEpoch => _atlasEpoch;
    private static volatile int _atlasEpoch;

    /// <summary>The frame's viewport (px), sampled once per present by the frame host (<see cref="SampleViewport"/>)
    /// and read by every UI draw and by cold consumers (the settings window clamps itself on-screen with these).
    /// 0 until the first frame draws.</summary>
    public static volatile int ViewportW, ViewportH;

    /// <summary>Publish the frame's viewport into <see cref="ViewportW"/>/<see cref="ViewportH"/> once per present,
    /// before the window roll-call: the screen Alloy's GUI pass draws into when Alloy renders, else the engine's live
    /// GL viewport. A no-op off-device, where the pair stays 0.</summary>
    public static void SampleViewport()
    {
        if (CX.Alloy.Renderer.On && CX.Alloy.GuiPass.Screen(out int w, out int h)) { ViewportW = w; ViewportH = h; return; }
        if (_log == null || !Gl.Init(_log) || Gl.GetIntegerv == null) return;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        ViewportW = vp[2]; ViewportH = vp[3];
    }

    public static bool Ready => _sheet != null;

    /// <summary>Where the draws below go: a frame's list while a roll-call records it, else drawn at once.</summary>
    private sealed class RecordState
    {
        public UiDrawList? Into;
        public readonly UiDrawList Now = new();
        /// <summary>The scene test every draw below is stamped with (<see cref="Anchor"/>); <c>Occl</c> false = none.</summary>
        public UiCmd Stamp;
    }

    private static readonly RecordState _record = new();

    /// <summary>Send every draw below into <paramref name="into"/> until the next call; null draws each at once, in the
    /// current context. The roll-call's thread only.</summary>
    public static void Record(UiDrawList? into) { _record.Into = into; _record.Stamp.Occl = false; }

    /// <summary>Anchor every draw below to world point <paramref name="world"/>, shown at UI pixel (<paramref name="ax"/>,
    /// <paramref name="ay"/>): where the world frame covers it, the draws fade to <paramref name="hidden"/> of their alpha.
    /// 1 or more, or <see cref="Unanchor"/>, is no test. Only Alloy's frame has the depth to test against.</summary>
    public static void Anchor(Vector3 world, float ax, float ay, float hidden)
    {
        ref UiCmd s = ref _record.Stamp;
        s.Occl = hidden < 1f;
        s.Hidden = hidden < 0f ? 0f : hidden; s.Wx = world.X; s.Wy = world.Y; s.Wz = world.Z; s.Ax = ax; s.Ay = ay;
    }

    /// <summary>The draws below are anchored to nothing.</summary>
    public static void Unanchor() => _record.Stamp.Occl = false;

    /// <summary>Record one draw, or with no list open replay it now. Hot, zero-alloc.</summary>
    private static void Add(in UiCmd c, ReadOnlySpan<float> verts, int count, ReadOnlySpan<int> rows = default)
    {
        RecordState r = _record;
        UiCmd cmd = c;
        if (r.Stamp.Occl)
        {
            cmd.Occl = true; cmd.Hidden = r.Stamp.Hidden;
            cmd.Wx = r.Stamp.Wx; cmd.Wy = r.Stamp.Wy; cmd.Wz = r.Stamp.Wz; cmd.Ax = r.Stamp.Ax; cmd.Ay = r.Stamp.Ay;
        }
        if (r.Into != null) { r.Into.Add(cmd, verts, count, rows); return; }
        r.Now.Reset(ViewportW, ViewportH);
        r.Now.Add(cmd, verts, count, rows);
        Replay(r.Now);
    }

    /// <summary>The texture a UI draw of engine texture <paramref name="engineTid"/> samples: Alloy's own when Alloy
    /// renders (the upload funnel adopted it), else the engine's. 0 stays 0. Game thread.</summary>
    internal static int Own(int engineTid)
        => engineTid > 0 && CX.Alloy.Renderer.On ? CX.Alloy.OwnedTextures.Draw(engineTid) : engineTid;

    /// <summary>Resolved once: the portrait path runs per drawn plate, so the lazy resolve's compare does not
    /// belong in it. Null when the client does not export the resman getter.</summary>
    internal static readonly AurTextureGetFn _aurGet = EngineFn.AurTextureGet;

    /// <summary>Cold init: load the font and bake the atlas (incl. its solid-white chrome texel). No context exists
    /// yet: each context builds its own GL objects on its first replay.</summary>
    public static void Init(ExtenderLog log)
    {
        _log = log;
        if (_sheet != null || _defaultName != null) return;   // idempotent: probe and ChatService may both call
        FontRegistry.Load(log);
        KnobSurface.Note(() => FontRegistry.Report(FacesOf));
        byte[]? bytes = FontRegistry.Bytes(DefaultFace);
        if (bytes == null) { log.Info("TextRig: no default font (embedded DejaVuSans missing from the image)"); return; }
        _defaultName = DefaultFace;
        _bakedPx = FontPx;
        _requests.Add(new FaceRequest(DefaultFace, bytes, FontPx));
        _sheet = AtlasSheet.Bake(_requests, Ranges, Icons, AtlasSize, log);
        log.Info(_sheet != null ? "TextRig: atlas ready" : "TextRig: atlas bake unavailable");
    }

    /// <summary>The <c>ui.font</c> ladder (spec §4.2 Units): 6-32 pt, every point; 1 pt = 1 design px before GUI scale.</summary>
    public const int MinFontPx = 6, MaxFontPx = 32;
    /// <summary>Atlas-bake ceiling. Above the slider max so the game's UI scale can push the effective px past
    /// 32 (effective = font * GUIScale) without the slider itself going that high; caps the atlas size.</summary>
    public const float AtlasMaxPx = 96f;

    /// <summary>A face size at the GUI scale, clamped like the UI atlas: <paramref name="fontPx"/>, or <c>ui.font</c>
    /// when 0 (a window / menu following the UI size), × <see cref="Metrics.GameUiScale"/>.</summary>
    public static float FacePx(int fontPx)
    {
        float px = (fontPx > 0 ? fontPx : UiKnobs.Font.V) * Metrics.GameUiScale;
        return px < MinFontPx ? MinFontPx : px > AtlasMaxPx ? AtlasMaxPx : px;
    }

    /// <summary>Re-bake the atlas at a new glyph height (cold, on a settings change) and flag the GL texture for
    /// re-upload next frame. No-op when unchanged (debounced for sub-pixel slider drags) or headless. Swaps the
    /// atlas reference atomically; the caller re-publishes the frame so its UVs and metrics match.</summary>
    public static void SetFont(float px)
    {
        if (_sheet == null || _log == null) return;
        px = px < MinFontPx ? MinFontPx : px > AtlasMaxPx ? AtlasMaxPx : px;   // effective px (font * UI scale)
        if (System.Math.Abs(px - _bakedPx) < 0.5f) return;   // integer-ish sizes: ignore sub-pixel churn
        lock (_bakeLock)   // reentrant: FontChanged's peak-face re-request takes it again
        {
            FaceRequest old = _requests[0];
            _requests[0] = old with { Px = px };
            if (Rebake()) { _bakedPx = px; FontChanged?.Invoke(); } else _requests[0] = old;
        }
    }

    /// <summary>The <see cref="FontRegistry"/> name <c>default</c> is bound to for the UI (<c>ui.typeface</c>); "default"
    /// is the load-time binding.</summary>
    private static string _typeface = FontRegistry.DefaultName;

    /// <summary>Rebind <c>default</c> to <paramref name="name"/> and re-bake every default-typeface face on its
    /// bytes (cold, on a settings change). The icon rows still come from the embedded font. A font stbtt refuses
    /// keeps the old sheet AND the old binding, so the knob may say a name the screen does not show -- logged.</summary>
    public static void SetTypeface(string name)
    {
        if (_sheet == null || _log == null || _requests.Count == 0) return;
        lock (_bakeLock)
        {
            if (name.Equals(_typeface, StringComparison.OrdinalIgnoreCase)) return;
            string prev = _typeface;
            byte[] prevFont = _requests[0].Font;
            if (!FontRegistry.Bind(name)) { _log.Info($"TextRig: typeface {name} unknown or refused; keeping {prev}"); return; }
            byte[]? bytes = FontRegistry.Bytes(DefaultFace);
            if (bytes == null) return;
            _typeface = name;
            if (ReferenceEquals(bytes, prevFont)) return;
            Retarget(bytes);
            if (Rebake()) { _log.Info($"TextRig: typeface -> {name}"); FontChanged?.Invoke(); return; }
            _log.Info($"TextRig: typeface {name} refused by the bake; keeping {prev}");
            FontRegistry.Bind(prev); _typeface = prev;
            Retarget(prevFont);
        }
    }

    /// <summary>Point every request of the default typeface at <paramref name="font"/> (the icon-only ones are the embedded font's).</summary>
    private static void Retarget(byte[] font)
    {
        for (int i = 0; i < _requests.Count; i++)
            if (_requests[i].Typeface == DefaultFace) _requests[i] = _requests[i] with { Font = font };
    }

    // ---- portrait pass ------------------------------------------------------------------

    /// <summary>resref -&gt; the texture a UI draw samples (<see cref="Own"/>), or 0 if not yet uploaded / unresolvable.
    /// Re-fetches the engine <c>CAurTexture*</c> from the resman EVERY frame and NEVER caches it: the engine frees and
    /// reloads its portrait textures on area transitions and module unloads, so any stored pointer dangles.</summary>
    internal static int ResolveTid(string resref)
    {
        // A non-null texture whose TID is still 0 is simply "not uploaded yet"; it appears once ready.
        return Own(CX.Framework.AurTex.Tid(ResolveTexture(resref)));
    }

    /// <summary>NWN portrait resrefs are a base plus one size letter -- the suffix table in
    /// <c>CNWPortrait::GetPortrait</c> (nm <c>0x81e910</c>) is <c>{ "", "T", "S", "M", "L", "H" }</c>. Medium is
    /// 64x100; tiny is 16x25, which is what the engine hands the chat log (<c>HandleServerToPlayerChat</c> calls
    /// virtual <c>+0x1d8</c> with <c>edx = 1</c>, @nm <c>0x75be6e</c>) and why those portraits looked blurry.</summary>
    private const byte SizeMedium = (byte)'m';

    /// <summary>If <paramref name="name"/> (a NUL-terminated resref of <paramref name="len"/> bytes including the
    /// NUL) ends in a size letter SMALLER than medium, rewrite it to medium in place. Pure and allocation-free; the
    /// caller must put the original letter back if the medium variant turns out not to exist.</summary>
    /// <returns>The index that was rewritten, or -1 if nothing was.</returns>
    private static int PreferMedium(Span<byte> name, int len)
    {
        int last = len - 2;                                     // the byte before the NUL
        if (last < 1) return -1;                                // "" or a one-letter name has no base to keep
        byte c = name[last];
        if (c != (byte)'t' && c != (byte)'T' && c != (byte)'s' && c != (byte)'S') return -1;
        name[last] = SizeMedium;
        return last;
    }

    /// <summary>Portrait texture, preferring the MEDIUM size variant of the resref and falling back to the resref as
    /// given: the engine hands the chat log a tiny (<c>_t</c>) portrait and both portrait paths magnify it. Nothing is
    /// cached or allocated -- the rewrite happens in the stack buffer the resref is marshalled into.</summary>
    internal static int ResolvePortraitTid(string resref)
    {
        if (resref.Length == 0) return 0;
        Span<byte> name = stackalloc byte[17];
        int len = FillCName(resref, name);
        int at = PreferMedium(name, len);
        if (at >= 0)
        {
            int tid;
            fixed (byte* p = name) tid = CX.Framework.AurTex.Tid(_aurGet(p, IntPtr.Zero, null, null, 0, 0));
            if (tid != 0) return Own(tid);
            FillCName(resref, name);                            // no medium variant: ask for exactly what we were given
        }
        fixed (byte* q = name) return Own(CX.Framework.AurTex.Tid(_aurGet(q, IntPtr.Zero, null, null, 0, 0)));
    }

    /// <summary>Self-check for the resref -&gt; C-string marshalling that feeds AurTextureGet: truncation at 16
    /// bytes and NUL-termination (a missing NUL would walk past the name into stack garbage), the portrait
    /// size-suffix preference, and the bindless record's layout against std430 offsets worked by hand. Headless.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        Span<byte> buf = stackalloc byte[17];
        bool ok = true;
        ok &= FillCName("0123456789abcdef", buf) == 17 && buf[16] == 0 && buf[0] == (byte)'0' && buf[15] == (byte)'f';
        ok &= FillCName("0123456789abcdefEXTRA", buf) == 17 && buf[16] == 0 && buf[15] == (byte)'f';   // over-length truncates
        ok &= FillCName("po_hu_0", buf) == 8 && buf[7] == 0 && buf[0] == (byte)'p';                    // short name NUL-terminates in place
        ok &= FillCName("", buf) == 1 && buf[0] == 0;                                                  // empty -> lone NUL

        int n = FillCName("po_hu_m_99_t", buf);
        ok &= PreferMedium(buf, n) == n - 2 && buf[n - 2] == (byte)'m' && buf[n - 1] == 0;   // tiny -> medium
        n = FillCName("po_hu_m_99_s", buf);
        ok &= PreferMedium(buf, n) == n - 2 && buf[n - 2] == (byte)'m';                      // small -> medium
        n = FillCName("po_hu_m_99_m", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[n - 2] == (byte)'m';                           // already medium: untouched
        n = FillCName("po_hu_m_99_l", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[n - 2] == (byte)'l';                           // larger: never downsized
        n = FillCName("po_hu_m_99_h", buf);
        ok &= PreferMedium(buf, n) < 0;
        n = FillCName("", buf);
        ok &= PreferMedium(buf, n) < 0;                                                      // empty name is left alone
        n = FillCName("t", buf);
        ok &= PreferMedium(buf, n) < 0 && buf[0] == (byte)'t';                               // a lone letter is a base, not a suffix
        n = FillCName("0123456789abcdet", buf);
        ok &= PreferMedium(buf, n) == 15 && buf[15] == (byte)'m' && buf[16] == 0;            // a full-length resref rewrites in place

        // std430's offsets for the UiRec block BatchVs declares, worked by hand: uvec2 0, uint 8 and 12, vec2 16, two
        // floats 24 and 28, vec4 32 and 48, vec2 64 and 72, padded to 80.
        bool rec = sizeof(UiRec) == 80 && Marshal.OffsetOf<UiRec>(nameof(UiRec.OffX)) == 16
                   && Marshal.OffsetOf<UiRec>(nameof(UiRec.Clip)) == 32 && Marshal.OffsetOf<UiRec>(nameof(UiRec.Occl)) == 48
                   && Marshal.OffsetOf<UiRec>(nameof(UiRec.Ax)) == 64;
        ok &= rec;

        log.Info(ok ? "TextRig.SelfCheck: passed"
                    : $"TextRig.SelfCheck: FAILED (resref C-string marshalling / portrait size preference{(rec ? "" : " / the UiRec layout")})");
    }

    private static IntPtr ResolveTexture(string resref)
    {
        Span<byte> name = stackalloc byte[17];   // 16 resref bytes + NUL
        FillCName(resref, name);
        fixed (byte* p = name) return _aurGet(p, IntPtr.Zero, null, null, 0, 0);
    }

    /// <summary>Write <paramref name="resref"/> into <paramref name="dest"/> (>= 17 bytes) as a
    /// null-terminated C string: copy up to 16 ASCII bytes, then a NUL. The engine resref is null-padded
    /// ASCII, max 16 bytes. Returns bytes written including the NUL.</summary>
    private static int FillCName(string resref, Span<byte> dest)
    {
        int n = 0;
        foreach (char c in resref) { if (n >= 16) break; dest[n++] = (byte)c; }
        dest[n] = 0;
        return n + 1;
    }

    /// <summary>Fraction of a portrait texture that is real image, from the engine's own ConvertPortrait (content =
    /// 1/1.28 of a pow2-padded height; the rest is black at the image bottom). Two texels tighter than the nominal
    /// 100/128, because linear filtering samples across the content/pad boundary; a fraction, so it holds at every
    /// resolution. ponytail: a portrait ever cropped or showing a black band, this one const is the knob (1.0 = raw).</summary>
    private const float PortraitContentV = 98f / 128f;   // 0.765625, nominal 0.78125 less two texel rows

    /// <summary>Two triangles for a portrait quad. The engine uploads portraits GL-bottom-origin, so the
    /// image arrives upside down: map the quad top to V=1 (image top) and the bottom to the content floor,
    /// which flips it upright AND crops the pow2 black pad band. Full U (no horizontal padding). y-down like
    /// the text ortho. <paramref name="residual"/> slides it with the body; <paramref name="alpha"/> fades it.</summary>
    internal static void FillQuad(ref QuadVerts v, PortraitQuad q, float residual, float alpha)
    {
        float x0 = q.X, y0 = q.Y + residual, x1 = q.X + q.W, y1 = q.Y + q.H + residual;
        float vTop = 1f, vBot = 1f - PortraitContentV;
        v[0] = Vtx(x0, y0, 0, vTop, alpha); v[1] = Vtx(x1, y0, 1, vTop, alpha); v[2] = Vtx(x1, y1, 1, vBot, alpha);
        v[3] = Vtx(x0, y0, 0, vTop, alpha); v[4] = Vtx(x1, y1, 1, vBot, alpha); v[5] = Vtx(x0, y1, 0, vBot, alpha);
    }

    private static ShadedVertex Vtx(float x, float y, float u, float w, float alpha)
        => new() { X = x, Y = y, U = u, V = w, R = 1f, G = 1f, B = 1f, A = alpha };

    /// <summary>A quad's six vertices as the float stream a <see cref="UiDrawList"/> takes.</summary>
    private static ReadOnlySpan<float> Floats6(in QuadVerts v) => MemoryMarshal.Cast<QuadVerts, float>(new ReadOnlySpan<QuadVerts>(in v));

    /// <summary>Draw a caller-built vertex batch (x,y,u,v,r,g,b,a) over the glyph atlas.</summary>
    /// <param name="offX">Vertex translation in px via <c>uOffset</c>; 0 for an un-scrolled batch.</param>
    /// <param name="clip">Scissor to (cx, cy, cw, ch), screen px y-down from the top-left.</param>
    public static void DrawBatch(float[] verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (verts == null) return;
        DrawBatch(verts.AsSpan(), count, alpha, offX, offY, clip, cx, cy, cw, ch);
    }

    /// <summary><see cref="DrawBatch(float[], int, float, float, float, bool, int, int, int, int)"/> over a span: a
    /// per-frame owner records straight from its <see cref="VertexBatch.AsSpan"/>.</summary>
    public static void DrawBatch(ReadOnlySpan<float> verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (count == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        Add(new UiCmd { Kind = UiDrawKind.Text, Alpha = alpha < 1f ? alpha : 1f, OffX = offX, OffY = offY,
                        Clip = clip, Cx = cx, Cy = cy, Cw = cw, Ch = ch }, verts, count);
    }

    /// <summary>Draw a single speaker portrait quad (resref -> its texture, never cached -- see <see cref="ResolveTid"/>).
    /// No-op until the engine has uploaded the texture or when the resref is empty; flipped upright and pad-cropped.</summary>
    public static void DrawPortrait(string resref, float x, float y, float w, float h, float alpha = 1f,
                                    bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (string.IsNullOrEmpty(resref) || _sheet == null || _log == null || alpha <= 0f) return;
        if (_aurGet == null || !CX.Framework.AurTex.Ready) return;
        int tid = ResolvePortraitTid(resref);
        if (tid == 0) return;
        QuadVerts verts = default;
        FillQuad(ref verts, new PortraitQuad(x, y, w, h, resref, default), 0f, alpha < 1f ? alpha : 1f);
        DrawPortraitQuad(tid, in verts, true, clip, cx, cy, cw, ch);
    }

    /// <summary>A portrait quad <see cref="FillQuad"/> built over texture <paramref name="tid"/> (0 draws nothing).
    /// <paramref name="sampler"/> reads it through the linear-clamp sampler instead of its own mip filter.</summary>
    internal static void DrawPortraitQuad(int tid, in QuadVerts v, bool sampler,
                                          bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (tid == 0 || ViewportW <= 0) return;
        Add(new UiCmd { Kind = UiDrawKind.Portrait, Tex = (uint)tid, Sampler = sampler, Alpha = 1f,
                        Clip = clip, Cx = cx, Cy = cy, Cw = cw, Ch = ch }, Floats6(in v), 6);
    }

    /// <summary>Portrait content aspect (width:height); the engine crops portraits taller-than-wide. Public so
    /// callers size a portrait rect to match the texture and avoid distortion.</summary>
    public const float PortraitAspect = 0.64f;

    /// <summary>One PLT icon layer: <paramref name="tid"/> is the PAIR texture, <paramref name="pal"/> the master
    /// palette, <paramref name="rows"/> this item's master-palette row per layer. Same quad and clip as
    /// <see cref="DrawIconTid"/>; only the resolve differs.</summary>
    public static void DrawIconPlt(int tid, uint pal, ReadOnlySpan<int> rows, float x, float y, float w, float h,
                                   float alpha = 1f, float u1 = 1f, float v0 = 0f,
                                   bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (tid == 0 || pal == 0 || rows.Length < PltRows || _log == null || alpha <= 0f || ViewportW <= 0) return;
        float a = alpha < 1f ? alpha : 1f, x1 = x + w, y1 = y + h;
        QuadVerts v = default;
        v[0] = Vtx(x, y, 0, 1, a); v[1] = Vtx(x1, y, u1, 1, a); v[2] = Vtx(x1, y1, u1, v0, a);
        v[3] = Vtx(x, y, 0, 1, a); v[4] = Vtx(x1, y1, u1, v0, a); v[5] = Vtx(x, y1, 0, v0, a);
        Add(new UiCmd { Kind = UiDrawKind.IconPlt, Tex = (uint)tid, Pal = pal, Alpha = 1f, U1 = u1, V0 = v0,
                        Clip = clip, Cx = cx, Cy = cy, Cw = cw, Ch = ch }, Floats6(in v), 6, rows[..PltRows]);
    }

    /// <summary>Draw one engine texture by resref as an icon quad: full UV (icons are square pow2 textures with no
    /// pad), flipped upright like <see cref="FillQuad"/>, WITH the texture's alpha. The resref is looked up as given
    /// (no portrait size-suffix rewrite) and never cached. No-op until the engine has uploaded the texture.</summary>
    public static void DrawIcon(string resref, float x, float y, float w, float h, float alpha = 1f)
    {
        if (string.IsNullOrEmpty(resref) || _aurGet == null || !CX.Framework.AurTex.Ready) return;
        DrawIconTid(ResolveTid(resref), x, y, w, h, alpha);
    }

    /// <summary>The part of texture <paramref name="tid"/> from <paramref name="u0"/> to <paramref name="u1"/> across and from
    /// <paramref name="vTop"/> down to <paramref name="vBottom"/>, in the texture's own bottom-up coordinates, stretched to the box.</summary>
    public static void DrawIconRegion(int tid, float x, float y, float w, float h, float alpha, float u0, float vTop, float u1, float vBottom)
    {
        if (tid == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        float a = alpha < 1f ? alpha : 1f, x1 = x + w, y1 = y + h;
        QuadVerts v = default;
        v[0] = Vtx(x, y, u0, vTop, a); v[1] = Vtx(x1, y, u1, vTop, a); v[2] = Vtx(x1, y1, u1, vBottom, a);
        v[3] = Vtx(x, y, u0, vTop, a); v[4] = Vtx(x1, y1, u1, vBottom, a); v[5] = Vtx(x, y1, u0, vBottom, a);
        Add(new UiCmd { Kind = UiDrawKind.Icon, Tex = (uint)tid, Sampler = true, Alpha = 1f }, Floats6(in v), 6);
    }

    /// <summary>The <see cref="DrawIcon"/> body for a live GL texture id the caller already holds; 0 draws nothing.
    /// <paramref name="u1"/> / <paramref name="v0"/> crop a padded sheet to its top-left content; <paramref name="clip"/> scissors
    /// to (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cw"/>, <paramref name="ch"/>), y-down px.
    /// <paramref name="flipV"/>: the texture's first row is its top (a model view's).</summary>
    public static void DrawIconTid(int tid, float x, float y, float w, float h, float alpha = 1f, float u1 = 1f, float v0 = 0f,
                                   bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0, bool flipV = false)
    {
        if (tid == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        float a = alpha < 1f ? alpha : 1f, x1 = x + w, y1 = y + h;
        float vt = flipV ? 0f : 1f, vb = flipV ? 1f : v0;
        QuadVerts v = default;
        v[0] = Vtx(x, y, 0, vt, a); v[1] = Vtx(x1, y, u1, vt, a); v[2] = Vtx(x1, y1, u1, vb, a);
        v[3] = Vtx(x, y, 0, vt, a); v[4] = Vtx(x1, y1, u1, vb, a); v[5] = Vtx(x, y1, 0, vb, a);
        Add(new UiCmd { Kind = UiDrawKind.Icon, Tex = (uint)tid, Sampler = true, Alpha = 1f,
                        Clip = clip, Cx = cx, Cy = cy, Cw = cw, Ch = ch }, Floats6(in v), 6);
    }
}
