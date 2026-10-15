using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>
/// Crosshair feedback for the action camera: <see cref="MouseLookService"/> owns it, <see cref="Crosshair"/> draws it.
/// Chaining the one player-&gt;server send funnel latches <c>{kind, target, when}</c> a round trip before the server's
/// echo; the creature's AI state and <c>CGuiInGame</c>'s action-queue panel are the lagging half. The latch is written
/// on the input thread and read on the render thread, so a torn read costs one wrong glyph. Allocates nothing.
/// </summary>
internal static unsafe class ActionFeedback
{
    private static bool _ready;
    private static SendPlayerToServerMessageFn* _next;

    // ---- the wire frame ----

    /// <summary>Major 0x06 is player input; 0x26 is the shift-click party-group twin of the same payload, same
    /// minor and the same leading object id.</summary>
    private const byte MajorInput = 0x06, MajorGroupInput = 0x26;  // not-an-offset: a message opcode

    /// <summary>The action minors this feature names. <c>CancelAction</c> is the "I changed my mind" signal and
    /// clears the latch instead of setting it.</summary>
    private const byte MinorAttack = 0x02, MinorUseFeat = 0x06, MinorUseSkill = 0x07, MinorDialog = 0x08,  // not-an-offset: a message opcode
                       MinorUseItem = 0x09, MinorUseObject = 0x0b, MinorCastSpell = 0x12,
                       MinorCancelAction = 0x14, MinorTargetSelected = 0x25;

    /// <summary>Bytes <c>CNWMessage::CreateWriteMessage</c> reserves before the first payload byte:
    /// <c>'p'</c>, major, minor and the u32 byte-stream length at <c>buf+3</c>.</summary>
    private const int HeaderBytes = 7;

    /// <summary>CastSpell's composed flags byte (after DWORD spell + 3 BYTEs); bit 0 = an object target follows.</summary>
    private const int CastSpellFlagsOff = 14;

    /// <summary>The one AI-state byte whose meaning two independent call sites pin down.</summary>
    private const byte AiStateAttacking = 2;

    // ---- the latch ----

    private static byte _kind;
    private static uint _target;
    private static long _at;
    private static int _sent;
    private static int _queued;
    private static bool _attacking;
    private static uint _aiTarget;

    // ---- the reticle additions, in px at GUI scale 1 ----

    /// <summary>How long the "order sent" pulse lives, and the radii it travels between.</summary>
    private const long PulseMs = 350;
    private const float PulseFrom = 6f, PulseTo = 22f, PulseTick = 7f;
    /// <summary>Half-size of the persistent "attacking" square ring, and the pending dot's size and drop.</summary>
    private const float AttackRing = 17f, DotHalf = 1.6f, DotDrop = 15f;
    /// <summary>The attack red, the same verb colour the reticle takes from the attack cursor.</summary>
    private const float AttackR = 1f, AttackG = 0.28f, AttackB = 0.22f;

    /// <summary>Arm the feedback: gate the three classes it reads and chain the send funnel. A refusal leaves
    /// the action camera and its crosshair untouched -- one fewer thing drawn, nothing broken.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "MouseLook.feedback", "CGuiInGame", "CPanelActionQueue", "CNWCCreature")) return;
        if (!SymbolMap.Require(log, "MouseLook.feedback", SymbolMap.SendPlayerToServerMessage)) return;

