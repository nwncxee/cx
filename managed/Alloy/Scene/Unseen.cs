using System;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>What <see cref="Unseen"/> holds between frames: the creatures drawing as silhouettes.</summary>
internal sealed class UnseenState
{
    internal readonly Unseen.Mark[] Marks = new Unseen.Mark[Unseen.MaxMarks];
    internal int Count;
    internal bool Spilled;
}

/// <summary>Creatures the player character hears but does not see. The game draws them half see-through, exactly as
/// it draws one under Invisibility; Alloy draws them as a solid flat silhouette so the two read apart.</summary>
internal static unsafe class Unseen
{
    /// <summary>The perception marks of <c>CNWVisibilityNode</c>'s flag byte, as <c>CNWCObject::GetFinalFadeAlpha</c>
    /// tests them: bit 0 seen, bit 1 heard, bit 4 invisible.</summary>
    internal const byte Seen = 1, Heard = 2, Invisible = 16;

    /// <summary>The alpha <c>CNWCObject::GetFinalFadeAlpha</c> answers for a heard-but-unseen creature, which a
    /// silhouette draws solid at; under it the game is fading the creature in or out.</summary>
    internal const float EngineAlpha = 0.5f;

    /// <summary>The record's slot-mask bit for a silhouette, <c>CX_SLOT_SILHOUETTE</c>. Above the palette bits.</summary>
    internal const int SlotBit = 1 << 13;

    /// <summary>Silhouettes held at once. ponytail: past it the rest draw see-through as the game has them, logged once;
    /// a perception list this long is the cue for a table keyed by object.</summary>
    internal const int MaxMarks = 256;

    /// <summary>One perception entry: the game object it names, its marks, and the creature's scene object.</summary>
    internal readonly record struct Sight(uint Oid, byte Marks, DbId Root);

    /// <summary>One creature drawing as a silhouette.</summary>
    internal readonly record struct Mark(uint Oid, DbId Root);

    private static UnseenState _s = new();

    /// <summary>Forget every silhouette: an area change or a stand-down.</summary>
    internal static void Reset()
    {
        for (int k = 0; k < _s.Count; k++)
            if (SceneDb.Objects.Valid(_s.Marks[k].Root)) SceneDb.ObjUnseen[_s.Marks[k].Root.Index] = false;
        _s = new UnseenState();
    }

    /// <summary>Do these perception marks make a silhouette?</summary>
    internal static bool Silhouetted(byte marks) => (marks & (Seen | Invisible)) == 0 && (marks & Heard) != 0;

    /// <summary>Is this object part of a silhouette? A creature's pieces and everything riding it answer as it does.</summary>
    internal static bool Of(int obj)
    {
        DbId root = SceneDb.ObjRunRoot[obj];
        return SceneDb.ObjUnseen[SceneDb.Objects.Valid(root) ? root.Index : obj];
    }

    /// <summary>The object alpha a part of <paramref name="obj"/> draws with, from the game's. A silhouette is solid
    /// at <see cref="EngineAlpha"/> and fades under it; its unlit parts add light of their own, so they are not drawn.</summary>
    internal static float Alpha(int obj, float engineAlpha, bool lit)
        => !Of(obj) ? engineAlpha : lit ? MathF.Min(1f, engineAlpha / EngineAlpha) : 0f;

    /// <summary>The slot mask a lit part of <paramref name="obj"/> carries.</summary>
    internal static int Slots(int obj, int slots) => Of(obj) ? slots | SlotBit : slots;

    /// <summary>This frame's perception entries: which creatures are silhouettes from here on.</summary>
    internal static void Frame(ReadOnlySpan<Sight> sights)
    {
        UnseenState s = _s;
        int kept = 0;
        for (int k = 0; k < s.Count; k++)
        {
            Mark m = s.Marks[k];
            if (!SceneDb.Objects.Valid(m.Root)) continue;
            if (Stays(m, sights)) s.Marks[kept++] = m;
            else SceneDb.ObjUnseen[m.Root.Index] = false;
        }
        s.Count = kept;
        foreach (Sight seen in sights)
        {
            if (!Silhouetted(seen.Marks) || !SceneDb.Objects.Valid(seen.Root) || SceneDb.ObjUnseen[seen.Root.Index]) continue;
            if (s.Count == MaxMarks)
            {
                if (!s.Spilled) Facade.Log?.Always($"Unseen: more than {MaxMarks} heard-but-unseen creatures -- the rest draw see-through");
                s.Spilled = true;
                break;
            }
            s.Marks[s.Count++] = new Mark(seen.Oid, seen.Root);
            SceneDb.ObjUnseen[seen.Root.Index] = true;
        }
    }

    /// <summary>Read the player character's perception list and take it as this frame's. Each entry the game marks
    /// heard-but-unseen is resolved to its creature's body model; the rest only say their entry exists.</summary>
    internal static void Poll()
    {
        Span<Sight> sights = stackalloc Sight[MaxMarks];
        int n = 0;
        IntPtr app = CameraService.App(), me = app == IntPtr.Zero ? IntPtr.Zero : CameraService.PlayerCreature();
        if (me != IntPtr.Zero)
        {
            var player = new CNWCCreature(me);
            IntPtr* nodes = (IntPtr*)player.Perception;
            int count = nodes == null ? 0 : player.PerceptionCount;
            for (int k = 0; k < count && n < MaxMarks; k++)
            {
                if (nodes[k] == IntPtr.Zero) continue;
                var node = new CNWVisibilityNode(nodes[k]);
                DbId root = default;
                if (Silhouetted(node.Marks))
                {
                    CGameObject go = new CClientExoApp(app).GetGameObject(node.Oid);
                    CNWCObject o = go.IsNull ? default : go.AsNWCObject();
                    if (!o.IsNull && o.Type == CameraService.CreatureType)
                    {
                        IntPtr body = new CNWCCreature(o.Ptr).GetModel(BodyPart, 1);
                        if (body != IntPtr.Zero) root = Facade.ObjectId(body);
                    }
                }
                sights[n++] = new Sight(node.Oid, node.Marks, root);
            }
        }
        Frame(sights[..n]);
    }

    /// <summary><c>CNWCCreature::GetModel</c>'s part argument for the creature's root body model.</summary>
    private const byte BodyPart = 255;

    /// <summary>Does a silhouette stay one? While its entry says so on the same scene object; with no entry, while
    /// the game still draws it at or under <see cref="EngineAlpha"/>, which is the game fading it out.</summary>
    private static bool Stays(Mark m, ReadOnlySpan<Sight> sights)
    {
        foreach (Sight seen in sights)
            if (seen.Oid == m.Oid) return Silhouetted(seen.Marks) && seen.Root == m.Root;
        return SceneDb.HitObjAlpha[m.Root.Index] <= EngineAlpha;
    }
}
