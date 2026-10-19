using System;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>
/// Every native read the overhead plate/bubble needs, in one place. No UI types, no GL: a service or table calls
/// in here and gets managed values back, and every engine field is read through its typed view.
/// <para>Every <c>CNWCObject*</c> below is BORROWED. Nothing here stores one; callers hold object <b>ids</b> and
/// re-resolve through <see cref="GameObject"/> every frame, treating null as the despawn signal.</para>
/// </summary>
public static unsafe class NameplateNative
{
    // Every engine offset this file reads is an EngineLayout row: the pointer it is handed is always the
    // CNWCObject base subobject, which is what `ObjItemPlated` is measured from.

    /// <summary>Set once by the service so the one-shot layout probe has somewhere to complain.</summary>
    private static ExtenderLog? _log;
    private static bool _probed;

    /// <summary>Set when a layout probe rejects a live instance: the offsets this file reads do not describe
    /// this client. Every read below then answers "nothing" rather than a number off a moved field -- a probe
    /// that only logs is not a stand-down.</summary>
    private static bool _layoutRot;

    /// <summary>Hand this file the log. From <c>NameplateService.Initialize</c>, before any hook fires.</summary>
    public static void UseLog(ExtenderLog log) => _log = log;

    /// <summary>Portrait size byte: the suffix appended to the base resref. <c>CNWPortrait::GetPortrait</c>
    /// (nm <c>0x81e910</c>) indexes <c>{ "", "T", "S", "M", "L", "H" }</c> by it, so 1 is TINY (16x25, what the
    /// engine's own bubble asks for) and 3 is MEDIUM (64x100). Upsizing needs no fallback of ours: that function
    /// walks +1, -1, +2, -2... through <c>CExoResMan::Exists</c>, so a creature with no <c>_m</c> finds <c>_l</c>.</summary>
    private const byte PortraitSizePlate = 3;

    /// <summary>CResRef is 17 bytes (char[16] + a length byte); a 16-byte buffer smashes the stack canary.</summary>
    private const int ResRefScratch = 32;

    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    // Resolved once: the object resolve runs per plated object per frame.
    private static readonly GetGameObjectFn _getGameObject = EngineFn.GetGameObject;
    private static readonly GetModuleFn _getModule = EngineFn.GetModule;
    private static readonly GetViewPortFn _getViewPort = EngineFn.GetViewPort;

    /// <summary>Whether the engine chain we need resolved at all. False headless (run-demo) and under a
    /// client that does not export these -- every accessor below then answers its neutral value.</summary>
    public static bool Ready => _appMgr != IntPtr.Zero && _getGameObject != null;

    /// <summary>The live CClientExoApp*, or Zero. Two loads: the symbol holds a CAppManager*, whose first
    /// field is the CClientExoApp* (the chat/dialog services walk the same chain).</summary>
    private static IntPtr ExoApp()
    {
        if (_appMgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)_appMgr;
        return mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
    }

    /// <summary>A live CNWCObject* for <paramref name="id"/>, or Zero when the object is gone. Zero is the
    /// despawn signal, not an error. Hot: one chain walk and one engine call, no allocation.</summary>
    public static IntPtr GameObject(uint id)
    {
        if (_getGameObject == null) return IntPtr.Zero;
        IntPtr app = ExoApp();
        return app == IntPtr.Zero ? IntPtr.Zero : _getGameObject(app, id);
    }

    public static uint IdOf(IntPtr obj) => obj == IntPtr.Zero ? 0u : new CNWCObject(obj).Id;
    public static byte TypeOf(IntPtr obj) => obj == IntPtr.Zero ? (byte)0 : new CNWCObject(obj).Type;

    /// <summary>Object type bytes we care about.</summary>
    public const byte TypeCreature = 5, TypeItem = 6, TypePlaceable = 9, TypeDoor = 0xa;  // not-an-offset: an object type enum

