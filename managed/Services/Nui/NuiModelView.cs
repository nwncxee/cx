using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Radial;

namespace CX.Services.Nui;

/// <summary>
/// <c>"@model:&lt;key&gt;"</c> as any NUI image shows a live model: a throwaway copy of a creature the server names, drawn every
/// frame into the texture the engine made for the image, so NUI draws it like any other. What the view shows is the
/// server's, by policy keys under <c>model.&lt;key&gt;.</c> (<c>docs/ui-policy-contract.md</c>). Up to
/// <see cref="NuiModelDoll.MaxViews"/> live at once, a doll each. A card or the held image shows one through <see cref="Show"/>.
/// </summary>
internal static unsafe class NuiModelView
{
    private const string Prefix = "@model:";
    private const string KeyRoot = "model.";
    private const int DefaultSize = 256, MaxSize = 1024, MaxKey = 48;
    /// <summary>The camera a view has until its server says otherwise: the view angle in degrees, the distance from and height above the creature in metres.</summary>
    private const float DefaultAngle = 30f, DefaultDist = 4f, DefaultHeight = 0.9f;
    /// <summary>The farthest a camera may be from the creature, the widest view angle and the highest it may stand, in metres or degrees.</summary>
    private const float MaxDist = 1000f, MaxAngle = 170f, MaxHeight = 1000f;
    /// <summary>The most degrees a pixel of drag may turn the doll.</summary>
    private const float MaxDrag = 10f;
    /// <summary><c>SDL_BUTTON_LMASK</c> in <c>SDL_GetMouseState</c>'s answer.</summary>
    private const uint SdlLeftMask = 1;

    /// <summary>How long a card's or the held image's view outlives the last frame that asked for it, so a pointer moving back onto an
    /// element finds the doll still built.</summary>
    internal const long LingerMs = 1500;

    /// <summary>One view: its doll's slot, the NUI image's pointers (read, never owned) or the texture CX made for it, and what the server last declared.</summary>
    private sealed class ViewData
    {
        public string Key = "";
        public NuiModelDoll.ModelSlot Slot = null!;
        public bool Owned;
        public uint OwnTex;
        public long Seen;
        public IntPtr Nk, Ctrl;
        public int W = DefaultSize, H = DefaultSize;
        public uint[]? Standin;
        public string StandinName = "";
        public int Epoch = -1;
        public uint Target;
        public float Angle = DefaultAngle, Dist = DefaultDist, Height = DefaultHeight, Yaw, Drag, Pitch;
        public float ClipNear = NuiModelLook.DefaultNear, ClipFar = NuiModelLook.DefaultFar;
        /// <summary>The nearest and farthest distance a scroll may move the camera to (both 0 = no zoom), the metres a notch moves it, and
        /// where the player has put it (NaN = still at <see cref="Dist"/>).</summary>
        public float ZoomMin, ZoomMax, ZoomStep = NuiModelLook.DefaultZoomStep, ZoomAt = float.NaN;
        /// <summary>The range the player's drag turn stays in (both 0 with <see cref="HasTurnRange"/> false = free).</summary>
        public float TurnMin, TurnMax;
        public bool HasTurnRange, MuteVfx, WornMuted;
        public bool Anim = true, Built, WasRendered;
        public string Wear = "", StandinRect = "", Vfx = "";
        public NuiModelVfx.VfxRow[] VfxRows = Array.Empty<NuiModelVfx.VfxRow>();
        public byte[] Info = Array.Empty<byte>();
        public bool OnPlayer;
        public uint TargetId;
        /// <summary>The player's own turn on top of the server's <see cref="Yaw"/>, and the press that is turning it. <see cref="Drag"/> is the
        /// server's degrees a pixel, 0 for no drag.</summary>
        public float DragYaw;
        public bool Dragging;
        public int LastX;
    }

    private sealed class ViewState
    {
        public bool Ready, CapSaid;
        public ExtenderLog? Log;
        public readonly List<ViewData> Views = new();
        public string PendingKey = "";
        public uint[]? PendingPixels;
        public int PendingW, PendingH;
        public string PendingStandin = "";
    }

