using System;
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
/// server's, by policy keys under <c>model.&lt;key&gt;.</c> (<c>docs/ui-policy-contract.md</c>); until the doll has been built
/// and drawn once, and again once it is gone, the image holds the server's stand-in, or nothing.
/// </summary>
internal static unsafe class NuiModelView
{
    private const string Prefix = "@model:";
    private const string KeyRoot = "model.";
    private const int DefaultSize = 256, MaxSize = 1024, MaxKey = 48;
    /// <summary>The most degrees a pixel of drag may turn the doll.</summary>
    private const float MaxDrag = 10f;
    /// <summary><c>SDL_BUTTON_LMASK</c> in <c>SDL_GetMouseState</c>'s answer.</summary>
    private const uint SdlLeftMask = 1;

    /// <summary>The one view the doll serves: the image's pointers (read, never owned) and what the server last declared.</summary>
    private sealed class ViewData
    {
        public string Key = "";
        public IntPtr Nk, Ctrl;
        public int W = DefaultSize, H = DefaultSize;
        public uint[]? Standin;
        public string StandinName = "";
        public int Epoch = -1;
        public uint Target;
        public float Angle = 30f, Dist = 4f, Height = 0.9f, Yaw, Drag;
        public bool Anim = true, Built, WasRendered;
        public string Wear = "", StandinRect = "", Vfx = "";
        public ushort[] VfxRows = Array.Empty<ushort>();
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
        public bool Ready;
        public ExtenderLog? Log;
        public ViewData? View;
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
        NuiModelVfx.Init(log);
        if (!_s.Ready) { log.Always("NuiModelView: the engine pieces did not resolve -- no model views"); return; }
        Capabilities.Register("modelview", 2, static () => _s.Ready && NuiKnobs.Enabled.V && NuiKnobs.ModelView.V);
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
        ViewData? old = _s.View;
        var v = new ViewData
        {
            Key = _s.PendingKey, Nk = *(IntPtr*)ret, Ctrl = *(IntPtr*)(ret + IntPtr.Size),
            W = _s.PendingW, H = _s.PendingH, Standin = _s.PendingPixels, StandinName = _s.PendingStandin,
        };
        if (old != null && old.Key == v.Key && old.Nk == v.Nk) { old.Ctrl = v.Ctrl; return; }
        if (old != null && NuiModelDoll.Doll.Creature != IntPtr.Zero) NuiModelDoll.Destroy();
        _s.View = v;
    }

    /// <summary>The image is alive while its shared_ptr control block counts an owner. The block outlives the image,
    /// because nk_loadimg's name map keeps a weak reference to it.</summary>
    private static bool Alive(ViewData v) => v.Ctrl != IntPtr.Zero && new SharedPtrBlock(v.Ctrl).UseCount > 0;

    private static void Frame()
    {
        ViewState s = _s;
        if (s.View != null) Serve(s, s.View);
        NuiModelDoll.KeepShown();
    }

    private static void Serve(ViewState s, ViewData v)
    {
        if (!Alive(v))
        {
            if (v.Built) { NuiModelDoll.Destroy(); v.Built = false; s.Log?.Always($"NuiModelView: '{v.Key}' closed -- doll taken down"); }
            NuiImageSpot.Watch(0);
            s.View = null;
            return;
        }
        uint tex = new NkImage(v.Nk).GlTexture;
        if (tex == 0) return;
        int epoch = ServerPolicy.Epoch;
        if (epoch != v.Epoch) { v.Epoch = epoch; Read(s, v); }
        IntPtr who = Resolve(v);
        if (who == IntPtr.Zero)
        {
            if (v.Built) { Teardown(s, v, tex); }
            return;
        }
        if (!v.Built)
        {
            if (!NuiModelDoll.Build(who, 0, 0, v.W, v.H, v.Anim)) return;
            v.Built = true;
            v.Info = NuiModelDoll.InfoOf(who).ToArray();
            NuiModelGear.Sync(NuiModelDoll.Doll.Creature, who, v.Wear);
            NuiModelVfx.Wear(NuiModelDoll.Doll.Creature, v.VfxRows);
            NuiModelDoll.SetTarget(tex, v.W, v.H);
            ApplyCamera(v);
            s.Log?.Always($"NuiModelView: '{v.Key}' -- doll drawing into texture {tex} ({v.W}x{v.H})");
        }
        else if (v.Target != tex) NuiModelDoll.SetTarget(tex, v.W, v.H);
        else
        {
            NuiModelGear.Sync(NuiModelDoll.Doll.Creature, who, v.Wear);
            ReadOnlySpan<byte> now = NuiModelDoll.InfoOf(who);
            if (!now.IsEmpty && v.Info.Length == now.Length && !now.SequenceEqual(v.Info))
            {
                if (NuiModelDoll.Refresh(who))
                {
                    NuiModelVfx.Wear(NuiModelDoll.Doll.Creature, v.VfxRows);
                    s.Log?.Always($"NuiModelView: '{v.Key}' -- the creature's appearance changed, the doll follows");
                }
                v.Info = now.ToArray();
            }
        }
        if (v.Dragging) Turn(v);
        NuiModelDoll.Orbit(v.Dist, v.Height, v.Yaw + v.DragYaw);
        v.Target = tex;
        NuiImageSpot.Watch(v.Drag > 0f ? tex : 0);
        if (NuiModelDoll.Rendered) v.WasRendered = true;
    }