    /// <summary>Object types whose chat lines the engine prefixes with <c>"[speaker]: "</c>: the <c>0x680</c> mask
    /// (7, 9, 10) tested at <c>HandleServerToPlayerChat</c>'s tail. Creatures get none.</summary>
    public static bool SpeakerPrefixed(byte type) => type < 11 && ((1 << type) & SpeakerPrefixMask) != 0;

    private const int SpeakerPrefixMask = 0x680;  // not-an-offset: an object-type bitmask

    /// <summary>The server has retired this object -- it gets neither element.</summary>
    public static bool Retired(IntPtr obj) => obj != IntPtr.Zero && new CNWCObject(obj).Retired == 1;

    /// <summary>The object's own bubble panel, or a null view when it has none yet.</summary>
    public static CGuiInGamePopUpBubble Bubble(IntPtr obj)
        => obj == IntPtr.Zero ? default : new CNWCObject(obj).Bubble;

    /// <summary>
    /// The engine's own projected anchor for this object's bubble panel, EXACTLY as stored; false when there is no
    /// bubble yet. <c>UpdatePopUpBubble</c> calls <c>Camera::MapToScreen</c> with the flip tail skipped, so these
    /// are the raw GL window transform: <b>pixels, BOTTOM-left origin, y UP</b>, in the CAMERA's viewport rect and
    /// divided by the GUI scale. <see cref="PlateLayout.ToDrawSpace"/> is the only thing that may consume them.
    /// </summary>
    public static bool AnchorRaw(IntPtr obj, out float x, out float y)
    {
        x = 0f; y = 0f;
        CGuiInGamePopUpBubble b = Bubble(obj);
        if (b.IsNull) return false;
        x = b.AnchorX;
        y = b.AnchorY;
        return true;
    }

    /// <summary>Clear the bubble's "live text owns me" latch. Load-bearing: under the text-bubble mode 0
    /// suppression nothing else ever clears it, and <c>MouseOver</c> refuses to build a plate while it is 1 -- without
    /// this a creature's plate dies permanently the first time it speaks.</summary>
    public static void ClearLiveTextLatch(IntPtr obj)
    {
        CGuiInGamePopUpBubble b = Bubble(obj);
        if (!b.IsNull) b.LiveText = 0;
    }

    private static readonly GuiPanelUnToggleOffFn _unToggleOff = EngineFn.GuiPanelUnToggleOff;

    /// <summary>Clear the bubble's hide bit so <c>UpdatePopUpBubble</c> projects its anchor again. A bubble
    /// that idled under a drawing bubble mode carries it (<c>ToggleOff</c>) and <c>SetChatBubbleMode(0)</c>'s
    /// walk only Deactivates, so <c>CGuiPanel::UnToggleOff</c> is the bit clear and nothing else. Cold: once
    /// per hover, spoken line or floaty, from the hooks.</summary>
    public static void UnfreezeAnchor(IntPtr obj)
    {
        CGuiInGamePopUpBubble b = Bubble(obj);
        if (!b.IsNull && _unToggleOff != null) _unToggleOff(b.Ptr);
    }