        _next = hooks.ChainSendPlayerToServerMessage(&OnSend, 50);
        _ready = _next != null;
        log.Info(_ready ? "MouseLook.feedback: chained CNWCMessage::SendPlayerToServerMessage; crosshair answers the action queue"
                        : "MouseLook.feedback: standing down (SendPlayerToServerMessage chain refused)");
    }

    /// <summary>Every player-&gt;server message. Cold -- one call per player action, no per-frame caller -- and it
    /// forwards unconditionally: this reads the frame the engine already built and changes nothing.</summary>
    [UnmanagedCallersOnly]
    private static int OnSend(IntPtr self, byte major, byte minor, byte* buf, uint len)
    {
        if (buf != null && (major == MajorInput || major == MajorGroupInput)) Latch(minor, buf, len);
        return (*_next)(self, major, minor, buf, len);
    }

    private static void Latch(byte minor, byte* buf, uint len)
    {
        if (minor == MinorCancelAction) { _at = 0; _kind = 0; _target = 0; return; }
        if (len < HeaderBytes) return;
        // GetWriteMessage stamps the BYTE-stream total here; the bit stream is memcpy'd past it, so bytes <= len.
        uint bytes = MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(buf, (int)len).Slice(3));
        if (bytes < HeaderBytes || bytes > len) return;
        int off = TargetOffset(minor, buf, bytes);
        if (off < 0) return;
        uint oid = off > 0 && off + 4 <= bytes ? MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(buf, (int)len).Slice(off)) : TypingSignal.ObjectInvalid;
        _target = oid == TypingSignal.ObjectInvalid ? 0u : oid;
        _kind = minor;
        _at = Environment.TickCount64;
        _sent++;
    }

    /// <summary>Where this minor's target object id sits in the frame, past <see cref="HeaderBytes"/>: 0 when the
    /// action carries none, -1 for a minor this feature does not name. Byte-stream writers advance the cursor in
    /// call order and the <c>BOOL</c>s do not, so a row is whatever precedes the OBJECTID: skill BYTE+BYTE, feat
    /// WORD+WORD, item OBJECTID+BYTE (+a sub-item BYTE unless 0xff), cast DWORD+3 BYTEs then a flag-gated object.</summary>
    private static int TargetOffset(byte minor, byte* buf, uint bytes)
    {
        switch (minor)
        {
            case MinorAttack:
            case MinorDialog:
            case MinorUseObject:
            case MinorTargetSelected:
                return 7;
            case MinorUseSkill: return 9;
            case MinorUseFeat: return 11;
            case MinorUseItem:
                // The sub-item BYTE is the one bit of payload length that moves the target, so the size decides.
                return (bytes - HeaderBytes) switch { 9 or 21 => 12, 10 or 22 => 13, _ => 0 };
            case MinorCastSpell:
                return bytes > CastSpellFlagsOff && (buf[CastSpellFlagsOff] & 1) != 0 ? CastSpellFlagsOff + 1 : 0;
            default: return -1;
        }
    }

    // ---- the per-frame read + draw ----

    /// <summary>What the crosshair adds: a pulse on a fresh order, a red ring while the player is attacking, a
    /// dot while the action queue still holds something. Runs inside <see cref="Crosshair"/>'s own batch, so it
    /// costs no extra draw call; three engine field reads and at most nine rects, no allocation.</summary>
    internal static void Draw(VertexBatch b, float cx, float cy, float s, float t)
    {
        if (!_ready || !MouseLook.Feedback.V) return;
        int queued = Pending();
        IntPtr cre = CameraService.PlayerCreature();
        var creature = new CNWCCreature(cre);
        bool attacking = cre != IntPtr.Zero && creature.AiState == AiStateAttacking;
        _queued = queued;
        _attacking = attacking;
        _aiTarget = attacking ? creature.AiTarget : 0u;

        long age = Environment.TickCount64 - _at;
        if (_at != 0 && (ulong)age < PulseMs)
        {
            float p = age / (float)PulseMs;
            float d = (PulseFrom + (PulseTo - PulseFrom) * p) * s, half = PulseTick * s * 0.5f, al = (1f - p) * 0.85f;
            b.Rect(cx - half, cy - d - t, cx + half, cy - d + t, 1f, 1f, 1f, al);
            b.Rect(cx - half, cy + d - t, cx + half, cy + d + t, 1f, 1f, 1f, al);
            b.Rect(cx - d - t, cy - half, cx - d + t, cy + half, 1f, 1f, 1f, al);
            b.Rect(cx + d - t, cy - half, cx + d + t, cy + half, 1f, 1f, 1f, al);
        }
        if (attacking) Ring(b, cx, cy, AttackRing * s, t, AttackR, AttackG, AttackB, 0.55f);
        if (queued > 0)
        {
            float k = DotHalf * s, y = cy + DotDrop * s;
            b.Rect(cx - k, y - k, cx + k, y + k, 1f, 1f, 1f, 0.8f);
        }
    }

    /// <summary>Actions the client still believes are outstanding: the "Current" slot plus the queued list.
    /// <c>AddAction</c> fills the slot first, so the count alone reads 0 for the common one-action case.</summary>
    private static int Pending()
    {
        IntPtr gui = GameGate.InGameGui;
        if (gui == IntPtr.Zero) return 0;
        CPanelActionQueue panel = new CGuiInGame(gui).ActionQueuePanel;
        if (panel.IsNull) return 0;
        int n = panel.QueuedCount;
        return (panel.Current != IntPtr.Zero ? 1 : 0) + (n > 0 ? n : 0);
    }

    /// <summary>A square outline of half-size <paramref name="k"/> from four thin rects.</summary>
    private static void Ring(VertexBatch b, float cx, float cy, float k, float t, float r, float g, float bl, float a)
    {
        b.Rect(cx - k, cy - k - t, cx + k, cy - k + t, r, g, bl, a);
        b.Rect(cx - k, cy + k - t, cx + k, cy + k + t, r, g, bl, a);
        b.Rect(cx - k - t, cy - k, cx - k + t, cy + k, r, g, bl, a);
        b.Rect(cx + k - t, cy - k, cx + k + t, cy + k, r, g, bl, a);
    }

    private static string Name(byte minor) => minor switch
    {
        MinorAttack => "attack",
        MinorUseFeat => "feat",
        MinorUseSkill => "skill",
        MinorDialog => "talk",
        MinorUseItem => "item",
        MinorUseObject => "use",
        MinorCastSpell => "cast",
        MinorTargetSelected => "target",
        _ => "-",
    };

    /// <summary>The mode status fragment. The queue and combat halves are the last drawn frame's sample --
    /// the panel walk belongs on the render thread, not on whichever thread typed the command.</summary>
    internal static string Report() => !_ready ? "feedback off (stood down)"
        : FormattableString.Invariant($"feedback {(MouseLook.Feedback.V ? "on" : "off")}, {_sent} order(s), last {Name(_kind)} on 0x{_target:x} {(_at == 0 ? "(none)" : $"{Environment.TickCount64 - _at}ms ago")}; {_queued} pending, {(_attacking ? $"attacking 0x{_aiTarget:x}" : "not attacking")}");
}
