using CX.Framework;

namespace CX.Alloy.Db;

public static partial class SceneDb
{
    /// <summary>The pieces an object OWNS, as an intrusive list: the head is here and each member's next is
    /// in <see cref="ObjSeamAppendNext"/>. Two kinds, one lifetime rule -- they die with their parent: what
    /// <c>Gob::Append</c> composed onto it (CX composes child records where the engine grew one part tree),
    /// and what a reference node instanced (<c>CreateReferenceObjects</c> @0x5421e0).</summary>
    internal static readonly DbId[] ObjSeamAppendHead = new DbId[MaxObjects], ObjSeamAppendNext = new DbId[MaxObjects];

    /// <summary>How many pieces one walk visits before it gives up. A composition is a handful of body parts
    /// and reference instances; a list this long is corrupt, and a walk that stops is better than one that
    /// runs forever on the game thread.</summary>
    public const int PieceWalkCap = 256;

    /// <summary>How deep a descent into a composition goes. A piece may own pieces, but not past this.</summary>
    public const int PieceDepthCap = 8;

    /// <summary>What the two link columns refused or survived, for <c>/alloy</c>.</summary>
    private sealed class PieceState
    {
        public long Capped, Rings, IndexRefusals, SelfRefusals, DeadRefusals;
        public bool SaidCapped, SaidRing;
    }

    private static PieceState _pieces = new();

    /// <summary>The pieces <paramref name="parent"/> owns, head first. The walk latches each piece's next
    /// link before handing the piece over, so a body that destroys what it is given still reaches the rest;
    /// it ends at a link whose record is gone, because that index has been recycled and its next link is the
    /// new tenant's; and it refuses to run forever, so a ring costs a log line rather than the session.</summary>
    public static PieceWalk Pieces(DbId parent)
        => new((uint)parent.Index < MaxObjects ? ObjSeamAppendHead[parent.Index] : default);

    /// <summary>One capped, ring-checked walk over a piece list. Floyd's two pointers find the ring at its
    /// first lap, which is what keeps a recursive descent over a ringed list from multiplying the cap by
    /// itself at every level.</summary>
    public struct PieceWalk
    {
        private DbId _next, _slow;
        private int _steps;

        internal PieceWalk(DbId head) { _next = _slow = head; _steps = 0; Current = default; }

        public PieceWalk GetEnumerator() => this;

        public DbId Current { get; private set; }

        public bool MoveNext()
        {
            if (_next.IsNull || !Objects.Valid(_next)) return false;
            Current = _next;
            _next = ObjSeamAppendNext[Current.Index];
            if (++_steps >= PieceWalkCap)
            {
                _pieces.Capped++;
                if (!_pieces.SaidCapped)
                {
                    _pieces.SaidCapped = true;
                    Seam.Facade.Log?.Always($"seam: a walk over one object's owned pieces reached {PieceWalkCap} "
                                            + "and STOPPED -- that list is corrupt, so pieces past the cap are "
                                            + "neither drawn nor released. Counted on /alloy.");
                }
                _next = default;
                return true;
            }
            if ((_steps & 1) == 0 && !_slow.IsNull && Objects.Valid(_slow)) _slow = ObjSeamAppendNext[_slow.Index];
            if (!_next.IsNull && _next.Value == _slow.Value)
            {
                _pieces.Rings++;
                if (!_pieces.SaidRing)
                {
                    _pieces.SaidRing = true;
                    Seam.Facade.Log?.Always("seam: an object's owned-piece list is a RING -- the walk stopped at "
                                            + "the first lap. Counted on /alloy.");
                }
                _next = default;
            }
            return true;
        }
    }