    /// <summary>Is the engine currently showing its feedback plate for this object? The per-class flag set at
    /// the top of <c>ShowFeedbackBubbleText</c> and cleared in <c>MouseOff</c> -- the pollable "hovered or
    /// Tab-highlighted" state, which is why no key tracking of our own is needed.</summary>
    public static bool Plated(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero) return false;
        if (!_probed && type == TypeCreature)
        {
            _probed = true;
            // The plate flag and the portrait slot are this file's two probe-able offsets, and a creature
            // is the one instance that carries both.
            if (!EngineLayout.Probe("CNWCCreature", obj, _log) | !EngineLayout.Probe("CNWCObject", obj, _log))
            {
                _layoutRot = true;
                _log?.Always("nameplate: standing down as a unit -- engine layout probe failed on this build");
            }
        }
        if (_layoutRot) return false;
        return type switch
        {
            TypeCreature => new CNWCCreature(obj).Plated != 0,
            TypePlaceable => new CNWCPlaceable(obj).Plated != 0,
            TypeItem => new CNWCObject(obj).ItemPlated != 0,
            _ => false,
        };
    }

    /// <summary>The object's <c>ObjectTextBubbleOverride</c> mode: 1 replace, 2 prepend, 3 append, 0 (or any
    /// other value) none. HOT -- one field read, no allocation.</summary>
    public static int OverrideMode(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return 0;
        int m = new CNWCObject(obj).OverrideMode;
        return m is 1 or 2 or 3 ? m : 0;
    }

    /// <summary>Does the object's override text still equal <paramref name="cached"/>? HOT: compares the engine's
    /// bytes against the cached string in place, so a steady plate allocates nothing. Latin1, matching
    /// <see cref="Chars"/>. A length compare alone would not do -- <c>CExoString</c> assignment can land a
    /// different payload of the same length in the same block, and status labels are near-identical lengths.</summary>
    public static bool OverrideTextIs(IntPtr obj, string cached)
    {
        if (obj == IntPtr.Zero) return cached.Length == 0;
        var o = new CNWCObject(obj);
        byte* p = (byte*)o.OverrideText;
        int len = o.OverrideLen;
        if (p == null || len <= 0 || len > MaxOverrideLen) return cached.Length == 0;
        if (cached.Length != len) return false;
        for (int i = 0; i < len; i++) if (cached[i] != (char)p[i]) return false;
        return true;
    }

    /// <summary>The override text verbatim -- NOT trimmed. The leading space / leading LF the server sends is
    /// the splice itself (the server's composer emits its own
    /// separators), so trimming it would glue "Poisoned" onto the injury line without its space and drop the
    /// break before "Sneaking". COLD -- allocates, called only on the change edge.</summary>
    public static string OverrideText(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "";
        var o = new CNWCObject(obj);
        return Chars((byte*)o.OverrideText, o.OverrideLen, MaxOverrideLen);
    }

    /// <summary>Longest override we will copy out. Past this the pointer is not what we think it is.</summary>
    private const int MaxOverrideLen = 1024;

    public static void Position(IntPtr obj, out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        if (obj == IntPtr.Zero) return;
        System.Numerics.Vector3 p = new CNWCObject(obj).Position;
        x = p.X; y = p.Y; z = p.Z;
    }

    /// <summary>The live game camera's world position. NOT the Alloy <c>CameraPos</c> global, which at
    /// present time is the UI frame's camera at (0,0,742). False when there is no module camera.</summary>
    public static bool CameraPosition(out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        CX.Engine.Camera cam = ModuleCamera();
        if (cam.IsNull) return false;
        System.Numerics.Vector3 p = cam.Pos;
        x = p.X; y = p.Y; z = p.Z;
        return true;
    }

    /// <summary>The live game camera's forward axis in world space: the camera quaternion applied to
    /// <c>(0,0,-1)</c>. Used to reject objects BEHIND the camera, which the engine's own projection does not --
    /// <c>planelineintersection</c> tests its denominator only against zero (@0x5b403f), never its sign, so a
    /// point behind the camera projects to a mirrored on-screen anchor. False when there is no module camera.</summary>
    public static bool CameraForward(out float x, out float y, out float z)
    {
        x = 0f; y = 0f; z = 0f;
        CX.Engine.Camera cam = ModuleCamera();
        if (cam.IsNull) return false;
        Quat q = cam.Rot;
        float qw = q.W, qx = q.X, qy = q.Y, qz = q.Z;
        // -(third column of the rotation matrix) -- the quaternion applied to (0,0,-1).
        x = -2f * (qx * qz + qw * qy);
        y = -2f * (qy * qz - qw * qx);
        z = -(1f - 2f * (qx * qx + qy * qy));
        return true;
    }

    /// <summary>The viewport rect <c>Camera::MapToScreen</c> projected into: the camera's own
    /// <c>+0x4d4..0x4e0</c>, or -- when either extent is exactly zero, which is the test at @0x4a3db4/@0x4a3de0
    /// -- the GL viewport, exactly as the engine's own fallback does. False when there is no camera and no
    /// resolvable <c>GetViewPort</c>, in which case the caller must assume the draw viewport.</summary>
    public static bool ViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        if (CameraViewportRect(out x, out y, out w, out h) && w != 0f && h != 0f) return true;
        return GlViewportRect(out x, out y, out w, out h);
    }

    /// <summary>The camera's own viewport fields, unfiltered -- zero extents and all. Separated from
    /// <see cref="ViewportRect"/> so <c>/nameplate probe</c> can say WHICH of the two sources the conversion
    /// actually used; a silent fallback is exactly the ambiguity the probe exists to remove.</summary>
    public static bool CameraViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        CX.Engine.Camera cam = ModuleCamera();
        if (cam.IsNull) return false;
        x = cam.ViewX; y = cam.ViewY; w = cam.ViewW; h = cam.ViewH;
        return true;
    }

    /// <summary>The GL viewport as the engine last set it (<c>GLRender::m_viewport</c>, widened to floats).
    /// Reported by <c>/nameplate probe</c> and used as <see cref="ViewportRect"/>'s fallback.</summary>
    public static bool GlViewportRect(out float x, out float y, out float w, out float h)
    {
        x = 0f; y = 0f; w = 0f; h = 0f;
        if (_getViewPort == null) return false;
        float* buf = stackalloc float[4];
        _getViewPort(buf);
        x = buf[0]; y = buf[1]; w = buf[2]; h = buf[3];
        return w > 0f && h > 0f;
    }

    /// <summary>The live <c>Camera*</c> off the module -- what <c>CClientExoApp::GetModuleCamera()</c> returns,
    /// read as a field because MSVC inlined that accessor out of existence. A null view when there is no
    /// module.</summary>
    private static CX.Engine.Camera ModuleCamera()
    {
        if (_getModule == null) return default;
        IntPtr app = ExoApp();
        IntPtr module = app == IntPtr.Zero ? IntPtr.Zero : _getModule(app);
        return module == IntPtr.Zero ? default : new CNWCModule(module).Camera;
    }

    /// <summary>The player's current area id, or 0 when there is no module/area.</summary>
    public static uint PlayerAreaId()
    {
        if (_getModule == null) return 0u;
        IntPtr app = ExoApp();
        if (app == IntPtr.Zero) return 0u;
        IntPtr module = _getModule(app);
        if (module == IntPtr.Zero) return 0u;
        CNWCArea area = new CNWCModule(module).CurrentArea;
        return area.IsNull ? 0u : area.Id;
    }

    /// <summary>A creature's area id (0 for anything else). Only the creature path is area-gated in vanilla;
    /// placeables and items rely on being unloaded instead.</summary>
    public static uint CreatureAreaId(IntPtr obj, byte type)
        => obj != IntPtr.Zero && type == TypeCreature ? new CNWCCreature(obj).Area : 0u;

    /// <summary>The "show mouse-over feedback" option word: bit 0 = the option, bit 1 = Tab held. Read for
    /// the <c>/nameplate</c> report only -- the plate trigger itself is the engine's, which already tests
    /// this whole word.</summary>
    public static int FeedbackWord()
    {
        IntPtr app = ExoApp();
        if (app == IntPtr.Zero) return 0;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        return intern.IsNull ? 0 : intern.FeedbackWord;
    }

    /// <summary>Damage-level index 0..5 for a creature (5 = dead), or -1 when it has no stats.</summary>
    public static int DamageLevel(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero || type != TypeCreature) return -1;
        CNWCCreatureStats stats = new CNWCCreature(obj).Stats;
        if (stats.IsNull) return -1;
        int lvl = stats.Damage;
        return lvl > 5 ? 5 : lvl;
    }

    /// <summary>The health bar's fill 0..1 for a creature in <paramref name="band"/> (<see cref="PlateLayout.HealthFraction"/>:
    /// exact HP where the server sent it and it agrees with the band, else the band), or -1 with no band. HOT: three
    /// short reads.</summary>
    public static float Health(IntPtr obj, byte type, int band)
    {
        if (band < 0 || obj == IntPtr.Zero || type != TypeCreature) return -1f;
        CNWCCreatureStats stats = new CNWCCreature(obj).Stats;
        return stats.IsNull ? -1f : PlateLayout.HealthFraction(stats.CurHp, stats.TempHp, stats.MaxHp, band);
    }

    /// <summary>Reaction byte for the name colour: 1 friend, 2 enemy, anything else neutral.</summary>
    public static byte Reaction(IntPtr obj, byte type)
        => obj != IntPtr.Zero && type == TypeCreature ? new CNWCCreature(obj).Attitude : (byte)0;

    /// <summary>The creature's display name off its stats block: the override wins outright, else
    /// "first last", else whichever half is non-empty, else "". COLD -- it allocates a string, so it is
    /// resolved once per plate candidate, never per frame.</summary>
    public static string CreatureName(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero || type != TypeCreature) return "";
        CNWCCreatureStats stats = new CNWCCreature(obj).Stats;
        if (stats.IsNull) return "";
        string over = Chars((byte*)stats.OverrideName, (int)stats.OverrideNameLen, MaxNameLen).Trim();
        if (over.Length != 0) return over;
        string first = Chars((byte*)stats.FirstName, (int)stats.FirstNameLen, MaxNameLen).Trim();
        string last = Chars((byte*)stats.LastName, (int)stats.LastNameLen, MaxNameLen).Trim();
        if (first.Length == 0) return last;
        if (last.Length == 0) return first;
        return first + " " + last;
    }

    /// <summary>Longest string we will copy out of an engine char*/len pair. A larger length means the
    /// pointer is not what we think it is, so we return "" rather than walk it.</summary>
    private const int MaxNameLen = 256;

    /// <summary>An engine <c>char* / len</c> pair copied out verbatim -- no trim, so anything whose leading or
    /// trailing whitespace is load-bearing can use it. "" when the length says the pointer is not what we
    /// think it is.</summary>
    private static string Chars(byte* p, int len, int maxLen)
    {
        if (p == null || len <= 0 || len > maxLen) return "";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)p[i]);   // Latin1: char == source byte
        return sb.ToString();
    }

    /// <summary>The object's plate portrait resref via the GetPortrait virtual, at
    /// <see cref="PortraitSizePlate"/>. "" when the object has no portrait. COLD.</summary>
    public static string Portrait(IntPtr obj)
    {
        if (obj == IntPtr.Zero || _layoutRot) return "";
        IntPtr vtbl = *(IntPtr*)obj;
        if (vtbl == IntPtr.Zero) return "";
        var o = new CNWCObject(obj);
        if (o.GetPortraitFn == IntPtr.Zero) return "";
        byte* buf = stackalloc byte[ResRefScratch];
        for (int i = 0; i < ResRefScratch; i++) buf[i] = 0;
        o.GetPortrait(buf, PortraitSizePlate);
        int n = 0; while (n < 16 && buf[n] != 0) n++;
        return n == 0 ? "" : Encoding.ASCII.GetString(buf, n);
    }

    /// <summary>The vanilla decay for a bubble line: the server's fixed value when it sent one, else the
    /// engine's own <c>clamp(len * perChar * 0.15, 4, 12)</c> (min/max are hard-coded in
    /// <c>CNWCObject::SetPopUpBubbleText</c>, disasm @0x7e8104). Pure -- self-checked.</summary>
    public static float Decay(int textLen, float perChar, float fixedSec)
    {
        if (fixedSec > FixedDecayEpsilon) return fixedSec;
        float t = perChar * textLen * DecayPerChar;
        if (t < DecayMin) return DecayMin;
        return t > DecayMax ? DecayMax : t;
    }

    /// <summary><c>CGuiInGamePopUpBubble::SetText</c> takes the fixed value only when it exceeds this: the
    /// literal at <c>.rodata 0x1313280</c> (<c>9a99193e</c> = 0.15f), compared by
    /// <c>ucomiss 0x1313280,%xmm3; jbe &lt;length-formula&gt;</c> at <c>0x69fdf3</c>. Not <c>&gt; 0</c>.</summary>
    private const float FixedDecayEpsilon = 0.15f;
    private const float DecayPerChar = 0.15f;
    private const float DecayMin = 4f, DecayMax = 12f;

    /// <summary>Chat channels that never bubble (server message, 0x0c, 0x0f). The engine already filters
    /// them before <c>SetPopUpBubbleText</c>; this is here so the rule is testable off-device.</summary>
    public static bool ChannelBubbles(int channel) => channel != 5 && channel != 0x0c && channel != 0x0f;

    /// <summary>Off-device proof of the two pure engine rules this file owns: the decay formula (both
    /// branches and both clamp rails) and the no-bubble channel set.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        check.Expect(Decay(10, 1f, 0f) == 4f, "a short line clamps to the 4s floor");
        check.Expect(Decay(1000, 1f, 0f) == 12f, "a long line clamps to the 12s ceiling");
        float mid = Decay(40, 1f, 0f);
        check.Expect(mid > 5.9f && mid < 6.1f, "40 chars at 1.0/char is 0.15*40 = 6s, inside the clamp");
        check.Expect(Decay(40, 0.75f, 0f) < mid, "a smaller per-char scale shortens the line");
        check.Expect(Decay(1000, 1f, 30f) == 30f, "a server-sent fixed value bypasses the length formula entirely");

        // The 0 < fixedSec <= 0.15 band: the engine's jbe rejects it, a `fixedSec > 0` rule would have taken it.
        check.Expect(Decay(10, 1f, 0.1f) == 4f, "a positive fixed value below 0.15 is NOT a fixed value (jbe @0x69fdf3)");
        check.Expect(Decay(10, 1f, 0.15f) == 4f, "exactly 0.15 is not a fixed value either (jbe, not jb)");
        check.Expect(Decay(10, 1f, 0.0001f) == 4f, "a hair above zero still falls through to the length formula");
        check.Expect(Decay(10, 1f, 0.16f) == 0.16f, "just past 0.15 IS a fixed value, however short");
        check.Expect(Decay(1000, 1f, -1f) == 12f, "a negative fixed value falls through to the formula");

        check.Expect(!ChannelBubbles(5) && !ChannelBubbles(0x0c) && !ChannelBubbles(0x0f), "channels 5, 0x0c and 0x0f never bubble");
        check.Expect(SpeakerPrefixed(7) && SpeakerPrefixed(TypePlaceable) && SpeakerPrefixed(TypeDoor),
                     "triggers, placeables and doors speak with the engine's [name]: prefix");
        check.Expect(!SpeakerPrefixed(TypeCreature) && !SpeakerPrefixed(TypeItem) && !SpeakerPrefixed(200),
                     "creatures and items do not, and an out-of-range type is not shifted into the mask");
        check.Expect(ChannelBubbles(1) && ChannelBubbles(3) && ChannelBubbles(4) && ChannelBubbles(0x0e), "every other handled channel bubbles");

        log.Info(check.Ok ? "NameplateNative.SelfCheck: passed" : "NameplateNative.SelfCheck: FAILED");
    }
}
