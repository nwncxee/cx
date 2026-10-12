using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>Everything about the seam that can be proven in a stub host -- no engine, no GL, no game. Covers
/// the measured slot order on BOTH OS layouts, the shell's layout against the engine's offsets, the <c>DbId</c>
/// round trip through the header, pointer normalisation of a sub-object vptr, SafePointer back-reference
/// nulling, the poison path and two-frame quarantine, and a setter leaving shell word and column agreeing.</summary>
internal static unsafe class FacadeSelfCheck
{
    internal static bool Run(ExtenderLog log)
    {
        var check = new Checker(log);
        Vtables(check);
        Layout(check);
        Shells(check, log);
        Lod(check);
        return check.Ok;
    }

    /// <summary>The <c>.lod</c> read and the part range it builds: <c>NewCAurObject</c>'s sscanf fields, its
    /// defaults where the file stops, and the level tags <see cref="SceneDb.LodHidden"/> reads.</summary>
    private static void Lod(Checker check)
    {
        ObjectFns.ParseLod("plc_tree\n20.5 plc_tree_l1\n40 plc_tree_l2\n", out string m0, out float d1, out string m1, out float d2, out string m2);
        check.Expect(m0 == "plc_tree" && d1 == 20.5f && m1 == "plc_tree_l1" && d2 == 40f && m2 == "plc_tree_l2",
            ".lod: level-0 model, near, level-1 model, far, level-2 model");
        ObjectFns.ParseLod("plc_tree 20 plc_tree_l1", out m0, out d1, out m1, out d2, out m2);
        check.Expect(m1 == "plc_tree_l1" && d1 == 20f && d2 == 99999f && m2 == "",
            ".lod: a file that stops after level 1 keeps the engine's never-switch far distance and no level 2");
        ObjectFns.ParseLod("plc_tree x plc_tree_l1", out m0, out d1, out m1, out _, out _);
        check.Expect(m0 == "plc_tree" && d1 == 99999f && m1 == "",
            ".lod: an unparsable distance stops the scan there, as sscanf does");
        ObjectFns.ParseLod(null, out m0, out d1, out _, out _, out _);
        check.Expect(m0 == "" && d1 == 99999f, "no .lod: no names, never switch");

        static ModelDef Dummies(string name, int n)
        {
            var m = new ModelDef { Name = name };
            m.Nodes = new NodeDef[n];
            for (int k = 0; k < n; k++)
                m.Nodes[k] = new NodeDef { Name = name + k, Index = k, Parent = k - 1, Kind = NodeKind.Dummy,
                                           Children = k + 1 < n ? new[] { k + 1 } : System.Array.Empty<int>() };
            return m;
        }
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { check.Expect(false, "no object slot for the LOD tree check"); return; }
        try
        {
            DbFeed.InitObject(obj.Index);
            bool built = PartTree.Build(obj, Dummies("cxtest_l0", 3),
                new PartTree.TreeOptions { Lod1 = Dummies("cxtest_l1", 2), Lod2 = Dummies("cxtest_l2", 1) });
            int first = SceneDb.ObjPartFirst[obj.Index], n = SceneDb.ObjPartCount[obj.Index];
            check.Expect(built && n == 6, $"the three trees share one range (got {n} parts)");
            if (n != 6) return;
            check.Expect(SceneDb.PartLodTree[first] == 1 && SceneDb.PartLodTree[first + 2] == 1
                         && SceneDb.PartLodTree[first + 3] == 2 && SceneDb.PartLodTree[first + 5] == 3,
                "each tree is tagged level + 1");
            check.Expect(SceneDb.PartParent[first + 3] == -1 && SceneDb.PartParent[first + 4] == first + 3,
                "an alternate is its own root, its parents inside its own tree");
            check.Expect(!SceneDb.LodHidden(1, 0) && SceneDb.LodHidden(2, 0) && SceneDb.LodHidden(1, 1)
                         && !SceneDb.LodHidden(2, 1) && !SceneDb.LodHidden(0, 2),
                "level 0 shows tree 1 only, level 1 tree 2 only, and an untagged part shows at every level");
            PartTree.Build(obj, Dummies("cxtest_plain", 2));
            check.Expect(SceneDb.PartLodTree[SceneDb.ObjPartFirst[obj.Index]] == 0,
                "an object with no alternates stays untagged");
        }
        finally
        {
            PartTree.Free(obj);
            SceneDb.Objects.Free(obj);
        }
    }