    private static readonly ViewState _s = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        _s.Ready = NuiModelDoll.Init(log);
        NuiModelGear.Init(log);
        NuiModelVfx.Init(log, hooks);
        if (!_s.Ready) { log.Always("NuiModelView: the engine pieces did not resolve -- no model views"); return; }
        Capabilities.Register("modelview", 4, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.ModelView.V);
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
        NuiKnobs.ModelView.Changed += Capabilities.Refresh;
        NuiModelEngine.Install(log, hooks);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 1, Frame, "NuiModelView.frame", log);
    }

    /// <summary>Is this nk_loadimg name a model view's?</summary>
    internal static bool IsModelName(byte* s)
    {
        for (int i = 0; i < Prefix.Length; i++) if (s[i] != (byte)Prefix[i]) return false;
        return s[Prefix.Length] != 0;
    }

    private static string KeyOf(byte* s)
    {
        var sb = new StringBuilder();
        for (byte* p = s + Prefix.Length; *p != 0 && sb.Length < MaxKey; p++) sb.Append((char)*p);
        return sb.ToString();
    }

    private static string Policy(string key, string what, string def) => ServerPolicy.Str($"{KeyRoot}{key}.{what}", def);

    private static float PolicyF(string key, string what, float def)
        => float.TryParse(Policy(key, what, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

    private static (int, int, bool) SizeOf(string key)
    {
        string v = Policy(key, "size", "");
        int x = v.IndexOf('x');
        if (x > 0 && int.TryParse(v.AsSpan(0, x), out int w) && int.TryParse(v.AsSpan(x + 1), out int h)
            && w is > 0 and <= MaxSize && h is > 0 and <= MaxSize) return (w, h, true);
        return (DefaultSize, DefaultSize, false);
    }

    /// <summary>nk_loadimg asks for the image: make the TGA it will load, the stand-in at its own size or a transparent
    /// one. A declaration that lands later is applied to the live texture by <see cref="Frame"/>.</summary>
    internal static bool Prepare(byte* resref)
    {
        if (!_s.Ready) return false;
        if (!NuiKnobs.Enabled.V || !NuiKnobs.ModelView.V)
        {
            _s.PendingKey = "";
            NuiItemIcons.ModelImage(default, 1, 1, false, default, out _, out _, out _);
            return true;
        }
        string key = KeyOf(resref);
        var (w, h, sized) = SizeOf(key);
        string standin = Policy(key, "standin", "");
        StandinImage(standin, w, h, sized, Policy(key, "standinrect", ""), out uint[] px, out int pw, out int ph);
        _s.PendingKey = key; _s.PendingPixels = px; _s.PendingW = pw; _s.PendingH = ph; _s.PendingStandin = standin;
        return true;
    }

    /// <summary>nk_loadimg returned <paramref name="ret"/>, a <c>shared_ptr&lt;NkImage&gt;</c> (pointer, control block).</summary>
    internal static void Loaded(byte* resref, IntPtr ret)
    {
        if (KeyOf(resref) != _s.PendingKey) return;
        IntPtr nk = *(IntPtr*)ret, ctrl = *(IntPtr*)(ret + IntPtr.Size);
        for (int i = _s.Views.Count - 1; i >= 0; i--)
        {
            ViewData old = _s.Views[i];
            if (old.Owned || old.Key != _s.PendingKey) continue;
            if (old.Nk == nk) { old.Ctrl = ctrl; return; }
            if (!Alive(old)) Release(_s, old);
        }
        NuiModelDoll.ModelSlot? slot = NuiModelDoll.Claim();
        if (slot == null) { SayCap(_s, _s.PendingKey); return; }
        _s.Views.Add(new ViewData
        {
            Key = _s.PendingKey, Slot = slot, Nk = nk, Ctrl = ctrl,
            W = _s.PendingW, H = _s.PendingH, Standin = _s.PendingPixels, StandinName = _s.PendingStandin,
        });
    }

    private static void SayCap(ViewState s, string key)
    {
        if (s.CapSaid) return;
        s.CapSaid = true;
        s.Log?.Always($"NuiModelView: '{key}' not shown -- {NuiModelDoll.MaxViews} model views are alive already, which is the most there is");
    }

    /// <summary>Is this expired? A card's or the held image's view whose last asking frame was <paramref name="seen"/> ms ago is done.</summary>
    internal static bool Lapsed(long now, long seen) => now - seen > LingerMs;

    /// <summary>The model <paramref name="key"/> as a texture to draw in a card or the held image: asked every frame the image is on
    /// screen. The first ask claims a slot; the doll is built on the next frame, and the texture is returned (0 until then, and while the
    /// view is not showing) once a frame of it has landed. <paramref name="w"/> and <paramref name="h"/> are the texture's size.
    /// The texture is upright in GL's terms flipped: draw it with <c>flipV</c>.</summary>
    internal static uint Show(string key, out int w, out int h)
    {
        w = h = 0;
        ViewState s = _s;
        if (!s.Ready || !NuiKnobs.Enabled.V || !NuiKnobs.ModelView.V || key.Length == 0) return 0;
        if (key.Length > MaxKey) key = key[..MaxKey];
        ViewData? v = null;
        foreach (ViewData c in s.Views) if (c.Owned && c.Key == key) { v = c; break; }
        if (v == null)
        {
            NuiModelDoll.ModelSlot? slot = NuiModelDoll.Claim();
            if (slot == null) { SayCap(s, key); return 0; }
            var (sw, sh, _) = SizeOf(key);
            v = new ViewData { Key = key, Slot = slot, Owned = true, W = sw, H = sh };
            s.Views.Add(v);
        }
        v.Seen = Environment.TickCount64;
        w = v.W; h = v.H;
        return v.Built && v.Slot.Rendered ? v.OwnTex : 0;
    }

    /// <summary>The image is alive while its shared_ptr control block counts an owner. The block outlives the image,
    /// because nk_loadimg's name map keeps a weak reference to it.</summary>
    private static bool Alive(ViewData v) => v.Ctrl != IntPtr.Zero && new SharedPtrBlock(v.Ctrl).UseCount > 0;

    private static void Frame()
    {
        ViewState s = _s;
        for (int i = s.Views.Count - 1; i >= 0; i--) Serve(s, s.Views[i]);
        foreach (NuiModelDoll.ModelSlot slot in NuiModelDoll.Slots) NuiModelDoll.KeepShown(slot);
    }

    /// <summary>A view is done: its doll is taken down, its watch and (a card's or the held image's) texture dropped, its slot given back.</summary>
    private static void Release(ViewState s, ViewData v)
    {
        if (v.Built) s.Log?.Always($"NuiModelView: '{v.Key}' closed -- doll taken down");
        NuiImageSpot.Watch(v.Slot.Index, 0);
        NuiModelDoll.Free(v.Slot);
        if (v.OwnTex != 0) { uint t = v.OwnTex; Gl.DeleteTextures(1, &t); v.OwnTex = 0; }
        v.Built = false;
        s.Views.Remove(v);
    }

    /// <summary>The texture CX makes for a card's or the held image's view: RGBA, its size, drawn into by the doll's layer.</summary>
    private static bool MakeTexture(ViewState s, ViewData v)
    {
        if (!Gl.Init(s.Log!) || Gl.TexImage2D == null) return false;
        int prev;
        uint t;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &prev);
        Gl.GenTextures(1, &t);
        Gl.BindTexture(Gl.TEXTURE_2D, t);
        Gl.TexImage2D(Gl.TEXTURE_2D, 0, (int)Gl.RGBA8, v.W, v.H, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TexParameteri(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)prev);
        v.OwnTex = t;
        return true;
    }

    private static void Serve(ViewState s, ViewData v)
    {
        uint tex;
        if (v.Owned)
        {
            if (Lapsed(Environment.TickCount64, v.Seen)) { Release(s, v); return; }
            if (v.OwnTex == 0 && !MakeTexture(s, v)) return;
            tex = v.OwnTex;
        }
        else
        {
            if (!Alive(v)) { Release(s, v); return; }
            tex = new NkImage(v.Nk).GlTexture;
            if (tex == 0) return;
        }
        NuiModelDoll.ModelSlot slot = v.Slot;
        int epoch = ServerPolicy.Epoch;
        if (epoch != v.Epoch) { v.Epoch = epoch; Read(s, v); }
        IntPtr who = Resolve(v);
        if (who == IntPtr.Zero)
        {
            if (v.Built) { Teardown(s, v, tex); }
            return;
        }
        NuiModelGear.Plan(slot.Gear, v.Wear);
        if (!v.Built)
        {
            if (!NuiModelDoll.Build(slot, who, 0, 0, v.W, v.H, v.Anim)) return;
            v.Built = true;
            NuiModelGear.TakeDirty(slot.Gear);
            v.Info = NuiModelDoll.InfoOf(who).ToArray();
            NuiModelGear.Sync(slot.Gear, slot.Doll.Creature, who);
            NuiModelVfx.Wear(slot.Doll.Creature, v.VfxRows, v.MuteVfx);
            NuiModelDoll.SetTarget(slot, tex, v.W, v.H);
            ApplyCamera(v);
            s.Log?.Always($"NuiModelView: '{v.Key}' -- doll {slot.Index} drawing into texture {tex} ({v.W}x{v.H}), scene 0x{slot.Doll.Scene:x}");
        }
        else if (v.Target != tex) NuiModelDoll.SetTarget(slot, tex, v.W, v.H);
        else
        {
            NuiModelGear.Sync(slot.Gear, slot.Doll.Creature, who);
            ReadOnlySpan<byte> now = NuiModelDoll.InfoOf(who);
            bool dressed = NuiModelGear.TakeDirty(slot.Gear);
            bool moved = !now.IsEmpty && v.Info.Length == now.Length && !now.SequenceEqual(v.Info);
            if (dressed || moved)
            {
                if (NuiModelDoll.Refresh(slot, who))
                {
                    NuiModelVfx.Wear(slot.Doll.Creature, v.VfxRows, v.MuteVfx);
                    s.Log?.Always($"NuiModelView: '{v.Key}' -- the {(dressed ? "previewed gear" : "creature's appearance")} changed, the doll follows");
                }
                if (moved) v.Info = now.ToArray();
                NuiModelGear.Settled(slot.Gear);
            }
        }
        if (v.Dragging) Turn(v);
        NuiModelDoll.Orbit(slot, Distance(v), v.Height, v.Yaw + v.DragYaw, v.Pitch);
        v.Target = tex;
        if (!v.Owned) NuiImageSpot.Watch(slot.Index, v.Drag > 0f || v.ZoomMax > 0f ? tex : 0);
        if (slot.Rendered) v.WasRendered = true;
    }

    /// <summary>The built NUI view the pointer is over, among those <paramref name="wants"/> accepts, or null.</summary>
    private static ViewData? Under(Func<ViewData, bool> wants)
    {
        foreach (ViewData v in _s.Views)
            if (!v.Owned && v.Built && wants(v) && NuiImageSpot.PointerOn(v.Slot.Index)) return v;
        return null;
    }

    /// <summary>How far the camera stands now: where the player scrolled it to, else the server's <c>dist</c>.</summary>
    private static float Distance(ViewData v) => float.IsNaN(v.ZoomAt) ? v.Dist : v.ZoomAt;

    /// <summary>A scroll landed on NUI (from the input hook): one over a model view whose server allows zoom moves the camera
    /// <see cref="ViewData.ZoomStep"/> metres a notch, nearer for a scroll up, inside the server's range. True = the view took it.</summary>
    internal static bool OnWheel(int notches)
    {
        ViewData? v = Under(static c => c.ZoomMax > 0f);
        if (v == null || notches == 0) return false;
        v.ZoomAt = Math.Clamp(Distance(v) - notches * v.ZoomStep, v.ZoomMin, v.ZoomMax);
        return true;
    }

    /// <summary>A left press landed on NUI (from the input hook): one on a model view whose server allows a drag starts turning its doll.</summary>
    internal static void OnPress()
    {
        ViewData? v = Under(static c => c.Drag > 0f);
        if (v == null || EngineFn.SdlGetMouseState == null) return;
        int mx = 0, my = 0;
        EngineFn.SdlGetMouseState(&mx, &my);
        v.Dragging = true;
        v.LastX = mx;
        _s.Log?.Info($"NuiModelView: '{v.Key}' turns with the pointer from x {mx}, the doll {NuiModelDoll.Pose(v.Slot)}");
    }

    /// <summary>While the press that began on the image is held, the doll turns <see cref="ViewData.Drag"/> degrees a pixel of sideways travel.</summary>
    private static void Turn(ViewData v)
    {
        int mx = 0, my = 0;
        uint buttons = EngineFn.SdlGetMouseState(&mx, &my);
        if ((buttons & SdlLeftMask) == 0) { v.Dragging = false; return; }
        if (mx == v.LastX) return;
        float turn = v.DragYaw + (mx - v.LastX) * v.Drag;
        v.DragYaw = v.HasTurnRange ? Math.Clamp(turn, v.TurnMin, v.TurnMax) : turn % 360f;
        v.LastX = mx;
    }

    /// <summary>The creature the server named, or zero while it names none or the client does not know it yet. The name was parsed
    /// when the policy last moved, so a frame composes no key.</summary>
    private static IntPtr Resolve(ViewData v)
    {
        if (v.OnPlayer) return NuiModelDoll.Player();
        return v.TargetId == 0 ? IntPtr.Zero : NuiModelDoll.Creature(v.TargetId | NuiItemIcons.ServerIdBit);
    }

    private static void Read(ViewState s, ViewData v)
    {
        string target = Policy(v.Key, "target", "");
        v.OnPlayer = target == "player";
        v.TargetId = !v.OnPlayer && uint.TryParse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id) ? id : 0;
        v.Angle = Math.Clamp(PolicyF(v.Key, "angle", DefaultAngle), 1f, MaxAngle);
        v.Dist = Math.Clamp(PolicyF(v.Key, "dist", DefaultDist), 0f, MaxDist);
        v.Height = Math.Clamp(PolicyF(v.Key, "height", DefaultHeight), -MaxHeight, MaxHeight);
        v.Yaw = PolicyF(v.Key, "yaw", 0f);
        v.Pitch = Math.Clamp(PolicyF(v.Key, "pitch", 0f), -NuiModelLook.MaxPitch, NuiModelLook.MaxPitch);
        bool clip = NuiModelLook.Pair(Policy(v.Key, "clip", ""), 0.001f, NuiModelLook.MaxFar, out float near, out float far) && near < far;
        v.ClipNear = clip ? near : NuiModelLook.DefaultNear;
        v.ClipFar = clip ? far : NuiModelLook.DefaultFar;
        bool zoom = NuiModelLook.Pair(Policy(v.Key, "zoom", ""), 0.01f, MaxDist, out float zmin, out float zmax);
        v.ZoomMin = zoom ? zmin : 0f;
        v.ZoomMax = zoom ? zmax : 0f;
        v.ZoomStep = Math.Clamp(PolicyF(v.Key, "zoomstep", NuiModelLook.DefaultZoomStep), NuiModelLook.MinZoomStep, NuiModelLook.MaxZoomStep);
        if (!zoom) v.ZoomAt = float.NaN;
        else if (!float.IsNaN(v.ZoomAt)) v.ZoomAt = Math.Clamp(v.ZoomAt, v.ZoomMin, v.ZoomMax);
        v.HasTurnRange = NuiModelLook.Pair(Policy(v.Key, "dragrange", ""), -NuiModelLook.MaxTurn, NuiModelLook.MaxTurn, out float tmin, out float tmax);
        v.TurnMin = v.HasTurnRange ? tmin : 0f;
        v.TurnMax = v.HasTurnRange ? tmax : 0f;
        if (v.HasTurnRange) v.DragYaw = Math.Clamp(v.DragYaw, v.TurnMin, v.TurnMax);
        v.MuteVfx = PolicyF(v.Key, "vfxmute", 0f) != 0f;
        NuiModelDoll.SetLight(v.Slot, NuiModelLook.Light(Policy(v.Key, "light", ""), Policy(v.Key, "lightcolor", ""), Policy(v.Key, "lightdir", ""), Policy(v.Key, "lightambient", "")));
        v.Drag = Math.Clamp(PolicyF(v.Key, "drag", 0f), 0f, MaxDrag);
        v.Anim = PolicyF(v.Key, "anim", 1f) != 0f;
        v.Wear = Policy(v.Key, "wear", "");
        string vfx = Policy(v.Key, "vfx", "");
        if (vfx != v.Vfx || v.MuteVfx != v.WornMuted)
        {
            v.Vfx = vfx;
            v.VfxRows = NuiModelVfx.Parse(vfx);
            v.WornMuted = v.MuteVfx;
            if (v.Built) NuiModelVfx.Wear(v.Slot.Doll.Creature, v.VfxRows, v.MuteVfx);
        }
        string[] bg = Policy(v.Key, "bg", "").Split(',');
        float C(int i) => i < bg.Length && float.TryParse(bg[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        NuiModelDoll.SetBackground(v.Slot, C(0), C(1), C(2), C(3));
        if (v.Built) ApplyCamera(v);
        string standin = Policy(v.Key, "standin", "");
        string rect = Policy(v.Key, "standinrect", "");
        if (!v.Owned && (standin != v.StandinName || rect != v.StandinRect))
        {
            v.StandinName = standin;
            v.StandinRect = rect;
            if (!v.Slot.Rendered && v.Target != 0) UploadStandin(s, v);
        }
    }

    private static void ApplyCamera(ViewData v)
    {
        IntPtr c = v.Slot.Doll.Camera;
        if (c == IntPtr.Zero) return;
        var cam = new CAurCamera(c);
        cam.SetViewAngle(v.Angle);
        cam.SetClipDist(v.ClipNear, v.ClipFar);
        NuiModelDoll.Orbit(v.Slot, Distance(v), v.Height, v.Yaw + v.DragYaw, v.Pitch);
        _s.Log?.Info($"NuiModelView: '{v.Key}' camera at yaw {v.Yaw}, the doll {NuiModelDoll.Pose(v.Slot)}");
    }

    /// <summary>The doll is gone (the server named no one, or the creature left): put the stand-in back in the texture.</summary>
    private static void Teardown(ViewState s, ViewData v, uint tex)
    {
        NuiModelDoll.Destroy(v.Slot);
        NuiImageSpot.Watch(v.Slot.Index, 0);
        v.Built = false;
        v.Target = tex;
        s.Log?.Always($"NuiModelView: '{v.Key}' -- doll taken down{(v.Owned ? "" : ", stand-in restored")}");
        if (!v.Owned) UploadStandin(s, v);
    }

    /// <summary>The TGA pixels for the stand-in resref <paramref name="standin"/> cut to <paramref name="rect"/>, or a transparent one.</summary>
    private static void StandinImage(string standin, int w, int h, bool sized, string rect, out uint[] px, out int pw, out int ph)
    {
        Span<byte> name = stackalloc byte[RadialIcons.ResRefLen + 1];
        name.Clear();
        for (int i = 0; i < standin.Length && i < RadialIcons.ResRefLen; i++) name[i] = (byte)standin[i];
        NuiItemIcons.ModelImage(name, w, h, sized, StandinRect.Parse(rect), out px, out pw, out ph);
    }

    /// <summary>The stand-in's pixels into the view's texture, in the engine's context with its texture binding put back as it was.</summary>
    private static void UploadStandin(ViewState s, ViewData v)
    {
        uint[]? px = v.Standin;
        if (v.StandinName.Length > 0)
        {
            StandinImage(v.StandinName, v.W, v.H, true, v.StandinRect, out uint[] fresh, out int fw, out int fh);
            if (fw != v.W || fh != v.H) { s.Log?.Always($"NuiModelView: stand-in '{v.StandinName}' is {fw}x{fh}, the view {v.W}x{v.H} -- not applied"); return; }
            px = v.Standin = fresh;
        }
        else
        {
            px = v.Standin = new uint[v.W * v.H];
        }
        if (px == null || v.Target == 0 || Gl.TexSubImage2D == null) return;
        int prev, pbo, align, rowLen;
        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &prev);
        Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &pbo);
        Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &align);
        Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &rowLen);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 4);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, 0);
        Gl.BindTexture(Gl.TEXTURE_2D, v.Target);
        fixed (uint* p = px) Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, v.W, v.H, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)prev);
        Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, rowLen);
        Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, align);
        Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)pbo);
    }

    /// <summary>Oracle: hand-written names. Which strings are model views' and what key each carries.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte* b = stackalloc byte[64];
        bool Is(string v)
        {
            for (int i = 0; i < v.Length; i++) b[i] = (byte)v[i];
            b[v.Length] = 0;
            return IsModelName(b);
        }
        check.Expect(Is("@model:me") && Is("@model:player.1"), "an @model: name with a key is a model view's");
        check.Expect(!Is("@model:") && !Is("@models:me") && !Is("@item:1a") && !Is("model:me") && !Is("@look:0001"), "a bare prefix or another family is not");
        for (int i = 0; i < 9; i++) b[i] = (byte)"@model:me"[i];
        b[9] = 0;
        check.Expect(KeyOf(b) == "me", "the key is what follows the prefix");
        check.Expect(Lapsed(10000, 8000) && !Lapsed(10000, 9000) && !Lapsed(10000, 10000), "a card's view lapses after a second and a half unasked, not before");
    }
}