    /// <summary>Links <paramref name="piece"/> onto the head of what <paramref name="parent"/> owns, or
    /// refuses. The columns are keyed by record INDEX, so a second link of one index is what makes a ring: the
    /// piece's next would be the head that is already that index. Refused and named rather than written.</summary>
    public static bool PieceLink(DbId parent, DbId piece)
    {
        if (!Objects.Valid(parent) || !Objects.Valid(piece))
        {
            _pieces.DeadRefusals++;
            Say(_pieces.DeadRefusals, $"seam: a link of o={piece.Index} onto o={parent.Index} names a record that "
                                    + "is not live -- refused. Counted on /alloy.");
            return false;
        }
        if (parent.Index == piece.Index)
        {
            _pieces.SelfRefusals++;
            Say(_pieces.SelfRefusals, $"seam: o={parent.Index} was asked to own ITSELF -- refused. Counted on /alloy.");
            return false;
        }
        // Every LINK on the list, live or not: a dead handle whose index has been recycled is exactly the one
        // that rings it, and the walk stops at a dead link rather than reading past it.
        DbId at = ObjSeamAppendHead[parent.Index];
        for (int n = 0; n < PieceWalkCap && !at.IsNull; n++)
        {
            if (at.Index == piece.Index)
            {
                _pieces.IndexRefusals++;
                Say(_pieces.IndexRefusals, $"seam: o={parent.Index} already owns record index {piece.Index}, so "
                                         + "linking it again would RING that list -- refused. The piece it names "
                                         + "was never taken off the list when it died. Counted on /alloy.");
                return false;
            }
            at = ObjSeamAppendNext[at.Index];
        }
        ObjSeamAppendNext[piece.Index] = ObjSeamAppendHead[parent.Index];
        ObjSeamAppendHead[parent.Index] = piece;
        return true;
    }

    /// <summary>Splices <paramref name="piece"/> out of what <paramref name="parent"/> owns and answers
    /// whether it was there. The piece's own next link is left alone: the caller destroys it next, and the
    /// record reset clears both columns.</summary>
    public static bool PieceUnlink(DbId parent, DbId piece)
    {
        if ((uint)parent.Index >= MaxObjects || piece.IsNull) return false;
        DbId prev = default;
        foreach (DbId c in Pieces(parent))
        {
            if (c.Value == piece.Value)
            {
                DbId next = ObjSeamAppendNext[piece.Index];
                if (prev.IsNull) ObjSeamAppendHead[parent.Index] = next;
                else ObjSeamAppendNext[prev.Index] = next;
                return true;
            }
            prev = c;
        }
        return false;
    }

    /// <summary>Drops the whole list: the parent owns nothing after this. The pieces themselves are the
    /// caller's to destroy.</summary>
    public static void PieceDropAll(DbId parent)
    {
        if ((uint)parent.Index < MaxObjects) ObjSeamAppendHead[parent.Index] = default;
    }

    /// <summary>Clears both columns for a recycled record index: inherited, the next tenant's scene moves
    /// drag the last one's pieces along.</summary>
    public static void PieceForget(int index)
    {
        if ((uint)index >= MaxObjects) return;
        ObjSeamAppendHead[index] = default;
        ObjSeamAppendNext[index] = default;
    }

    /// <summary>Are both of a record index's link columns clear -- it owns nothing, and is in no owner's
    /// list? What a recycled index must start as.</summary>
    internal static bool PieceLinksCleared(int index)
        => (uint)index < MaxObjects && ObjSeamAppendHead[index].IsNull && ObjSeamAppendNext[index].IsNull;

    /// <summary>Plants a ring: the shape no writer will make, which is the only way to check that the walks
    /// survive one. Self-checks only.</summary>
    internal static void PieceRingForCheck(DbId parent, DbId piece)
    {
        ObjSeamAppendHead[parent.Index] = piece;
        ObjSeamAppendNext[piece.Index] = piece;
    }

    /// <summary>What the lists refused and survived, for the seam's <c>/alloy</c> line. Empty while nothing
    /// has gone wrong, which is the normal session.</summary>
    public static string PieceStats()
        => (_pieces.Capped == 0 ? "" : $", {_pieces.Capped} WALKS CAPPED")
         + (_pieces.Rings == 0 ? "" : $", {_pieces.Rings} RINGED LISTS")
         + (_pieces.IndexRefusals == 0 ? "" : $", {_pieces.IndexRefusals} LINKS REFUSED (index already owned)")
         + (_pieces.SelfRefusals == 0 ? "" : $", {_pieces.SelfRefusals} SELF-LINKS REFUSED")
         + (_pieces.DeadRefusals == 0 ? "" : $", {_pieces.DeadRefusals} LINKS REFUSED (record not live)");

    /// <summary>Said the first time and at every power of two after it, so a storm costs a handful of lines.</summary>
    private static void Say(long n, string line)
    {
        if ((n & (n - 1)) == 0) Seam.Facade.Log?.Always(line);
    }
}