    /// <summary>What <c>NewCAurObject</c> answers when the model does not load, which is two different answers.
    /// A model the resource lookup does not know is a NULL, as the engine's own factory answers it. A model
    /// whose resource IS there and CX could not read mints a record with NO model instead: <c>CGuiObject::SetModel</c>
    /// @0x586520 falls back to <c>GUI_empty</c> and dereferences it at +0x45 unchecked, so a NULL there faults.</summary>
    private static void Factory(Checker check)
    {
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels, missing = ObjectFns.MissingModels;
        try
        {
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            check.Expect(ObjectFns.CreateForCheck("cx_absent_model") == 0,
                "a model the resource lookup does not know must still be a NULL, the way the engine answers it");

            long objects = SceneDb.Objects.Live;
            nint shell = ObjectFns.CreateForCheck("cx_present_unreadable");
            check.Expect(shell != 0,
                "a model whose resource is present and unreadable must mint an object, not the NULL the engine's "
                + "GUI path dereferences");
            if (shell == 0) return;
            DbId id = Facade.Object(shell, SeamSlot.Unnamed);
            check.Expect(!id.IsNull, "the minted record did not come back from its shell");
            check.Expect(SceneDb.ObjModel[id.Index] == null && SceneDb.ObjPartCount[id.Index] == 0,
                "an object minted for an unreadable model must hold no model and no parts");
            check.Expect(ObjectFns.UnreadableModels == unreadable + 1 && ObjectFns.Stats().Contains("unreadable-model="),
                "the unreadable-model refusal must be counted on the period line");
            ObjectSlots.DestroyRecord(id);
            check.Expect(SceneDb.Objects.Live == objects, "the minted record was not released again");
        }
        // The counter goes back: a session's line must start at zero, or this check is the first thing the
        // next crash report sees on it.
        finally
        {
            ObjectFns.ResourcePresent = was;
            ObjectFns.UnreadableModels = unreadable;
            ObjectFns.MissingModels = missing;
        }
    }

    /// <summary>A panel page with more riders than the shell's attach list keeps (17, <c>Gob::AddAttachment</c>
    /// @0x5424a0): every one of them follows the page into its scene and out again -- the character sheet's
    /// missing ability labels and the tabs that leaked into each other.</summary>
    private static void Riders(Checker check)
    {
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels;
        const int N = 40;
        var kids = new DbId[N];
        DbId page = default, sc = default;
        try
        {
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            nint ps = ObjectFns.CreateForCheck("cx_present_unreadable");
            page = Facade.Object(ps, SeamSlot.Unnamed);
            for (int i = 0; i < N; i++)
            {
                kids[i] = Facade.Object(ObjectFns.CreateForCheck("cx_present_unreadable"), SeamSlot.Unnamed);
                SceneDb.SetAttachTo(kids[i].Index, page);
            }
            SceneDb.SetAttachTo(kids[3].Index, default);
            sc = DbScene.Create("sceneGUI_cx_riders");
            ObjectSlots.JoinScene(page, ps, sc, 0);
            int inScene = 0;
            for (int i = 0; i < N; i++) if (SceneDb.ObjScene[kids[i].Index].Value == sc.Value) inScene++;
            check.Expect(inScene == N - 1, $"every rider of a page joins its scene, past the attach list's 17 ({inScene} of {N - 1})");
            check.Expect(SceneDb.ObjScene[kids[3].Index].IsNull, "a detached rider does not follow its old parent");
            ObjectSlots.JoinScene(page, ps, default, 0);
            int left = 0;
            for (int i = 0; i < N; i++) if (SceneDb.ObjScene[kids[i].Index].IsNull) left++;
            check.Expect(left == N, $"every rider leaves with its page ({left} of {N})");
            // A button list chains each row to the row before it: a chain far deeper than any recursion cap.
            for (int i = 1; i < N; i++) SceneDb.SetAttachTo(kids[i].Index, kids[i - 1]);
            SceneDb.SetAttachTo(kids[0].Index, page);
            ObjectSlots.JoinScene(page, ps, sc, 0);
            int chained = 0;
            for (int i = 0; i < N; i++) if (SceneDb.ObjScene[kids[i].Index].Value == sc.Value) chained++;
            check.Expect(chained == N, $"a chain of riders {N} deep follows its page whole ({chained} of {N})");
            ObjectSlots.JoinScene(page, ps, default, 0);
        }
        finally
        {
            for (int i = 0; i < N; i++) if (!kids[i].IsNull) ObjectSlots.DestroyRecord(kids[i]);
            if (!page.IsNull) ObjectSlots.DestroyRecord(page);
            if (!sc.IsNull) DbScene.Destroy(sc);
            ObjectFns.ResourcePresent = was;
            ObjectFns.UnreadableModels = unreadable;
        }
    }

