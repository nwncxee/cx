using CX.Alloy.Seam;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>What the owned-piece list promises: the writer refuses the shapes that ring it, and a walk over a
/// list that is already ringed or impossibly long stops and says so instead of holding the game thread.</summary>
public static partial class SceneDb
{
    internal static bool PieceSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneDb.Pieces.SelfCheck");
        PieceState saved = _pieces;
        _pieces = new PieceState();
        try
        {
            PieceOrder(check);
            PieceRefusals(check);
            PieceRingWalk(check);
            PieceCappedWalk(check);
            PieceLatchedNext(check);
            PieceDepth(check);
        }
        finally
        {
            _pieces = saved;
        }
        log.Info(check.Ok ? "SceneDb.Pieces.SelfCheck: passed"
                          : "SceneDb.Pieces.SelfCheck: FAILED -- a corrupt owned-piece list could hang the "
                          + "game thread, or a good one is being walked wrong");
        return check.Ok;
    }

    /// <summary>A record with both link columns clear, which is what a folded one starts as.</summary>
    private static DbId PieceSubject()
    {
        DbId id = Objects.Alloc();
        if (!id.IsNull) PieceForget(id.Index);
        return id;
    }

    private static int PieceCount(DbId parent)
    {
        int n = 0;
        foreach (DbId _ in Pieces(parent)) n++;
        return n;
    }

    /// <summary>Two pieces walk head first, and nothing is counted: the guards cost a good list nothing.</summary>
    private static void PieceOrder(Checker check)
    {
        DbId body = PieceSubject(), first = PieceSubject(), second = PieceSubject();
        check.Expect(PieceLink(body, second) && PieceLink(body, first),
                     "two pieces would not link onto one body");
        int at = 0;
        DbId[] seen = new DbId[4];
        foreach (DbId c in Pieces(body)) { if (at < seen.Length) seen[at] = c; at++; }
        check.Expect(at == 2, $"a two-piece list walked {at} pieces");
        check.Expect(seen[0].Value == first.Value && seen[1].Value == second.Value,
                     "the walk did not yield the last-linked piece first");
        check.Expect(_pieces is { Capped: 0, Rings: 0, IndexRefusals: 0, SelfRefusals: 0, DeadRefusals: 0 },
                     "an ordinary list moved one of the refusal tallies");
        PieceDropAll(body);
        Objects.Free(second); Objects.Free(first); Objects.Free(body);
    }

    /// <summary>The three links that would ring a list: an object onto itself, a dead handle, and -- the one a
    /// session actually reaches -- a handle whose INDEX is already on the list because the last tenant of that
    /// index was never taken off it.</summary>
    private static void PieceRefusals(Checker check)
    {
        DbId body = PieceSubject();
        check.Expect(!PieceLink(body, body) && _pieces.SelfRefusals == 1,
                     "an object was allowed to own itself");
        check.Expect(PieceLinksCleared(body.Index), "the refused self-link wrote a column anyway");

        // A live index under a generation nobody minted: the handle a list keeps after its piece died.
        DbId ghost = DbId.Make(body.Index, body.Gen + 7);
        check.Expect(!Objects.Valid(ghost), "the ghost handle is live, so it checks nothing");
        check.Expect(!PieceLink(body, ghost) && _pieces.DeadRefusals == 1,
                     "a link of a record that is not live was allowed");

        DbId stale = PieceSubject();
        check.Expect(PieceLink(body, stale), "the first piece would not link");
        Objects.Free(stale);
        DbId tenant = Objects.Alloc();
        check.Expect(tenant.Index == stale.Index && tenant.Value != stale.Value,
                     "the test really does recycle the dead piece's index under a new generation");
        PieceForget(tenant.Index);
        check.Expect(!PieceLink(body, tenant) && _pieces.IndexRefusals == 1,
                     "the new tenant of an index still on the list was linked -- that writes a ring");
        check.Expect(ObjSeamAppendHead[body.Index].Value == stale.Value && ObjSeamAppendNext[tenant.Index].IsNull,
                     "the refused link changed the list");

        PieceDropAll(body);
        Objects.Free(tenant); Objects.Free(body);
    }

    /// <summary>A ringed list is walked once round and stopped, counted and said. Both the one-piece ring and
    /// the two-piece one, because a check that only sees a piece pointing at itself misses the pair.</summary>
    private static void PieceRingWalk(Checker check)
    {
        DbId body = PieceSubject(), a = PieceSubject(), b = PieceSubject();
        PieceRingForCheck(body, a);
        int n = PieceCount(body);
        check.Expect(n == 1 && _pieces.Rings == 1,
                     $"a piece whose next is itself walked {n} pieces and counted {_pieces.Rings} rings");

        ObjSeamAppendHead[body.Index] = a;
        ObjSeamAppendNext[a.Index] = b;
        ObjSeamAppendNext[b.Index] = a;
        n = PieceCount(body);
        check.Expect(n <= PieceWalkCap && _pieces.Rings == 2,
                     $"a two-piece ring walked {n} pieces and counted {_pieces.Rings} rings");
        check.Expect(PieceStats().Contains("RINGED"), "a ringed list is not named on the period line");

        PieceForget(a.Index); PieceForget(b.Index); PieceDropAll(body);
        Objects.Free(b); Objects.Free(a); Objects.Free(body);
    }

    /// <summary>A list longer than any composition stops at the cap, counted -- the second line of defence,
    /// for a shape the writer's refusals did not catch.</summary>
    private static void PieceCappedWalk(Checker check)
    {
        DbId body = PieceSubject();
        DbId[] made = new DbId[PieceWalkCap + 4];
        bool linked = true;
        for (int i = 0; i < made.Length; i++)
        {
            made[i] = PieceSubject();
            linked &= !made[i].IsNull && PieceLink(body, made[i]);
        }
        check.Expect(linked, "the long list would not build");
        int n = PieceCount(body);
        check.Expect(n == PieceWalkCap && _pieces.Capped == 1,
                     $"a list of {made.Length} pieces walked {n}, cap {PieceWalkCap}, counted {_pieces.Capped}");
        check.Expect(PieceStats().Contains("CAPPED"), "a capped walk is not named on the period line");
        PieceDropAll(body);
        for (int i = made.Length - 1; i >= 0; i--)
        {
            if (made[i].IsNull) continue;
            PieceForget(made[i].Index);
            Objects.Free(made[i]);
        }
        Objects.Free(body);
    }

    /// <summary>The release walks this list and destroys what it is handed, which clears the piece's own next
    /// link. The walk latches that link first, so the rest of the list is still released.</summary>
    private static void PieceLatchedNext(Checker check)
    {
        DbId body = PieceSubject(), a = PieceSubject(), b = PieceSubject();
        check.Expect(PieceLink(body, b) && PieceLink(body, a), "the two pieces would not link");
        int n = 0;
        foreach (DbId c in Pieces(body))
        {
            n++;
            PieceForget(c.Index);
            Objects.Free(c);
        }
        check.Expect(n == 2, $"a walk whose body destroyed each piece reached {n} of 2");
        PieceDropAll(body);
        Objects.Free(body);
    }

    /// <summary>A composition grafted deeper than the depth cap stops at it: a descent is bounded by the cap,
    /// not by how deep the data goes.</summary>
    private static void PieceDepth(Checker check)
    {
        int levels = PieceDepthCap + 4;
        DbId[] chain = new DbId[levels];
        bool built = true;
        for (int i = 0; i < levels; i++)
        {
            chain[i] = PieceSubject();
            built &= !chain[i].IsNull;
            if (i > 0 && built) built &= PieceLink(chain[i - 1], chain[i]);
        }
        check.Expect(built, "the deep chain would not build");
        var v = new PieceDepthVisitor();
        PartSlots.ForEach(chain[0], 0, true, ref v);
        check.Expect(v.Levels == PartSlots.MaxGraft + 1,
                     $"a descent over {levels} levels reached {v.Levels}, cap {PartSlots.MaxGraft}");
        for (int i = levels - 1; i >= 0; i--)
        {
            if (chain[i].IsNull) continue;
            PieceForget(chain[i].Index);
            Objects.Free(chain[i]);
        }
    }

    /// <summary>Counts the records one part-slot descent reaches, which is what the depth cap bounds.</summary>
    private struct PieceDepthVisitor : IPartSlots
    {
        public int Levels;
        public int At(int part, int slot) => 0;
        public void Done(DbId obj, int changed) => Levels++;
    }
}
