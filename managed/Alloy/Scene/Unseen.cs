using System;
using System.Diagnostics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>What <see cref="Unseen"/> holds between frames: the creatures drawing as silhouettes, the game objects
/// the last perception list named, and when it was read.</summary>
internal sealed class UnseenState
{
    internal readonly Unseen.Mark[] Marks = new Unseen.Mark[Unseen.MaxMarks];
    internal int Count;
    internal readonly uint[] Known = new uint[Unseen.MaxMarks];
    internal int KnownCount;
    internal long PolledAt;
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

    /// <summary>Seconds a creature takes to darken into its silhouette, or to brighten back out of it.</summary>
    internal const float FadeSeconds = 0.5f;

    /// <summary>Where the silhouette amount sits in the record's slot mask, above the palette bits: eight bits,
    /// 255 a whole silhouette. <c>CX_SLOT_SILHOUETTE</c> is its lowest bit.</summary>
    internal const int SlotShift = 13, SlotLevels = 255, SlotBit = 1 << SlotShift;

    /// <summary>Silhouettes held at once. ponytail: past it the rest draw see-through as the game has them, logged once;
    /// a perception list this long is the cue for a table keyed by object.</summary>
    internal const int MaxMarks = 256;

    /// <summary>The longest step one poll takes, so a hitch does not finish a fade unseen.</summary>
    private const float MaxStep = 0.1f;

    /// <summary>One perception entry: the game object it names, its marks, and the creature's scene object.</summary>
    internal readonly record struct Sight(uint Oid, byte Marks, DbId Root);

    /// <summary>One creature drawing as a silhouette, or fading out of one.</summary>
    internal record struct Mark(uint Oid, DbId Root, float Amount);

    /// <summary>An object's silhouette: how far in it is, 0 to 1, and whether it is heading in.</summary>
    internal readonly record struct Dim(float Amount, bool In);

    private static UnseenState _s = new();

    /// <summary>Forget every silhouette: an area change or a stand-down.</summary>
    internal static void Reset()
    {
        for (int k = 0; k < _s.Count; k++)
            if (SceneDb.Objects.Valid(_s.Marks[k].Root)) SceneDb.ObjUnseen[_s.Marks[k].Root.Index] = default;
        _s = new UnseenState();
    }

    /// <summary>Do these perception marks make a silhouette?</summary>
    internal static bool Silhouetted(byte marks) => (marks & (Seen | Invisible)) == 0 && (marks & Heard) != 0;

    /// <summary>The silhouette <paramref name="obj"/> is part of. A creature's pieces and everything riding it answer as it does.</summary>
    private static Dim DimOf(int obj)
    {
        DbId root = SceneDb.ObjRunRoot[obj];
        return SceneDb.ObjUnseen[SceneDb.Objects.Valid(root) ? root.Index : obj];
    }

    /// <summary>Is this object part of a silhouette, however far in?</summary>
    internal static bool Of(int obj) => DimOf(obj).Amount > 0f;

    /// <summary>How far into its silhouette this object is, 0 to 1.</summary>
    internal static float Amount(int obj) => DimOf(obj).Amount;

    /// <summary>The object alpha a part of <paramref name="obj"/> draws with, from the game's. Heading in, a silhouette
    /// is solid at <see cref="EngineAlpha"/> and fades under it; heading out it eases back to the game's alpha. Its
    /// unlit parts add light of their own, so they dim away as it darkens.</summary>
    internal static float Alpha(int obj, float engineAlpha, bool lit)
    {
        Dim d = DimOf(obj);
        if (d.Amount <= 0f) return engineAlpha;
        float solid = MathF.Min(1f, engineAlpha / EngineAlpha);
        float body = d.In ? solid : engineAlpha + (solid - engineAlpha) * d.Amount;
        return lit ? body : body * (1f - d.Amount);
    }

    /// <summary>The slot-mask bits that carry a silhouette amount.</summary>
    internal static int SlotMask(float amount) => (int)MathF.Round(Math.Clamp(amount, 0f, 1f) * SlotLevels) << SlotShift;

    /// <summary>The slot mask a lit part of <paramref name="obj"/> carries.</summary>
    internal static int Slots(int obj, int slots) => slots | SlotMask(DimOf(obj).Amount);

    /// <summary>This frame's perception entries, <paramref name="seconds"/> after the last: which creatures are heading
    /// into a silhouette, which out, and how far each has got.</summary>
    internal static void Frame(ReadOnlySpan<Sight> sights, float seconds)
    {
        UnseenState s = _s;
        foreach (Sight seen in sights)
        {
            if (!Silhouetted(seen.Marks) || !SceneDb.Objects.Valid(seen.Root)) continue;
            int at = Find(s, seen.Oid);
            if (at >= 0)
            {
                if (s.Marks[at].Root == seen.Root) continue;
                if (SceneDb.Objects.Valid(s.Marks[at].Root)) SceneDb.ObjUnseen[s.Marks[at].Root.Index] = default;
                s.Marks[at].Root = seen.Root;
                continue;
            }
            if (s.Count == MaxMarks)
            {
                if (!s.Spilled) Facade.Log?.Always($"Unseen: more than {MaxMarks} heard-but-unseen creatures -- the rest draw see-through");
                s.Spilled = true;
                break;
            }
            // a creature the last list did not name is arriving: it fades up as a silhouette, never through its own colours
            s.Marks[s.Count++] = new Mark(seen.Oid, seen.Root, Array.IndexOf(s.Known, seen.Oid, 0, s.KnownCount) < 0 ? 1f : 0f);
        }

        float step = seconds / FadeSeconds;
        int kept = 0;
        for (int k = 0; k < s.Count; k++)
        {
            Mark m = s.Marks[k];
            if (!SceneDb.Objects.Valid(m.Root)) continue;
            bool heading = Stays(m, sights);
            m.Amount = heading ? MathF.Min(1f, m.Amount + step) : MathF.Max(0f, m.Amount - step);
            if (!heading && m.Amount <= 0f) { SceneDb.ObjUnseen[m.Root.Index] = default; continue; }
            SceneDb.ObjUnseen[m.Root.Index] = new Dim(m.Amount, heading);
            s.Marks[kept++] = m;
        }
        s.Count = kept;

        s.KnownCount = Math.Min(sights.Length, s.Known.Length);
        for (int k = 0; k < s.KnownCount; k++) s.Known[k] = sights[k].Oid;
    }

    /// <summary>The mark for game object <paramref name="oid"/>, or -1.</summary>
    private static int Find(UnseenState s, uint oid)
    {
        for (int k = 0; k < s.Count; k++) if (s.Marks[k].Oid == oid) return k;
        return -1;
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
        long now = Stopwatch.GetTimestamp();
        float seconds = _s.PolledAt == 0 ? 0f : (float)Math.Min((now - _s.PolledAt) / (double)Stopwatch.Frequency, MaxStep);
        _s.PolledAt = now;
        Frame(sights[..n], seconds);
    }

    /// <summary><c>CNWCCreature::GetModel</c>'s part argument for the creature's root body model.</summary>
    private const byte BodyPart = 255;

    /// <summary>Is a silhouette heading in? While its entry says so; with no entry, while the game still draws it at or
    /// under <see cref="EngineAlpha"/>, which is the game fading it out.</summary>
    private static bool Stays(Mark m, ReadOnlySpan<Sight> sights)
    {
        foreach (Sight seen in sights)
            if (seen.Oid == m.Oid) return Silhouetted(seen.Marks);
        return SceneDb.HitObjAlpha[m.Root.Index] <= EngineAlpha;
    }
}