    private static void Vtables(Checker check)
    {
        // The counts in CX-183 section 1, which the generator must reproduce.
        (SeamTable t, int lin, int win)[] expect =
        {
            (SeamTable.Gob, 94, 93),
            (SeamTable.Scene, 88, 87),
            (SeamTable.Camera, 41, 33),
            (SeamTable.StringGob, 143, 97),
            (SeamTable.CAurString, 52, 51),
            (SeamTable.CameraGob, 94, 93),
        };
        foreach ((SeamTable t, int lin, int win) e in expect)
        {
            check.Expect(SeamVtables.Linux(e.t).Length == e.lin,
                $"{e.t}'s Linux table is {SeamVtables.Linux(e.t).Length} slots, not the measured {e.lin}");
            check.Expect(SeamVtables.Windows(e.t).Length == e.win,
                $"{e.t}'s Windows table is {SeamVtables.Windows(e.t).Length} slots, not the measured {e.win}");
        }

        // The tables must still be the ones the generator produced from the TSV. A hand edit, or a TSV the
        // generator was never re-run against, is a silently wrong slot -- the kind that dispatches
        // Camera::HitCheck where the game expects Camera::HitCheck(int, int, ...).
        check.Expect(SeamVtables.Hash() == SeamVtables.Digest,
            $"SeamVtables.g.cs no longer matches its generator's digest (re-run tools/seam/gen-vtables.py); "
            + $"table hash {SeamVtables.Hash():x}, expected {SeamVtables.Digest:x}");

        // Rule 3's other half: the secondary vftables must CARRY the derived class's overrides on Windows,
        // not inherit the base's. Camera's Gob table and a plain Gob's were byte-identical once.
        ReadOnlySpan<short> wg = SeamVtables.Windows(SeamTable.Gob), wcg = SeamVtables.Windows(SeamTable.CameraGob);
        int differing = 0;
        for (int i = 0; i < wg.Length && i < wcg.Length; i++) if (wg[i] != wcg[i]) differing++;
        check.Expect(differing == 8,
            $"Camera's Windows Gob table differs from a plain Gob's in {differing} slots, not the measured 8");
        check.Expect(SeamVtables.Windows(SeamTable.CAurString)[0] != SeamVtables.Windows(SeamTable.Gob)[0],
            "a text shell's Windows CAurString table must carry StringGob's destructor, not Gob's");

        // Rule 1: the destructor pair collapses -- Linux 0 and 1 are both Windows 0, and nothing else shifts.
        ReadOnlySpan<short> gl = SeamVtables.Linux(SeamTable.Gob), gw = SeamVtables.Windows(SeamTable.Gob);
        check.Expect(gw[0] == gl[1], "Windows slot 0 of Gob is not Linux's DELETING destructor");

        // Rule 2: MSVC reverses adjacent overloads. Gob's two ReplaceTexture overloads are Linux 12/13 and
        // Windows 11/12 the other way round; GetMinimumBoundingBox's two are declared far apart and do NOT swap.
        check.Expect(gw[11] == gl[13] && gw[12] == gl[12],
            "the ReplaceTexture overload pair is not reversed in the Windows table");
        check.Expect(gw[63] == gl[64] && gw[88] == gl[89],
            "the two GetMinimumBoundingBox overloads moved; they are declared far apart and must not swap");

        // Rule 3: GCC duplicates a secondary-base override into the primary vtable, MSVC does not -- so
        // Camera's primary is 41 on Linux and 33 on Windows, and StringGob's is 143 against 97.
        check.Expect(SeamVtables.Linux(SeamTable.Camera).Length - SeamVtables.Windows(SeamTable.Camera).Length == 8,
            "Camera's primary table should lose exactly eight slots on Windows");
        check.Expect(SeamVtables.Linux(SeamTable.StringGob).Length - SeamVtables.Windows(SeamTable.StringGob).Length == 46,
            "StringGob's primary table should lose exactly 46 slots on Windows");

        // Aliasing: one implementation, one logical slot. A CAurString thunk and the StringGob primary slot
        // behind it share an id, so one registration fills both.
        int viaString = SeamVtables.Logical(SeamTable.CAurString, 4);
        int viaPrimary = SeamVtables.Logical(SeamTable.StringGob, 94);
        check.Expect(viaString == viaPrimary && viaString >= 0,
            "CAurString slot 4 and StringGob slot 94 are the same implementation and must share one logical id");
        // An override of a virtual BOTH bases declare stays in MSVC's primary too: nwmain.exe's StringGob vftable
        // (rva 0xd81440) holds StringGob::GetExternalData at Windows 30, the slot a GUI click reads a label through.
        check.Expect(SeamVtables.Windows(SeamTable.StringGob)[30] == SeamVtables.Windows(SeamTable.CAurString)[8],
            "Windows StringGob slot 30 must be StringGob::GetExternalData (measured), not Gob's -- a label click is lost");

        // Every slot of every table resolves to a logical id: a -1 would be a hole the builder fills with
        // poison and nobody asked for.
        foreach (SeamTable t in Enum.GetValues<SeamTable>())
        {
            bool holes = false;
            foreach (short s in SeamVtables.Linux(t)) holes |= s < 0;
            foreach (short s in SeamVtables.Windows(t)) holes |= s < 0;
            check.Expect(!holes, $"{t}'s slot order has an unfilled index");
        }

        // The offset-to-top words, which are what makes a sub-object pointer normalise without a thunk.
        check.Expect(SeamVtables.OffsetToTop(SeamTable.Gob) == 0, "a primary table's offset-to-top must be 0");
        check.Expect(SeamVtables.OffsetToTop(SeamTable.CameraGob) == -8,
            "Camera's Gob sub-object sits at +8, so its table's offset-to-top is -8");
        check.Expect(SeamVtables.OffsetToTop(SeamTable.CAurString) == -0x4c0,
            "a text shell's CAurString sub-object sits at +0x4c0, so its table's offset-to-top is -0x4c0");

        // The by-value flags: the 17 slots whose declaration differs per OS.
        check.Expect(SeamVtables.ByValue(SeamVtables.Logical(SeamTable.Gob, 25)), "Gob::GetPosition returns a Vector by value");
        check.Expect(!SeamVtables.ByValue(SeamVtables.Logical(SeamTable.Gob, 3)), "Gob::GetName does not return by value");
    }