    /// <summary>A left press landed on NUI (from the input hook): one on a model view whose server allows a drag starts turning its doll.</summary>
    internal static void OnPress()
    {
        ViewData? v = _s.View;
        if (v == null || !v.Built || v.Drag <= 0f || EngineFn.SdlGetMouseState == null || !NuiImageSpot.PointerOn()) return;
        int mx = 0, my = 0;
        EngineFn.SdlGetMouseState(&mx, &my);
        v.Dragging = true;
        v.LastX = mx;
        _s.Log?.Info($"NuiModelView: '{v.Key}' turns with the pointer from x {mx}, the doll {NuiModelDoll.Pose()}");
    }

    /// <summary>While the press that began on the image is held, the doll turns <see cref="ViewData.Drag"/> degrees a pixel of sideways travel.</summary>
    private static void Turn(ViewData v)
    {
        int mx = 0, my = 0;
        uint buttons = EngineFn.SdlGetMouseState(&mx, &my);
        if ((buttons & SdlLeftMask) == 0) { v.Dragging = false; return; }
        if (mx == v.LastX) return;
        v.DragYaw = (v.DragYaw + (mx - v.LastX) * v.Drag) % 360f;
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
        v.Angle = PolicyF(v.Key, "angle", 30f);
        v.Dist = PolicyF(v.Key, "dist", 4f);
        v.Height = PolicyF(v.Key, "height", 0.9f);
        v.Yaw = PolicyF(v.Key, "yaw", 0f);
        v.Drag = Math.Clamp(PolicyF(v.Key, "drag", 0f), 0f, MaxDrag);
        v.Anim = PolicyF(v.Key, "anim", 1f) != 0f;
        v.Wear = Policy(v.Key, "wear", "");
        string vfx = Policy(v.Key, "vfx", "");
        if (vfx != v.Vfx)
        {
            v.Vfx = vfx;
            v.VfxRows = NuiModelVfx.Parse(vfx);
            if (v.Built) NuiModelVfx.Wear(NuiModelDoll.Doll.Creature, v.VfxRows);
        }
        string[] bg = Policy(v.Key, "bg", "").Split(',');
        float C(int i) => i < bg.Length && float.TryParse(bg[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        NuiModelDoll.SetBackground(C(0), C(1), C(2), C(3));
        if (v.Built) ApplyCamera(v);
        string standin = Policy(v.Key, "standin", "");
        string rect = Policy(v.Key, "standinrect", "");
        if (standin != v.StandinName || rect != v.StandinRect)
        {
            v.StandinName = standin;
            v.StandinRect = rect;
            if (!NuiModelDoll.Rendered && v.Target != 0) UploadStandin(s, v);
        }
    }

    private static void ApplyCamera(ViewData v)
    {
        IntPtr c = NuiModelDoll.Doll.Camera;
        if (c == IntPtr.Zero) return;
        var cam = new CAurCamera(c);
        cam.SetViewAngle(v.Angle);
        cam.SetClipDist(0.1f, 2000f);
        NuiModelDoll.Orbit(v.Dist, v.Height, v.Yaw + v.DragYaw);
        _s.Log?.Info($"NuiModelView: '{v.Key}' camera at yaw {v.Yaw}, the doll {NuiModelDoll.Pose()}");
    }

    /// <summary>The doll is gone (the server named no one, or the creature left): put the stand-in back in the texture.</summary>
    private static void Teardown(ViewState s, ViewData v, uint tex)
    {
        NuiModelDoll.Destroy();
        NuiImageSpot.Watch(0);
        v.Built = false;
        v.Target = tex;
        s.Log?.Always($"NuiModelView: '{v.Key}' -- doll taken down, stand-in restored");
        UploadStandin(s, v);
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
    }
}
