using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

using CX.Services.Chat.Core.Model;

namespace CX.Ui;

/// <summary>
/// The UI's GL RIG, not a renderer: it owns the shared GL objects -- a stb_truetype glyph atlas baked once at
/// init (cold), a GL 3.3-core program and one VAO/VBO pair -- and exposes the primitives every UI service draws
/// through: <see cref="DrawBatch"/> for a caller-built vertex batch, <see cref="DrawPortrait"/> for an
/// engine-resident texture quad. The context is core, so everything is shader-driven; nothing here decides WHAT to draw.
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
        _texUp = false;   // the hot Draw uploads the new sheet bitmap on the next frame
        AtlasEpoch++;     // cached-geometry consumers rebuild: their glyph UVs went stale
        Rebaked?.Invoke();
        return true;
    }

    /// <summary>Injected log (set in <see cref="Init"/>); the chat draw path reads it via this.</summary>
    internal static ExtenderLog? Log => _log;

    /// <summary>Bumped on every atlas re-bake (<see cref="SetFont"/>), so a cached-geometry consumer
    /// (the attribution overlay) can tell its glyph UVs went stale and rebuild.</summary>
    internal static int AtlasEpoch { get; private set; }

    /// <summary>The frame's viewport (px), sampled once per present by the frame host (<see cref="SampleViewport"/>)
    /// and read by every UI draw and by cold consumers (the settings window clamps itself on-screen with these).
    /// 0 until the first frame draws.</summary>
    public static volatile int ViewportW, ViewportH;

    /// <summary>Sample the live GL viewport into <see cref="ViewportW"/>/<see cref="ViewportH"/>. The frame host
    /// calls it once per present, before the window roll-call, and every draw below reads the published pair
    /// instead of re-querying the driver. A no-op off-device, where the pair stays 0.</summary>
    public static void SampleViewport()
    {
        if (_log == null || !Gl.Init(_log) || Gl.GetIntegerv == null) return;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        ViewportW = vp[2]; ViewportH = vp[3];
    }

    public static bool Ready => _sheet != null;

    // GL objects (created lazily on the first drawn frame; no context exists at init). _vbo is the shared
    // draw rig ChatRenderer's Draw/DrawOverlay upload through; internal so it can bind it directly.
    private static uint _prog, _vao, _tex;
    internal static uint _vbo;
    // Batch rig: DrawBatch's own VAO/VBO so its uploads never clobber the shared _vbo (keeps Draw's cache live).
    private static uint _bvao, _bvbo;
    private static bool _bvaoUp;
    private static int _uProj, _uTex, _uAlpha, _uOffset;
    /// <summary>Whole-batch alpha multiplier for the text program, applied via <c>uAlpha</c>. Default 1 (chat's
    /// <c>ChatRenderer.Draw</c> never touches it); <see cref="DrawBatch"/> sets it for the dialog fade and restores 1.</summary>
    private static float _textAlpha = 1f;
    /// <summary>Vertex translation for the text program, applied via <c>uOffset</c>. Default (0,0); the dialog's
    /// clipped content pass sets it to the scroll/body offset and restores 0 (chat's <c>ChatRenderer.Draw</c> is untouched).</summary>
    private static float _texOffX, _texOffY;
    // _texUp is volatile: a cold SetFont clears it to force the hot Draw to re-upload the re-baked atlas.
    // _texUp/_vaoUp are internal so ChatRenderer's Draw/DrawOverlay can gate the same shared rig.
    internal static volatile bool _texUp;
    internal static bool _vaoUp;
    private static bool _progUp;

    // Portrait pass: its own RGBA program + VAO/VBO (a second buffer so the text VBO's
    // upload-if-changed cache stays valid). The engine CAurTexture* is NEVER cached -- see ResolveTid.
    // internal: ChatRenderer's multi-portrait sub-pass drives this same rig.
    internal static uint _progRgb, _pvao, _pvbo;
    internal static int _uProjRgb, _uTexRgb;
    private static bool _progRgbUp;
    internal static bool _pvaoUp;
    /// <summary>Sampler object the portrait pass reads the engine's texture through -- see
    /// <see cref="EnsurePortraitSampler"/>. Same lifetime as <see cref="_pvao"/>: a GL name in the ENGINE's
    /// context, created once and never deleted.</summary>
    internal static uint _psamp;

    /// <summary>Resolved once: the portrait path runs per drawn plate, so the lazy resolve's compare does not
    /// belong in it. Null when the client does not export the resman getter.</summary>
    internal static readonly AurTextureGetFn _aurGet = EngineFn.AurTextureGet;

    /// <summary>Cold init: load the font, bake the atlas (incl. its solid-white chrome texel). GL state
    /// is driven directly at draw time (snapshot/restore), so nothing to resolve here.</summary>
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

    internal static void BindText(in Matrix4x4 proj)
    {
        Gl.UseProgram(_prog);
        fixed (Matrix4x4* pm = &proj) Gl.UniformMatrix4fv(_uProj, 1, (byte)Gl.FALSE, (float*)pm);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);   // already on TEXTURE0
        Gl.Uniform1i(_uTex, 0);
        Gl.Uniform1f(_uAlpha, _textAlpha);     // 1 for chat; the dialog fade overrides via DrawBatch
        Gl.Uniform2f(_uOffset, _texOffX, _texOffY);   // (0,0) for chat; the dialog scroll pass overrides
        Gl.BindVertexArray(_vao);
    }

    // ---- portrait pass ------------------------------------------------------------------

    /// <summary>resref -&gt; live GL texture id, or 0 if not yet uploaded / unresolvable. Re-fetches the engine
    /// <c>CAurTexture*</c> from the resman EVERY frame and NEVER caches it: the engine frees and reloads its
    /// portrait textures on area transitions and module unloads, so any stored pointer dangles and crashes when
    /// bound. AurTextureGet returns the current live (borrowed) pointer, the only way to never hold a stale one.</summary>
    internal static int ResolveTid(string resref)
    {
        IntPtr tex = ResolveTexture(resref);
        // A non-null texture whose TID is still 0 is simply "not uploaded yet"; a portrait still streaming
        // in appears once ready. AurTextureGet only returns Zero for an empty name (guarded upstream:
        // hasPortrait requires a non-empty resref), so the Zero branch is effectively unreachable.
        return CX.Framework.AurTex.Tid(tex);
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

    /// <summary>Portrait TID, preferring the MEDIUM size variant of the resref and falling back to the resref as
    /// given: the engine hands the chat log a tiny (<c>_t</c>) portrait and both portrait paths magnify it, so the
    /// preference is what fixes the blur and the fallback keeps a custom portrait shipping no <c>_m</c>. Nothing is
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
            if (tid != 0) return tid;
            FillCName(resref, name);                            // no medium variant: ask for exactly what we were given
        }
        fixed (byte* q = name) return CX.Framework.AurTex.Tid(_aurGet(q, IntPtr.Zero, null, null, 0, 0));
    }

    /// <summary>Self-check for the resref -&gt; C-string marshalling that feeds AurTextureGet: truncation at 16
    /// bytes and NUL-termination (a missing NUL would walk past the name into stack garbage), plus the portrait
    /// size-suffix preference. Headless.</summary>
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

        log.Info(ok ? "TextRig.SelfCheck: passed"
                    : "TextRig.SelfCheck: FAILED (resref C-string marshalling / portrait size preference)");
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

    private const string FSrcRgb =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = vec4(texture(uTex, vUV).rgb, vA); }\n";

    internal static bool EnsurePortraitProgram()
    {
        if (_progRgbUp) return _progRgb != 0;
        _progRgbUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcRgb);
        if (vs == 0 || fs == 0) return false;
        _progRgb = Gl.CreateProgram();
        Gl.AttachShader(_progRgb, vs); Gl.AttachShader(_progRgb, fs); Gl.LinkProgram(_progRgb);
        int ok; Gl.GetProgramiv(_progRgb, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: portrait program link failed"); _progRgb = 0; return false; }
        _uProjRgb = UniformOf(_progRgb, "uProj"); _uTexRgb = UniformOf(_progRgb, "uTex");
        _log?.Info("TextRig: portrait shader program ready");
        return true;
    }

    /// <summary>Lazily create the sampler both portrait paths draw through: LINEAR for the 2.8x minification a medium
    /// (64x100) source in a ~36px plate is, CLAMP because the crop UVs stop short of the pow2 padding, un-mipmapped
    /// because the portrait may carry level 0 only, where a <c>LINEAR_MIPMAP_*</c> min filter reads black.</summary>
    /// <returns>false when the loader gave no sampler entry points; callers then inherit the texture's own filter.</returns>
    internal static bool EnsurePortraitSampler()
    {
        if (_psamp != 0) return true;
        if (Gl.BindSampler == null) return false;
        // A sampler object, never glTexParameteri/glGenerateMipmap: those mutate a texture the engine owns and
        // caches, and the respecify is the AMD/Mesa mip trap.
        // ponytail: bilinear still skips texels at 2.8x; the ceiling is a mip chain on a texture we own, or a
        // supersampled fetch in FSrcRgb.
        _psamp = Gl.LinearClampSampler();
        return _psamp != 0;
    }

    internal static void SetupPortraitVao()
    {
        uint a; Gl.GenVertexArrays(1, &a); _pvao = a;
        uint vb; Gl.GenBuffers(1, &vb); _pvbo = vb;
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        int stride = Floats * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 3, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(3, 1, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(7 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.BindVertexArray(0);
        _pvaoUp = true;
    }

    /// <summary>Column-major ortho for glOrtho(0,w,h,0,-1,1): top-left origin, y grows downward.</summary>
    internal static Matrix4x4 Ortho(float w, float h)
        => new(2f / w, 0f, 0f, 0f,
               0f, -2f / h, 0f, 0f,
               0f, 0f, -1f, 0f,
               -1f, 1f, 0f, 1f);

    private const string VSrc =
        "#version 330 core\nlayout(location=0) in vec2 aPos;\nlayout(location=1) in vec2 aUV;\n" +
        "layout(location=2) in vec3 aCol;\nlayout(location=3) in float aA;\nuniform mat4 uProj;\nuniform vec2 uOffset;\n" +
        "out vec2 vUV;\nout vec3 vCol;\nout float vA;\n" +
        "void main(){ gl_Position = uProj*vec4(aPos + uOffset,0.0,1.0); vUV = aUV; vCol = aCol; vA = aA; }\n";
    private const string FSrc =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nuniform float uAlpha;\nout vec4 o;\n" +
        "void main(){ float a = texture(uTex, vUV).r * vA * uAlpha; o = vec4(vCol, a); }\n";

    internal static bool EnsureProgram()
    {
        if (_progUp) return _prog != 0;
        _progUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrc);
        if (vs == 0 || fs == 0) return false;
        _prog = Gl.CreateProgram();
        Gl.AttachShader(_prog, vs); Gl.AttachShader(_prog, fs); Gl.LinkProgram(_prog);
        int ok; Gl.GetProgramiv(_prog, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: program link failed"); _prog = 0; return false; }
        _uProj = Uniform("uProj"); _uTex = Uniform("uTex"); _uAlpha = Uniform("uAlpha"); _uOffset = Uniform("uOffset");
        _log?.Info("TextRig: shader program ready");
        return true;
    }

    private static uint Compile(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        fixed (byte* sp = bytes)
        {
            byte* pp = sp; int len = bytes.Length;
            Gl.ShaderSource(sh, 1, &pp, &len);
        }
        Gl.CompileShader(sh);
        int ok; Gl.GetShaderiv(sh, Gl.COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* buf = stackalloc byte[512]; int n = 0;
            Gl.GetShaderInfoLog(sh, 512, &n, buf);
            _log?.Info($"TextRig: shader compile failed: {Marshal.PtrToStringAnsi((IntPtr)buf, n)}");
            return 0;
        }
        return sh;
    }

    private static int Uniform(string name) => UniformOf(_prog, name);

    private static int UniformOf(uint prog, string name)
    {
        Span<byte> b = stackalloc byte[name.Length + 1];
        for (int i = 0; i < name.Length; i++) b[i] = (byte)name[i];
        b[name.Length] = 0;
        fixed (byte* p = b) return Gl.GetUniformLocation(prog, p);
    }

    internal static void UploadTexture()
    {
        AtlasSheet? a = _sheet;   // snapshot: a cold re-bake swap must not pair one sheet's Pixels with another's side
        if (a == null) return;
        if (_tex == 0) { uint t; Gl.GenTextures(1, &t); _tex = t; }   // create once; a re-bake respecifies the same id (no leak)
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _tex);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, a.Mips.Length);   // must equal the uploaded chain, or the texture is incomplete and samples black
        fixed (byte* px = a.Pixels)
            Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.R8, a.Side, a.Side, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        for (int l = 1; l < AtlasSheet.MipLevels && l - 1 < a.Mips.Length; l++)
            fixed (byte* px = a.Mips[l - 1])
                Gl.TexImage2D(Gl.TEXTURE_2D, l, (int)Gl.R8, a.Side >> l, a.Side >> l, 0, Gl.RED, Gl.UNSIGNED_BYTE, px);
        // back to the GL default, in the ENGINE's context, or every engine texture upload after this
        // one reads its rows one byte at a time. GLRender::CreateTexture @0x4ca2b0 -- the CAurTexture upload
        // path, and the one an area transition runs for every texture it loads -- calls glTexImage2D with no
        // pixel-store call of its own, and GLRender::DirectCreateTexture @0x4c9cc0 SKIPS both its set and its
        // restore when the caller asks for 4 (`if (align != 4)`). Both assume the default is still in place.
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        _texUp = true;
        _log?.Info($"TextRig: atlas uploaded as GL tex {_tex} ({a.Side}², {a.FaceCount} face(s), {AtlasSheet.MipLevels} levels, {a.Bytes / 1024} KB R8)");
    }

    /// <summary>Create a VAO + its own VBO wired to the text vertex layout (pos2, uv2, rgb3, alpha1).
    /// A VAO captures the array-buffer bound at attrib-pointer time, so each rig owns its buffer: the shared
    /// pair backs <c>ChatRenderer.Draw</c>'s settled-frame cache; the batch pair lets <see cref="DrawBatch"/> upload
    /// without clobbering it (chat keeps its cache while a dialog modal is open).</summary>
    private static uint MakeVao(out uint vbo)
    {
        uint a; Gl.GenVertexArrays(1, &a);
        uint vb; Gl.GenBuffers(1, &vb); vbo = vb;
        Gl.BindVertexArray(a);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, vb);
        int stride = Floats * sizeof(float);
        Gl.VertexAttribPointer(0, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)0);
        Gl.EnableVertexAttribArray(0);
        Gl.VertexAttribPointer(1, 2, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(2 * sizeof(float)));
        Gl.EnableVertexAttribArray(1);
        Gl.VertexAttribPointer(2, 3, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(4 * sizeof(float)));
        Gl.EnableVertexAttribArray(2);
        Gl.VertexAttribPointer(3, 1, Gl.FLOAT, (byte)Gl.FALSE, stride, (void*)(7 * sizeof(float)));
        Gl.EnableVertexAttribArray(3);
        Gl.BindVertexArray(0);
        return a;
    }

    internal static void SetupVao() { _vao = MakeVao(out _vbo); _vaoUp = true; }
    private static void SetupBatchVao() { _bvao = MakeVao(out _bvbo); _bvaoUp = true; }

    /// <summary>Upload <paramref name="count"/> floats from <paramref name="b"/> to the shared VBO.
    /// Count is the active vert data (Rf.Verts.Length), not the buffer length (which may be larger after
    /// scratch recycling).</summary>
    internal static void UploadBuffer(float[] b, int count)
    {
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        fixed (float* p = b) Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(count * sizeof(float)), p, Gl.DYNAMIC_DRAW);
    }

    /// <summary>Draw a caller-built vertex batch (x,y,u,v,r,g,b,a) through the shared text program, atlas and VAO,
    /// uploading to the batch rig's own VBO so chat's and its settled-frame cache stay intact. Full preamble, no snapshot.</summary>
    /// <param name="offX">Vertex translation in px via <c>uOffset</c>; 0 for an un-scrolled batch.</param>
    /// <param name="clip">Scissor to (cx, cy, cw, ch), screen px y-down from the top-left; the box is saved + restored.</param>
    public static void DrawBatch(float[] verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (verts == null) return;
        DrawBatch(verts.AsSpan(), count, alpha, offX, offY, clip, cx, cy, cw, ch);
    }

    /// <summary><see cref="DrawBatch(float[], int, float, float, float, bool, int, int, int, int)"/> over a span: a
    /// per-frame owner uploads straight from its <see cref="VertexBatch.AsSpan"/> with no copy.</summary>
    public static void DrawBatch(ReadOnlySpan<float> verts, int count, float alpha = 1f, float offX = 0f, float offY = 0f,
                                 bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (count == 0 || _sheet == null || _log == null || alpha <= 0f || ViewportW <= 0) return;
        if (!Gl.Init(_log)) return;
        _textAlpha = alpha < 1f ? alpha : 1f;   // read by BindText; restored to 1 before returning
        _texOffX = offX; _texOffY = offY;       // ditto -- restored to (0,0) before returning

        // no state snapshot -- UiHostService resyncs the engine on hand-back, and every draw below
        // sets its own full preamble.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (EnsureProgram())
        {
            if (!_texUp) UploadTexture();
            if (!_bvaoUp) SetupBatchVao();

            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            int vw = ViewportW, vh = ViewportH;
            Matrix4x4 m = Ortho(vw, vh);

            // GL scissor is bottom-left origin; our rects are y-down top-left, so flip Y.
            if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, vh - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
            else Gl.Disable(Gl.SCISSOR_TEST);

            BindText(m);
            Gl.BindVertexArray(_bvao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _bvbo);
            fixed (float* p = verts)
                Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(count * Floats * sizeof(float)), p, Gl.DYNAMIC_DRAW);
            Gl.DrawArrays(Gl.TRIANGLES, 0, count);
            // No settled-cache invalidation: the batch rig owns _bvbo, so chat's _vbo (and its cache) is untouched.

        }

        _textAlpha = 1f; _texOffX = 0f; _texOffY = 0f;   // leave the shared program neutral for chat's Draw
    }

    /// <summary>Draw a single speaker portrait quad (resref -> engine-resident GL texture, never cached -- see
    /// <see cref="ResolveTid"/>) through the shared RGBA portrait program. Used by <c>DialogService</c> to put
    /// a portrait in its overlay. No-op until the engine has uploaded the texture (TID 0) or when the resref is
    /// empty. Sets its own full GL preamble, taking no state snapshot; the image is flipped upright and pow2-pad-cropped.</summary>
    public static void DrawPortrait(string resref, float x, float y, float w, float h, float alpha = 1f,
                                    bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (string.IsNullOrEmpty(resref) || _sheet == null || _log == null || alpha <= 0f) return;
        if (_aurGet == null || !CX.Framework.AurTex.Ready) return;
        if (!Gl.Init(_log)) return;
        if (!EnsurePortraitProgram()) return;
        int tid = ResolvePortraitTid(resref);
        if (tid == 0) return;   // not uploaded yet this frame

        // no state snapshot -- UiHostService resyncs the engine on hand-back, and every draw below
        // sets its own full preamble.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (!_pvaoUp) SetupPortraitVao();

        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

        Matrix4x4 m = Ortho(ViewportW, ViewportH);
        if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, ViewportH - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
        else Gl.Disable(Gl.SCISSOR_TEST);

        QuadVerts verts = default;
        FillQuad(ref verts, new PortraitQuad(x, y, w, h, resref, default), 0f, alpha < 1f ? alpha : 1f);

        Gl.UseProgram(_progRgb);
        Gl.UniformMatrix4fv(_uProjRgb, 1, (byte)Gl.FALSE, (float*)&m);
        Gl.Uniform1i(_uTexRgb, 0);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)sizeof(QuadVerts), &verts, Gl.DYNAMIC_DRAW);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        bool samp = EnsurePortraitSampler();
        if (samp) Gl.BindSampler(0, _psamp);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        // Unbind is load-bearing, not tidiness: a sampler left on unit 0 would override the glyph atlas's own filter/wrap state.
        if (samp) Gl.BindSampler(0, 0);
    }

    /// <summary>Portrait content aspect (width:height); the engine crops portraits taller-than-wide. Public so
    /// callers size a portrait rect to match the texture and avoid distortion.</summary>
    public const float PortraitAspect = 0.64f;

    /// <summary>Icon program: the portrait program discards texture alpha and icons carry theirs, so a
    /// second fragment source over the same VAO/VBO/sampler rig.</summary>
    private static uint _progIcon;
    private static int _uProjIcon, _uTexIcon;
    private static bool _progIconUp;
    private const string FSrcIcon =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\nuniform sampler2D uTex;\nout vec4 o;\n" +
        "void main(){ o = texture(uTex, vUV) * vec4(1.0, 1.0, 1.0, vA); }\n";

    /// <summary>The same icon quad for a PLT layer. An item's PLT icon is a PAIR texture (intensity R, layer G) and
    /// its colour is <c>master[rows[layer]][intensity]</c> -- the lookup the engine performs when it composites a
    /// (resref, scheme) texture, which Alloy never builds. texelFetch, because the pair texture must never be
    /// filtered: the mean of two layer indices is a third layer. Bilinear runs over the RESOLVED colours.</summary>
    private const string FSrcIconPlt =
        "#version 330 core\nin vec2 vUV;\nin vec3 vCol;\nin float vA;\n" +
        "uniform sampler2D uTex;\nuniform sampler2D uPal;\nuniform int uRows[10];\nuniform vec2 uCrop;\nout vec4 o;\n" +
        "ivec2 lo, hi;\n" +
        "vec4 tap(ivec2 p){\n" +
        "  vec2 rg = texelFetch(uTex, clamp(p, lo, hi), 0).rg;\n" +
        "  int l = int(rg.g * 255.0 + 0.5); l = l < 9 ? l : 9;\n" +
        "  vec4 c = texelFetch(uPal, ivec2(int(rg.r * 255.0 + 0.5), uRows[l]), 0);\n" +
        "  return vec4(c.rgb * c.a, c.a);\n}\n" +
        "void main(){\n" +
        "  ivec2 d = textureSize(uTex, 0); d = max(d, ivec2(1));\n" +
        "  lo = ivec2(0, int(floor(uCrop.y * float(d.y) + 0.5))); hi = ivec2(max(int(floor(uCrop.x * float(d.x) + 0.5)) - 1, 0), d.y - 1);\n" +
        "  vec2 t = vUV * vec2(d) - 0.5; vec2 f = fract(t); ivec2 p = ivec2(floor(t));\n" +
        "  vec4 c = mix(mix(tap(p), tap(p + ivec2(1,0)), f.x),\n" +
        "               mix(tap(p + ivec2(0,1)), tap(p + ivec2(1,1)), f.x), f.y);\n" +
        "  o = vec4(c.rgb / max(c.a, 0.0001), c.a * vA);\n}\n";

    private static uint _progIconPlt;
    private static bool _progIconPltUp;
    private static int _uProjIconPlt, _uTexIconPlt, _uPalIconPlt, _uRowsIconPlt, _uCropIconPlt;

    private static bool EnsureIconPltProgram()
    {
        if (_progIconPltUp) return _progIconPlt != 0;
        _progIconPltUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcIconPlt);
        if (vs == 0 || fs == 0) return false;
        _progIconPlt = Gl.CreateProgram();
        Gl.AttachShader(_progIconPlt, vs); Gl.AttachShader(_progIconPlt, fs); Gl.LinkProgram(_progIconPlt);
        int ok; Gl.GetProgramiv(_progIconPlt, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: PLT icon program link failed"); _progIconPlt = 0; return false; }
        _uProjIconPlt = UniformOf(_progIconPlt, "uProj"); _uTexIconPlt = UniformOf(_progIconPlt, "uTex");
        _uPalIconPlt = UniformOf(_progIconPlt, "uPal"); _uRowsIconPlt = UniformOf(_progIconPlt, "uRows[0]"); _uCropIconPlt = UniformOf(_progIconPlt, "uCrop");
        _log?.Info("TextRig: PLT icon shader program ready");
        return true;
    }

    /// <summary>One PLT icon layer: <paramref name="tid"/> is the PAIR texture, <paramref name="pal"/> the
    /// master palette, <paramref name="rows"/> this item's master-palette row per layer. Same quad, same
    /// state and same clip as <see cref="DrawIconTid"/>; only the resolve differs.</summary>
    public static void DrawIconPlt(int tid, uint pal, ReadOnlySpan<int> rows, float x, float y, float w, float h,
                                   float alpha = 1f, float u1 = 1f, float v0 = 0f,
                                   bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0)
    {
        if (tid == 0 || pal == 0 || rows.Length < 10 || _log == null || alpha <= 0f) return;
        if (!Gl.Init(_log)) return;
        if (!EnsureIconPltProgram()) return;

        if (!_pvaoUp) SetupPortraitVao();
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, ViewportH - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
        else Gl.Disable(Gl.SCISSOR_TEST);

        Matrix4x4 m = Ortho(ViewportW, ViewportH);
        float a = alpha < 1f ? alpha : 1f, x1 = x + w, y1 = y + h;
        QuadVerts v = default;
        v[0] = Vtx(x, y, 0, 1, a); v[1] = Vtx(x1, y, u1, 1, a); v[2] = Vtx(x1, y1, u1, v0, a);
        v[3] = Vtx(x, y, 0, 1, a); v[4] = Vtx(x1, y1, u1, v0, a); v[5] = Vtx(x, y1, 0, v0, a);

        Gl.UseProgram(_progIconPlt);
        Gl.UniformMatrix4fv(_uProjIconPlt, 1, (byte)Gl.FALSE, (float*)&m);
        Gl.Uniform1i(_uTexIconPlt, 0);
        Gl.Uniform1i(_uPalIconPlt, 1);
        Gl.Uniform2f(_uCropIconPlt, u1, v0);
        fixed (int* r = rows) Gl.Uniform1iv(_uRowsIconPlt, 10, r);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)sizeof(QuadVerts), &v, Gl.DYNAMIC_DRAW);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1);
        Gl.BindTexture(Gl.TEXTURE_2D, pal);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        // No sampler object: both reads are texelFetch, and one left bound on unit 0 would outlive this draw.
        Gl.BindSampler(0, 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, 0); Gl.ActiveTexture(Gl.TEXTURE0);
        if (clip) Gl.Disable(Gl.SCISSOR_TEST);
    }

    private static bool EnsureIconProgram()
    {
        if (_progIconUp) return _progIcon != 0;
        _progIconUp = true;
        uint vs = Compile(Gl.VERTEX_SHADER, VSrc), fs = Compile(Gl.FRAGMENT_SHADER, FSrcIcon);
        if (vs == 0 || fs == 0) return false;
        _progIcon = Gl.CreateProgram();
        Gl.AttachShader(_progIcon, vs); Gl.AttachShader(_progIcon, fs); Gl.LinkProgram(_progIcon);
        int ok; Gl.GetProgramiv(_progIcon, Gl.LINK_STATUS, &ok);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        if (ok == 0) { _log?.Info("TextRig: icon program link failed"); _progIcon = 0; return false; }
        _uProjIcon = UniformOf(_progIcon, "uProj"); _uTexIcon = UniformOf(_progIcon, "uTex");
        _log?.Info("TextRig: icon shader program ready");
        return true;
    }

    /// <summary>Draw one engine texture by resref as an icon quad: full UV (icons are square pow2
    /// textures with no pad), flipped upright like <see cref="FillQuad"/>, WITH the texture's alpha. The resref
    /// is looked up as given (no portrait size-suffix rewrite) and never cached -- see <see cref="ResolveTid"/>.
    /// No-op until the engine has uploaded the texture.</summary>
    public static void DrawIcon(string resref, float x, float y, float w, float h, float alpha = 1f)
    {
        if (string.IsNullOrEmpty(resref) || _aurGet == null || !CX.Framework.AurTex.Ready) return;
        DrawIconTid(ResolveTid(resref), x, y, w, h, alpha);
    }

    /// <summary>The <see cref="DrawIcon"/> body for a live GL texture id the caller already holds; 0 draws nothing.
    /// <paramref name="u1"/> / <paramref name="v0"/> crop a padded sheet to its top-left content; <paramref name="clip"/> scissors
    /// to (<paramref name="cx"/>, <paramref name="cy"/>, <paramref name="cw"/>, <paramref name="ch"/>), y-down px.
    /// <paramref name="flipV"/>: the texture's first row is its top (a model view's). Sets its own GL preamble, taking no state snapshot.</summary>
    public static void DrawIconTid(int tid, float x, float y, float w, float h, float alpha = 1f, float u1 = 1f, float v0 = 0f,
                                   bool clip = false, int cx = 0, int cy = 0, int cw = 0, int ch = 0, bool flipV = false)
    {
        if (tid == 0 || _sheet == null || _log == null || alpha <= 0f) return;
        if (!Gl.Init(_log)) return;
        if (!EnsureIconProgram()) return;

        Gl.ActiveTexture(Gl.TEXTURE0);
        if (!_pvaoUp) SetupPortraitVao();
        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        if (clip) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(cx, ViewportH - (cy + ch), cw < 0 ? 0 : cw, ch < 0 ? 0 : ch); }
        else Gl.Disable(Gl.SCISSOR_TEST);

        Matrix4x4 m = Ortho(ViewportW, ViewportH);

        float a = alpha < 1f ? alpha : 1f, x1 = x + w, y1 = y + h;
        float vt = flipV ? 0f : 1f, vb = flipV ? 1f : v0;
        QuadVerts v = default;
        v[0] = Vtx(x, y, 0, vt, a); v[1] = Vtx(x1, y, u1, vt, a); v[2] = Vtx(x1, y1, u1, vb, a);
        v[3] = Vtx(x, y, 0, vt, a); v[4] = Vtx(x1, y1, u1, vb, a); v[5] = Vtx(x, y1, 0, vb, a);

        Gl.UseProgram(_progIcon);
        Gl.UniformMatrix4fv(_uProjIcon, 1, (byte)Gl.FALSE, (float*)&m);
        Gl.Uniform1i(_uTexIcon, 0);
        Gl.BindVertexArray(_pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, _pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)sizeof(QuadVerts), &v, Gl.DYNAMIC_DRAW);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)tid);
        bool samp = EnsurePortraitSampler();
        if (samp) Gl.BindSampler(0, _psamp);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        // Unbind is load-bearing, not tidiness: a sampler left on unit 0 would override the glyph atlas's own filter/wrap state.
        if (samp) Gl.BindSampler(0, 0);
        if (clip) Gl.Disable(Gl.SCISSOR_TEST);
    }
}