    private static void Layout(Checker check)
    {
        // The sizes are the engine's own operator new arguments; the offsets are the words game code reads
        // without a vtable. A drift here is a facade the game writes past the end of.
        check.Expect(SeamLayout.GobSize == 0x4c0, "sizeof(Gob) is NewCAurObject's operator_new(0x4c0)");
        check.Expect(SeamLayout.SceneSize == 0x518, "sizeof(Scene) is NewCAurScene's operator_new(0x518)");
        check.Expect(SeamLayout.StringGobSize == 0x558, "sizeof(StringGob) is NewCAurString's operator_new(0x558)");
        check.Expect(SeamLayout.StringGobString == SeamLayout.GobSize,
            "the CAurString sub-object starts exactly at sizeof(Gob); RemoveAuroraString's -0x4c0 depends on it");
        check.Expect(SeamLayout.CameraSize >= 0xe68, "sizeof(Camera) is 0xe68 on Linux and 0xe70 on Windows");
        check.Expect(SeamLayout.GobVisualXform + SeamLayout.GobVisualXformSize <= SeamLayout.GobSize,
            "the embedded CAurObjectVisualTransformData must fit inside the shell");
        check.Expect(SeamLayout.GobAttachList + 16 <= SeamLayout.GobSize,
            "the attachment list the engine reallocs in place must fit inside the shell");
        foreach (int off in new[]
        {
            SeamLayout.GobSafeList, SeamLayout.GobName, SeamLayout.GobScene, SeamLayout.GobRoot,
            SeamLayout.GobPos, SeamLayout.GobRot, SeamLayout.GobBehavior, SeamLayout.GobExternal,
            SeamLayout.GobCallbackList, SeamLayout.GobScaleFinal, SeamLayout.GobTriMeshArea,
        })
            check.Expect(off > 0 && off < SeamLayout.GobSize, $"the mirrored word at 0x{off:x} falls outside the shell");
        check.Expect(SeamLayout.SafeListAt(FacadeKind.Camera) == SeamLayout.CameraGobSub + SeamLayout.GobSafeList,
            "a camera's SafePointer list is its Gob sub-object's, i.e. +8 further on");
    }

