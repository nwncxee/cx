using System.Numerics;
using CX.Alloy.Seam;
using CX.Engine;

namespace CX.Alloy.Db;

/// <summary><b>The attachment placement</b>: the one term an attached object has that the transform step does not
/// apply, from either of two edges -- the seam's attach slots, which record the edge and build no behaviour (ADR
/// 0028 leaves behaviour lifetime with the game), or one the game installed through the unhooked
/// <c>GuiAurAttachToObject</c> @0x68b050, READ at <c>+0x30</c> (part) and <c>+0x84</c> (offset); it OVERWRITES <c>ObjPos</c>.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How deep the parent chain is followed before the placement gives up. A STACK guard, not a
    /// budget: the character-select list attaches each <c>ctl_pre_btn_char</c> to the previous one, so the chain
    /// is as long as the player has characters. A true cycle is caught exactly by <see cref="_inSnap"/> at any
    /// depth, so this only has to be past any chain content can build.</summary>
    private const int MaxAttachSnap = 256;

    private static int _snapDepth;
    private static long _attachSnaps, _attachDeep, _attachBadPart, _attachCycle, _behPlaced, _behForeign, _textOffsets;
    /// <summary>This object's placement is on the stack right now: reaching it again IS the cycle, whatever
    /// the depth.</summary>
    private static readonly bool[] _inSnap = new bool[SceneDb.MaxObjects];

    /// <summary>Text position offsets applied at compose this session.</summary>
    internal static long TextOffsetsApplied => _textOffsets;

    /// <summary>Place <paramref name="i"/> on the part it rides, from whichever of the two edges it has.
    /// Called at the top of the compose, before the staleness test -- which is what makes a MOVING parent
    /// recompose the child, since the move is noticed here and reported through <see cref="Moved"/>.</summary>
    internal static void SnapAttached(int i)
    {
        // A game-made CAurBehavior already writes this object's placement every animate (its own subclass
        // offset included), and the solve runs before the pose -- but only once it is actually SOLVING: one merely
        // installed on an object nothing steps (a GUI panel whose scene never animates) is placed from here.
        if (BehaviourPlacing(i)) return;
        Vector3 offset = default;
        int part;
        DbId up = SceneDb.ObjAttachTo[i];
        if (SceneDb.Objects.Valid(up))
        {
            part = ParentPart(up, SceneDb.ObjAttachPart[i]);
            // A TEXT object is the one case that still rides the parent's ROOT when its named anchor did not
            // resolve. Vanilla leaves such a label where its own SetPosition put it -- but CX cannot: measured
            // (.scratch/live/menu-db9), the value the game hands CAurString::SetPosition for these is a
            // PIXEL-space pair ((175,145), (175,135) a line apart) that is not a placement in the panel's
            // world at all, so honouring it puts the label nowhere. Riding the root is the engine's own
            // fallback for a NULL part name and lands the label on its control; what is still missing is the
            // text BOX that would then centre it there (see the note on the box below).
            if (part < 0 && SceneDb.ObjKindOf[i] == ObjKind.Text) { part = RootPart(up); ReportRooted(i, up); }
            if (part < 0) { _attachBadPart++; ReportLostAnchor(i, up); return; }
            LinkChild(i, up);
        }
        else if (!ShellAttach(i, out part, out offset)) return;

        if (_inSnap[i]) { _attachCycle++; ReportDeep(i, "closes a CYCLE"); return; }
        if (_snapDepth >= MaxAttachSnap) { _attachDeep++; ReportDeep(i, $"is deeper than {MaxAttachSnap} hops"); return; }
        DbId owner = SceneDb.PartObj[part];
        if (SceneDb.Objects.Valid(owner))
        {
            _snapDepth++;
            _inSnap[i] = true;
            // try/finally, not optimism: a throw that left the flag set would refuse this object's placement
            // for the rest of the session and report a cycle that is not there.
            try { ComposeIndex(owner.Index); }
            finally { _inSnap[i] = false; _snapDepth--; }
        }
        Matrix4x4 m = SceneDb.PartWorld[part];
        var p = new Vector3(m.M41, m.M42, m.M43) + offset;
        // CAurBehaviorAttachNoOrientation::Control @0x4a2bd0 puts the object's own orientation back after the solve.
        Quaternion q = SceneDb.ObjAttachMode[i] == SceneDb.AttachNoOrientation ? SceneDb.ObjRot[i] : RotOf(m);
        _attachSnaps++;
        _byMode[SceneDb.ObjAttachMode[i] & 7]++;
        p += TextOffset(i, q);
        if (SceneDb.ObjPos[i] == p && SceneDb.ObjRot[i] == q) return;
        // Through the facade, not into the columns alone: Gob::GetPosition answers the SHELL word and the
        // game's own code reads it by interior address, so a snap that moved only the column would be a lie.
        Facade.SetXform(SceneDb.Objects.IdAt(i), SceneDb.ObjFacade[i], p, q);
        Moved(i);
    }

    /// <summary>A text object's position offset, applied as <c>CAurBehaviorAttachTranslate::Control</c>
    /// @0x4a2960 applies it -- rotated into the frame the base attach just snapped onto, then added:
    /// <c>SetPosition(GetPosition() + GetOrientation() * offset)</c>. Zero for everything else, and applied
    /// only while the behaviour it was set for is the installed one -- one pointer load, no vtable call.</summary>
    private static Vector3 TextOffset(int i, in Quaternion q)
    {
        nint owner = SceneDb.TextOffsetBehavior[i];
        if (owner == 0)
        {
            // The usual case in a GUI panel: the translate attach is the DATABASE's own edge, so there is no
            // behaviour pointer to key the lifetime on. The attach-mode column stands in for it -- the next
            // attach clears the offset, which is exactly when the engine's behaviour would have been replaced.
            if (SceneDb.ObjAttachMode[i] != SceneDb.AttachTranslate) return default;
            Vector3 own = SceneDb.TextPosOffset[i];
            if (own == default) return default;
            _textOffsets++;
            return Vector3.Transform(own, q);
        }
        nint shell = SceneDb.ObjFacade[i];
        if (shell == 0 || new Gob(shell).Attach.Ptr != owner)
        { SceneDb.TextOffsetBehavior[i] = 0; return default; }
        _textOffsets++;
        return Vector3.Transform(SceneDb.TextPosOffset[i], q);
    }

    /// <summary>The second edge: a <c>CAurBehaviorCrossSceneAttach</c> the game installed through
    /// <c>SetBehavior</c> on an object CX has no attach edge for. The behaviour is READ, never called.
    /// <paramref name="offset"/> is the <c>WithOffset</c> subclass's constant world vector, added unrotated
    /// exactly as its <c>Control</c> @0x68c280 adds it.</summary>
    private static bool ShellAttach(int i, out int part, out Vector3 offset)
    {
        part = -1; offset = default;
        nint shell = SceneDb.ObjFacade[i];
        if (shell == 0) return false;
        nint beh = new Gob(shell).Attach.Ptr;
        if (beh == 0) return false;
        DbBehaviorKind kind = Kind(beh);
        if (kind != DbBehaviorKind.CrossSceneAttach && kind != DbBehaviorKind.CrossSceneAttachOffset)
        { _behForeign++; return false; }
        var att = new CAurBehaviorAttach((System.IntPtr)beh);
        if (att.Part == System.IntPtr.Zero || ResolvePart == null) return false;
        int p = ResolvePart(att.Part);
        if ((uint)p >= SceneDb.MaxParts || !SceneDb.Parts.LiveAt(p)) { _attachBadPart++; return false; }
        if (kind == DbBehaviorKind.CrossSceneAttachOffset)
            offset = new CAurBehaviorCrossSceneAttach((System.IntPtr)beh).Offset;
        part = p;
        // The link, from the other side: this object is attached to whatever owns that part, and the death
        // edge sweeps on it. Noted here as well as at the solve, because a control whose behaviour has not
        // solved yet is still a child -- and vanilla's own notice (the callback UpdateScenesAttach registers
        // on the parent) exists from the behaviour's CONSTRUCTOR, not from its first Control.
        NoteAttached(SceneDb.PartObj[p]);
        LinkChild(i, SceneDb.PartObj[p]);
        if (_attachParent[i].IsNull) _attachParent[i] = SceneDb.PartObj[p];
        _behPlaced++;
        return true;
    }

    /// <summary>No-silent-fallbacks: a rider whose anchor no longer resolves is NOT placed and keeps the world
    /// matrix it last had, which looks like a body part standing beside its creature. Said once per object,
    /// with the anchor it wanted and what that index holds now. A <c>-1</c> part is NOT "the root": both
    /// resolvers answer the root for a null or empty name, so it can only be a name that did not resolve.</summary>
    private static void ReportLostAnchor(int i, DbId up)
    {
        if (_saidLost.Count >= MaxLostSaid || !_saidLost.Add(i)) return;
        int part = SceneDb.ObjAttachPart[i];
        string holder = (uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part)
                      ? "o=" + SceneDb.PartObj[part].Index : "nothing (dead slot)";
        _log?.Always($"DbXform: \"{SceneDb.ObjModel[i]?.Name ?? "-"}\" (o={i}) rides part {part} of "
                   + $"\"{SceneDb.ObjModel[up.Index]?.Name ?? "-"}\" (o={up.Index}) by the name "
                   + $"\"{SceneDb.ObjAttachNode[i] ?? "(root)"}\", and that part belongs to {holder} -- it is "
                   + "NOT PLACED and keeps the transform it last had. Counted on /alloy as 'no parent part'.");
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidLost = new();
    private const int MaxLostSaid = 32;

    /// <summary>No-silent-fallbacks for the OTHER half of a lost anchor: a label whose named part did not
    /// resolve rides its parent's ROOT instead, which places it but not where the panel meant. Said once per
    /// object, bounded, with the label's own first line so the panel it belongs to is identifiable.</summary>
    private static void ReportRooted(int i, DbId up)
    {
        if (_saidRooted.Count >= MaxLostSaid || !_saidRooted.Add(i)) return;
        TextDoc? d = TextObjects.Of(SceneDb.Objects.IdAt(i));
        string text = d != null && d.LineCount > 0
                    ? System.Text.Encoding.Latin1.GetString(d.Lines[0].Bytes, 0, d.Lines[0].Length) : "";
        _log?.Always($"DbXform: the label \"{text}\" (o={i}) asked for a part of "
                   + $"\"{SceneDb.ObjModel[up.Index]?.Name ?? "-"}\" (o={up.Index}) that does not resolve, so it "
                   + $"rides that object's ROOT -- its place is then its position offset alone "
                   + $"(box {d?.MaxWidth ?? 0f:F2}x{d?.MaxHeight ?? 0f:F2}, alignH {d?.AlignH ?? 0f:F2}).");
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidRooted = new();

    /// <summary>Is <paramref name="part"/> a part of <paramref name="up"/>'s tree? The engine grows ONE part
    /// range per gob, so "belongs to the parent" is a single compare; CX makes every <c>Gob::Append</c>ed piece
    /// a child OBJECT, so a part of the parent's COMPOSITION counts as a part of the parent -- otherwise a name
    /// the game resolved on a grafted body part is refused as a lost anchor.</summary>
    private static int ParentPart(DbId up, int part)
    {
        if (part < 0 || part >= SceneDb.MaxParts || !SceneDb.Parts.LiveAt(part)) return -1;
        DbId owner = SceneDb.PartObj[part];
        if (owner.Value == up.Value) return part;
        for (int hop = 0; hop < MaxAttachSnap && SceneDb.Objects.Valid(owner); hop++)
        {
            if ((SceneDb.ObjSeamFlags[owner.Index] & (uint)SeamFlag.Composed) == 0) break;
            owner = SceneDb.ObjAttachTo[owner.Index];
            if (owner.Value == up.Value) { _composedAnchors++; return part; }
        }
        return -1;
    }

    /// <summary>Anchors that resolved through the parent's COMPOSITION rather than its own part range: how much
    /// of a creature the game addresses through a grafted piece, and zero if it never does.</summary>
    private static long _composedAnchors;

    /// <summary>The parent's root part, which is what <c>Gob::AttachToObject</c> resolves a NULL part name to.</summary>
    private static int RootPart(DbId up)
    {
        int first = SceneDb.ObjPartFirst[up.Index];
        return SceneDb.ObjPartCount[up.Index] > 0 && (uint)first < SceneDb.MaxParts
               && SceneDb.Parts.LiveAt(first) ? first : -1;
    }

    /// <summary>The chain ran past <see cref="MaxAttachSnap"/>, or closed on itself: walk it and say what it
    /// is, ONCE. The two want opposite fixes -- a bigger cap, or the seam slot that recorded an edge the engine
    /// would have replaced -- so the walk names every hop and marks the object the chain returns to.</summary>
    private static void ReportDeep(int i, string why)
    {
        // Bounded per OBJECT, not once per session: a single global latch was spent by the boot self-check's
        // own synthetic chain, and every real refusal for the rest of the run was then silent.
        if (_saidDeep.Count >= MaxLostSaid || !_saidDeep.Add(i)) return;
        string s = $"DbAnim: an attach chain {why} -- the objects on it are "
                 + "NOT PLACED (totals on /alloy). The chain from o=" + i + ":";
        var seen = new System.Collections.Generic.HashSet<int>();
        int at = i;
        for (int hop = 0; hop < 64; hop++)
        {
            if (!seen.Add(at)) { s += $"  -> o={at} **CYCLE CLOSES HERE**"; break; }
            DbId up = SceneDb.ObjAttachTo[at];
            int part = SceneDb.ObjAttachPart[at];
            string owner = (uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part)
                         ? SceneDb.PartObj[part].Index.ToString() : "-";
            s += $"  o={at} ({SceneDb.ObjKindOf[at]} \"{SceneDb.ObjModel[at]?.Name ?? "-"}\""
               + $" scene={SceneDb.ObjScene[at].Index} parts={SceneDb.ObjPartCount[at]})"
               + $" -attached to-> o={up.Index} at part {part} (owned by o={owner})";
            if (!SceneDb.Objects.Valid(up)) { s += "  [chain ends: parent not live]"; break; }
            at = up.Index;
        }
        _log?.Info(s);
    }

    private static readonly System.Collections.Generic.HashSet<int> _saidDeep = new();

    /// <summary>Placements by attach VARIANT (<see cref="SceneDb.ObjAttachMode"/>), so "which subclass the game
    /// installs" is a measurement rather than a guess. <c>Gob::AttachToObject</c> @0x43e0e0's switch has five
    /// arms; CX composes them as the plain one, plus the position offset for translate and the object's own
    /// orientation for no-orientation.</summary>
    private static readonly long[] _byMode = new long[8];

    private static string Variants()
    {
        string s = "";
        for (int m = 0; m < 5; m++)
            if (_byMode[m] != 0) s += $"{(s.Length == 0 ? "" : " ")}{Name(m)}={_byMode[m]}";
        return s.Length == 0 ? "" : $", variants {s}";
    }

    private static string Name(int m) => m switch
    {
        0 => "plain", 1 => "translate", 2 => "rotate", 3 => "no-orientation", 4 => "gui-scale", _ => "?",
    };

    // ---- the parent's side of the edge: who has to be carried when this object moves ----

    /// <summary>Parent -&gt; its first attached child, and child -&gt; the next child of the same parent: the
    /// attachment list vanilla keeps on the gob (<c>Gob::AddAttachment</c> @0x4424a0). Needed because a parent's
    /// move does not mark its children and the compose visits only what changed. Index 0 is the sentinel, never
    /// a live record; <see cref="_kidOf"/> is the parent a child is linked under now, so a re-attach MOVES it.</summary>
    private static readonly int[] _kidHead = new int[SceneDb.MaxObjects];
    private static readonly int[] _kidNext = new int[SceneDb.MaxObjects];
    private static readonly int[] _kidOf = new int[SceneDb.MaxObjects];
    private static int _kidDepth;
    private static long _kidPlaced, _kidDeep;

    /// <summary>Record that <paramref name="child"/> rides <paramref name="parent"/>, from whichever edge read
    /// the link. Cheap to repeat: an unchanged link is one compare.</summary>
    private static void LinkChild(int child, DbId parent)
    {
        int p = SceneDb.Objects.Valid(parent) ? parent.Index : 0;
        if (_kidOf[child] == p) return;
        UnlinkChild(child);
        if (p == 0 || p == child) return;
        _kidNext[child] = _kidHead[p];
        _kidHead[p] = child;
        _kidOf[child] = p;
    }

    /// <summary>Take <paramref name="child"/> off its parent's list (<c>Gob::RemoveAttachment</c>'s job).</summary>
    private static void UnlinkChild(int child)
    {
        int p = _kidOf[child];
        _kidOf[child] = 0;
        if (p != 0)
        {
            if (_kidHead[p] == child) _kidHead[p] = _kidNext[child];
            else
                for (int at = _kidHead[p]; at != 0; at = _kidNext[at])
                    if (_kidNext[at] == child) { _kidNext[at] = _kidNext[child]; break; }
        }
        _kidNext[child] = 0;
    }

    /// <summary>Every column this file keeps for <paramref name="i"/>, for a RECYCLED record: the link to its
    /// old parent, and the children that were riding the record's previous tenant.</summary>
    internal static void ForgetKids(int i)
    {
        UnlinkChild(i);
        for (int c = _kidHead[i]; c != 0; )
        {
            int next = _kidNext[c];
            _kidOf[c] = 0; _kidNext[c] = 0;
            c = next;
        }
        _kidHead[i] = 0;
    }

    /// <summary>Carry <paramref name="i"/>'s attached children, now that its own matrices are current. This is
    /// what <c>CAurBehaviorAttach::Control</c> does for the engine, one frame later than the parent's move
    /// rather than at it.</summary>
    private static void PlaceChildren(int i)
    {
        int c = _kidHead[i];
        if (c == 0) return;
        if (_kidDepth >= MaxAttachSnap) { _kidDeep++; return; }
        _kidDepth++;
        try
        {
            // next read BEFORE the compose: that compose may re-link or retire this child.
            for (int next; c != 0; c = next)
            {
                next = _kidNext[c];
                if (!SceneDb.Objects.LiveAt(c)) { UnlinkChild(c); continue; }
                if (_kidOf[c] != i) continue;
                // The child whose own climb brought us here is on the snap stack, and that climb places it
                // with this very matrix on the way back out. Carrying it again re-enters its SnapAttached,
                // which reads the stack flag as a CYCLE and refuses -- half a million refusals a session,
                // every one of them a placement that then happened anyway, and a real cycle lost among them.
                if (_inSnap[c]) continue;
                _kidPlaced++;
                ComposeIndex(c);
            }
        }
        finally { _kidDepth--; }
    }

    /// <summary>Attachment placements this session by edge, and the refusals: a chain that closed on itself,
    /// one past <see cref="MaxAttachSnap"/>, and a link whose part is gone.</summary>
    internal static string AttachStats()
        => $"attach: {_attachSnaps} placed ({_behPlaced} off a game-installed cross-scene behaviour)"
         + (_kidPlaced == 0 ? "" : $", {_kidPlaced} carried by a parent that moved")
         + Variants()
         + (_composedAnchors == 0 ? "" : $", {_composedAnchors} anchored on a GRAFTED piece of the parent")
         + (_kidDeep == 0 ? "" : $", {_kidDeep} carry(s) past the depth guard")
         + (_behForeign == 0 ? "" : $", {_behForeign} snap(s) left to a non-attach behaviour that has not solved yet")
         + (_attachCycle == 0 ? "" : $", {_attachCycle} CYCLES (not placed)")
         + (_attachDeep == 0 ? "" : $", {_attachDeep} TOO DEEP (not placed)")
         + (_attachBadPart == 0 ? "" : $", {_attachBadPart} with no parent part (not placed)");
}