    private static void Shells(Checker check, ExtenderLog log)
    {
        // Build the tables against a registry with nothing in it: every slot is then the poison entry, which is
        // what this check wants -- it proves the builder, the header and the quarantine, not the slot bodies.
        bool fresh = !Facade.Built;
        if (fresh && Facade.Build(new SeamRegistry(), log, census: false) < 0)
        {
            check.Expect(false, "the vtable block could not be built");
            return;
        }

        foreach (SeamTable t in Enum.GetValues<SeamTable>())
            check.Expect(Facade.Poison(t) != 0 && Facade.Poison(t) != Facade.Table(t),
                $"{t} has no poison twin, or it is the live table");

        Factory(check);
        Riders(check);

        DbId id = SceneDb.Objects.Alloc();
        check.Expect(!id.IsNull, "the objects table would not hand out a record");
        if (id.IsNull) return;

        nint shell = Facade.Mint(FacadeKind.Object, id);
        check.Expect(shell != 0, "a Gob-shaped shell could not be minted");
        if (shell == 0) { SceneDb.Objects.Free(id); return; }

        // The DbId round trip, and that it survives through the vptr rather than through any engine field.
        check.Expect(Facade.Shell(shell) == shell, "a primary pointer must normalise to itself");
        check.Expect(Facade.Object(shell, SeamSlot.Unnamed).Value == id.Value, "the shell did not carry its DbId back");
        check.Expect(*(nint*)shell == Facade.Table(SeamTable.Gob), "the shell's vptr is not the Gob table");

        // A camera shell's two vptrs, and the +8 sub-object normalising without a thunk.
        DbId cam = SceneDb.Cameras.Alloc();
        nint camShell = cam.IsNull ? 0 : Facade.Mint(FacadeKind.Camera, cam);
        if (camShell != 0)
        {
            check.Expect(*(nint*)camShell == Facade.Table(SeamTable.Camera), "a camera's +0 vptr is not the Camera table");
            nint sub = camShell + SeamLayout.CameraGobSub;
            check.Expect(*(nint*)sub == Facade.Table(SeamTable.CameraGob), "a camera's +8 vptr is not its Gob table");
            check.Expect(Facade.Shell(sub) == camShell, "a camera's CAurObject sub-object did not normalise to the shell");
            // The sub-object's RECORD and its Gob words. The one handle in a camera shell's header is the
            // CAMERA row's, so without the hop through SceneDb.CamObj the Gob slots a camera reaches through
            // +8 resolve the camera's (index, generation) against the OBJECTS table -- reading, and WRITING,
            // whatever record happens to sit there (the chase camera's own eye, into a foreign object).
            DbId camObj = SceneDb.Objects.Alloc();
            SceneDb.CamObj[cam.Index] = camObj;
            check.Expect(Facade.GobBlock(sub) == sub, "a camera's Gob block is its +8 sub-object, not the shell base");
            check.Expect(!camObj.IsNull && Facade.Object(sub, SeamSlot.Unnamed).Value == camObj.Value,
                "a camera's CAurObject sub-object did not resolve to its own object record");
            SceneDb.CamObj[cam.Index] = default;
            if (!camObj.IsNull) SceneDb.Objects.Free(camObj);
            Facade.Retire(camShell);
            SceneDb.Cameras.Free(cam);
        }

        // A text shell's CAurString sub-object, which the game reaches by the literal +0x4c0.
        DbId text = SceneDb.Objects.Alloc();
        nint textShell = text.IsNull ? 0 : Facade.Mint(FacadeKind.String, text);
        if (textShell != 0)
        {
            nint sub = textShell + SeamLayout.StringGobString;
            check.Expect(*(nint*)sub == Facade.Table(SeamTable.CAurString), "a text shell's +0x4c0 vptr is not the CAurString table");
            check.Expect(Facade.Shell(sub) == textShell, "a text shell's CAurString sub-object did not normalise to the shell");
            check.Expect(Facade.StringRecord(sub, SeamSlot.Unnamed).Value == text.Value,
                "the CAurString sub-object did not resolve to the text record");
            Facade.Retire(textShell);
            SceneDb.Objects.Free(text);
        }

        // A newborn shell's SafePointer list must already be a VALID, EMPTY CExoArrayList -- {NULL, 0, 0}.
        // The engine's own `SafePointer<CAurObject>::operator=` @0x477d70 is what assigns a CHitInfo's
        // SafePointer at +0x48/+0x50 against it, and it reallocs the array in place from whatever is there:
        // a non-null pointer with a zero capacity, or garbage, is a free() of a pointer we never allocated.
        check.Expect(*(nint*)(shell + SeamLayout.GobSafeList) == 0
                     && *(int*)(shell + SeamLayout.GobSafeCount) == 0
                     && *(int*)(shell + SeamLayout.GobSafeCap) == 0,
            "a newborn shell's SafePointer list is not an empty {NULL, 0, 0} CExoArrayList");

        // The mirrored words: a setter must leave the shell and the column agreeing, because game code reads
        // one and the renderer reads the other.
        var pos = new Vector3(1.5f, -2.25f, 3f);
        var rot = new Quaternion(0.1f, 0.2f, 0.3f, 0.927f);
        Facade.SetXform(id, shell, pos, rot);
        check.Expect(Facade.ShellPos(shell) == pos && SceneDb.ObjPos[id.Index] == pos,
            "the position setter left the shell word and the column disagreeing");
        check.Expect(Facade.ShellRot(shell) == rot && SceneDb.ObjRot[id.Index] == rot,
            "the orientation setter left the shell word and the column disagreeing");
        check.Expect(SceneDb.Objects.DirtyAt(id.Index).HasFlag(Dirty.Xform), "a transform setter must mark Dirty.Xform");

        Facade.SetName(shell, "cx_selfcheck");
        check.Expect(Facade.NameOf(shell) == "cx_selfcheck", "the inline name array did not round trip");
        var longName = new string('n', SeamLayout.GobNameMax + 32);
        Facade.SetName(shell, longName);
        check.Expect(Facade.NameOf(shell).Length == SeamLayout.GobNameMax - 1,
            "a name longer than the engine's array must be truncated, not written past the field");

        // The SafePointer back-reference list, over a synthetic one the way the game builds it: a
        // SafePointer is {target, int index}, the list at +0x08 is {array, count, capacity}, and the retire
        // must zero every target and set every index to -1 (SafePointerList<T>::~SafePointerList @0x445470).
        const int n = 3;
        nint sps = (nint)NativeMemory.AllocZeroed(n * 16);
        nint arr = (nint)NativeMemory.AllocZeroed(n * (nuint)sizeof(nint));
        for (int k = 0; k < n; k++)
        {
            nint sp = sps + k * 16;
            *(nint*)sp = shell;
            *(int*)(sp + 8) = k;
            ((nint*)arr)[k] = sp;
        }
        *(nint*)(shell + SeamLayout.GobSafeList) = arr;
        *(int*)(shell + SeamLayout.GobSafeCount) = n;
        *(int*)(shell + SeamLayout.GobSafeCap) = n;

        Facade.Retire(shell);
        bool nulled = true, unindexed = true;
        for (int k = 0; k < n; k++)
        {
            nulled &= *(nint*)(sps + k * 16) == 0;
            unindexed &= *(int*)(sps + k * 16 + 8) == -1;
        }
        check.Expect(nulled, "retiring a facade left a SafePointer still pointing at it");
        check.Expect(unindexed, "retiring a facade left a SafePointer's index set");
        check.Expect(*(nint*)(shell + SeamLayout.GobSafeList) == 0 && *(int*)(shell + SeamLayout.GobSafeCount) == 0,
            "the back-reference list was not emptied at retire");
        NativeMemory.Free((void*)sps);

        // The poison path: a retired shell answers no handle, counts the call and names the slot once. The
        // two USE AFTER FREE lines and the one alien line below are this check FIRING the refusal on purpose.
        Facade.InSelfCheck = true;
        long before = Facade.PoisonCalls;
        check.Expect(Facade.Object(shell, SeamVtables.Logical(SeamTable.Gob, 25)).IsNull,
            "a retired facade must not resolve to a record");
        check.Expect(Facade.PoisonCalls > before, "a call on a retired facade must be counted");
        check.Expect(*(nint*)shell == Facade.Poison(SeamTable.Gob),
            "retiring a facade must swap its vptr to the poison table -- the slots that answer from the "
            + "shell's own words never resolve a handle, so the table is the only thing that stops them");

        // The quarantine: the block is not handed out again until two published frames have passed.
        int quarantined = SeamShell.Quarantined;
        check.Expect(quarantined > 0, "a retired shell must sit in the quarantine ring");
        SeamShell.Frame();
        SeamShell.Frame();
        check.Expect(Facade.Object(shell, SeamSlot.Unnamed).IsNull,
            "the quarantine must keep answering a retired pointer, not let it resolve");

        // An alien pointer: something that was never a facade is a named refusal, not a crash.
        long aliens = Facade.AlienCalls;
        nint notOurs = (nint)NativeMemory.AllocZeroed(64);
        check.Expect(Facade.Shell(notOurs) == 0, "a pointer with a foreign vptr must not normalise");
        // review #4: a vptr that merely LANDS inside the vtable block is not one of ours either. The range
        // test alone adjusted such a pointer by whatever the word ahead of it held and handed the result on
        // as a shell -- a wild read in the path whose whole job is to refuse it.
        *(nint*)notOurs = Facade.Table(SeamTable.Gob) + sizeof(nint);
        check.Expect(Facade.Shell(notOurs) == 0,
            "a vptr that points INTO a table rather than at its address point must not normalise");
        *(nint*)notOurs = 0;
        check.Expect(Facade.Object(notOurs, SeamSlot.Unnamed).IsNull, "a foreign pointer must not resolve to a record");
        check.Expect(Facade.AlienCalls > aliens, "a foreign pointer must be counted");
        NativeMemory.Free((void*)notOurs);

        GuiHold(check);
        PartHold(check);
        SceneHold(check);

        Facade.InSelfCheck = false;
        SceneDb.Objects.Free(id);
        SceneDb.Objects.TakeDirty();
        if (fresh) Facade.DropTables();
        SeamShell.DropAll();
    }

    /// <summary>A shell whose object was ever in a GUI scene is never handed out again, even once the ring is
    /// full and past its quarantine. Oracle: hand-written expectations -- the stamp each join leaves, the GUI
    /// shell's address never coming back, and the same churn reusing a world shell.</summary>
    private static void GuiHold(Checker check)
    {
        nint probe = SeamShell.Alloc(FacadeKind.Object, default);
        check.Expect(!SeamShell.NoteScene(probe, world: true), "a first world join is not a world-to-GUI move");
        check.Expect(SeamShell.NoteScene(probe, world: false), "a world object joining a GUI scene must be named");
        check.Expect(!SeamShell.NoteScene(probe, world: false), "the world-to-GUI move is named once per shell");
        check.Expect(!SeamShell.NoteScene(0, world: false), "a null shell carries no stamp");

        DbId world = SceneDb.Scenes.Alloc(), gui = SceneDb.Scenes.Alloc(), wobj = SceneDb.Objects.Alloc(), gobj = SceneDb.Objects.Alloc();
        if (world.IsNull || gui.IsNull || wobj.IsNull || gobj.IsNull)
        {
            check.Expect(false, "no records for the GUI hold check");
            return;
        }
        SceneDb.SceneIsWorld[world.Index] = true;
        SceneDb.SceneIsWorld[gui.Index] = false;
        DbFeed.InitObject(wobj.Index);
        DbFeed.InitObject(gobj.Index);
        nint wshell = Facade.Mint(FacadeKind.Object, wobj), gshell = Facade.Mint(FacadeKind.Object, gobj);
        DbScene.Join(world, wobj);
        DbScene.Join(gui, gobj);
        DbScene.Leave(gui, gobj);
        DbScene.Leave(world, wobj);
        int held = SeamShell.Held, quarantined = SeamShell.Quarantined;
        Facade.Retire(gshell);
        check.Expect(SeamShell.Held == held + 1 && SeamShell.Quarantined == quarantined,
            "a shell that was in a GUI scene must be held, not queued for reuse");
        Facade.Retire(wshell);
        check.Expect(SeamShell.Held == held + 1 && SeamShell.Quarantined == quarantined + 1,
            "a shell that was only ever in a world scene goes to the reuse ring");

        SeamShell.Retire(probe);
        // Age what the ring already holds first, so filling it evicts those rather than overflowing.
        for (int f = 0; f < SeamShell.QuarantineFrames; f++) SeamShell.Frame();
        for (int k = 0; k < SeamShell.RingSize; k++) SeamShell.Retire(SeamShell.Alloc(FacadeKind.Object, default));
        for (int f = 0; f < SeamShell.QuarantineFrames; f++) SeamShell.Frame();
        long reused = SeamShell.Reused;
        bool gotGui = false;
        var minted = new nint[SeamShell.RingSize];
        for (int k = 0; k < minted.Length; k++)
        {
            minted[k] = SeamShell.Alloc(FacadeKind.Object, default);
            gotGui |= minted[k] == gshell || minted[k] == probe;
        }
        check.Expect(SeamShell.Reused > reused, "a full ring past its quarantine must hand a block back, or this check proves nothing");
        check.Expect(!gotGui, "a retired GUI shell was handed out again");
        foreach (nint s in minted) SeamShell.Retire(s);

        SceneDb.Objects.Free(wobj);
        SceneDb.Objects.Free(gobj);
        SceneDb.SceneIsWorld[world.Index] = false;
        SceneDb.Scenes.Free(world);
        SceneDb.Scenes.Free(gui);
    }

    /// <summary>A part shell whose object was ever in a GUI scene is held at the object's death, never freed,
    /// so the part allocator cannot hand its address out again. Oracle: hand-written expectations -- the
    /// held and quarantined counts, and the GUI part's address never coming back from a churn that does reuse
    /// the world part's. Its two GUI parts stay held for the session.</summary>
    private static void PartHold(Checker check)
    {
        PartShell.Build();
        DbId world = SceneDb.Scenes.Alloc(), gui = SceneDb.Scenes.Alloc(), wobj = SceneDb.Objects.Alloc(),
             gobj = SceneDb.Objects.Alloc(), churn = SceneDb.Objects.Alloc();
        if (world.IsNull || gui.IsNull || wobj.IsNull || gobj.IsNull || churn.IsNull)
        {
            check.Expect(false, "no records for the GUI part hold check");
            return;
        }
        SceneDb.SceneIsWorld[world.Index] = true;
        SceneDb.SceneIsWorld[gui.Index] = false;
        DbFeed.InitObject(wobj.Index);
        DbFeed.InitObject(gobj.Index);
        DbFeed.InitObject(churn.Index);
        nint wshell = Facade.Mint(FacadeKind.Object, wobj), gshell = Facade.Mint(FacadeKind.Object, gobj);
        nint wpart = PartShell.For(wobj, 0), before = PartShell.For(gobj, 0);
        if (wpart == 0 || before == 0)
        {
            check.Skip("no Part vtable in this host, so no part shell can be minted");
        }
        else
        {
            DbScene.Join(world, wobj);
            DbScene.Join(gui, gobj);
            nint after = PartShell.For(gobj, 1);
            DbScene.Leave(gui, gobj);
            DbScene.Leave(world, wobj);
            long held = PartShell.Held, quarantined = PartShell.Quarantined;
            PartShell.FreeAll(gobj);
            check.Expect(PartShell.Held == held + 2 && PartShell.Quarantined == quarantined,
                "both parts of an object that was in a GUI scene, minted before and after the join, must be held");
            PartShell.FreeAll(wobj);
            check.Expect(PartShell.Held == held + 2 && PartShell.Quarantined == quarantined + 1,
                "a part of an object only ever in a world scene goes to the quarantine");
            check.Expect(!PartShell.Resolve(before, out _, out _) && !PartShell.Resolve(after, out _, out _),
                "a held GUI part shell must not resolve to a record");

            for (int f = 0; f < SeamShell.QuarantineFrames; f++) SeamShell.Frame();
            check.Expect(PartShell.Quarantined == quarantined, "the world part must be freed past its quarantine");
            bool gotGui = false, gotWorld = false;
            for (int k = 0; k < 256; k++)
            {
                nint p = PartShell.For(churn, k);
                gotGui |= p == before || p == after;
                gotWorld |= p == wpart;
            }
            check.Expect(gotWorld, "the freed world part's address must come back, or this check proves nothing");
            check.Expect(!gotGui, "a held GUI part shell was handed out again");
            PartShell.FreeAll(churn);
            for (int f = 0; f < SeamShell.QuarantineFrames; f++) SeamShell.Frame();
        }

        Facade.Retire(gshell);
        Facade.Retire(wshell);
        SceneDb.Objects.Free(wobj);
        SceneDb.Objects.Free(gobj);
        SceneDb.Objects.Free(churn);
        SceneDb.SceneIsWorld[world.Index] = false;
        SceneDb.Scenes.Free(world);
        SceneDb.Scenes.Free(gui);
    }

    /// <summary>A scene shell that never became a world scene is held at retire, so no later scene -- the world
    /// one included -- can be minted at its address. Oracle: hand-written expectations -- the held and
    /// quarantined counts, and the GUI scene's address never coming back from a churn that does reuse blocks.</summary>
    private static void SceneHold(Checker check)
    {
        DbId world = SceneDb.Scenes.Alloc(), gui = SceneDb.Scenes.Alloc();
        if (world.IsNull || gui.IsNull)
        {
            check.Expect(false, "no records for the scene hold check");
            return;
        }
        nint wshell = Facade.Mint(FacadeKind.Scene, world), gshell = Facade.Mint(FacadeKind.Scene, gui);
        SeamShell.NoteScene(wshell, world: true);
        int held = SeamShell.Held, quarantined = SeamShell.Quarantined;
        Facade.Retire(gshell);
        check.Expect(SeamShell.Held == held + 1 && SeamShell.Quarantined == quarantined,
            "a scene shell that was never a world scene must be held, not queued for reuse");
        Facade.Retire(wshell);
        check.Expect(SeamShell.Held == held + 1 && SeamShell.Quarantined == quarantined + 1,
            "a world scene's shell goes to the reuse ring");

        for (int f = 0; f < SeamShell.QuarantineFrames; f++) SeamShell.Frame();
        long reused = SeamShell.Reused;
        bool gotGui = false;
        for (int k = 0; k < SeamShell.RingSize; k++)
        {
            nint s = SeamShell.Alloc(FacadeKind.Scene, default);
            gotGui |= s == gshell;
            SeamShell.NoteScene(s, world: true);
            SeamShell.Retire(s);
        }
        check.Expect(SeamShell.Reused > reused, "a full scene ring past its quarantine must hand a block back, or this check proves nothing");
        check.Expect(!gotGui, "a retired GUI scene shell was handed out again");

        SceneDb.Scenes.Free(world);
        SceneDb.Scenes.Free(gui);
    }
}
