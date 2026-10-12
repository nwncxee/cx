using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>What a <c>CAurScene</c> IS: a row in <see cref="SceneDb.Scenes"/> plus the ordered member list the four <c>RenderGob*</c> slots
/// reorder. Every method runs on the GAME thread, from a <see cref="Seam.SceneSlots"/> entry point or a scene free function, and writes
/// columns plus one <see cref="Dirty"/> bit (ADR 0028 section 4). Fog, sky, wind, ambient, grass, static lighting and geometry fade are
/// columns here too, bridged into the renderer by <see cref="SceneFeed"/>.</summary>
public static unsafe class DbScene
{
    private static ExtenderLog? _log;
    private static long _created, _destroyed, _reorders, _refused;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Create a scene record. <paramref name="name"/> is load-bearing (the <c>sceneGUI</c> prefix
    /// decides the view's depth range and the GUI/world split), so it is stored, not logged.
    /// Null handle = the table is full, which is logged loudly and once per cause -- never a quiet drop.</summary>
    public static DbId Create(string name)
    {
        DbId id = SceneDb.Scenes.Alloc();
        if (id.IsNull)
        {
            if (_refused++ == 0)
                _log?.Always($"DbScene: the scene table is full ({SceneDb.MaxScenes} scenes) -- "
                           + $"NewCAurScene(\"{name}\") REFUSED; the game layer will get a null scene");
            return default;
        }
        int i = id.Index;
        SceneDb.SceneName[i] = name ?? "";
        SceneDb.SceneIsWorld[i] = false;   // InitBSP is what makes a scene a world scene
        SceneDb.SceneAmbient[i] = Vector3.Zero;
        SceneDb.SceneFogNear[i] = 0f; SceneDb.SceneFogFar[i] = 0f;
        SceneDb.SceneFogColor[i] = Vector3.Zero; SceneDb.SceneFogOn[i] = false;
        SceneDb.SceneFocus[i] = Vector3.Zero;
        SceneDb.SceneAnimOn[i] = true;
        SceneDb.SceneAnimating[i] = false;
        SceneDb.SceneWindDir[i] = Vector3.Zero;
        SceneDb.SceneWindMag[i] = 0f; SceneDb.SceneWindGust[i] = 0f; SceneDb.SceneWindTimeout[i] = 0f;
        SceneDb.SceneSkyFade[i] = default;
        SceneDb.SceneSkyT[i] = 0f; SceneDb.SceneSkyCur[i] = 0; SceneDb.SceneSkyDesired[i] = 0;
        SceneDb.SceneSkyOff[i] = true;
        SceneDb.SceneBlindLights[i] = default;
        SceneDb.SceneShadowGob[i] = default;
        SceneDb.SceneHighlightTex[i] = "";
        SceneDb.SceneRenderAabb[i] = false;
        SceneDb.SceneObjects[i]?.Clear();
        SceneDb.SceneHighlights[i]?.Clear();
        SceneDb.SceneHighlightN[i] = 0;
        for (int k = 0; k < SceneDb.SkyBoxSlots; k++) SceneDb.SetSceneSky(id, k, default);
        SceneDb.SceneLines[i]?.Clear();
        SceneDb.SceneFacade[i] = 0;
        _created++;
        return id;
    }

    /// <summary>The scene facade's destructor: every object still in the scene dies with it (ADR 0028's scene
    /// destroy edge), then the record. Its tiles and lights die as part of its objects.</summary>
    public static void Destroy(DbId scene)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int i = scene.Index;
        // THE AREA EDGE. Nothing else announces it: no engine-side hook is installed for it, so without this
        // ModelCache.AreaEdge, TileBuild.Revoke and every pass's Revoke would never fire, and an area change
        // would keep the
        // previous area's models, tiles and arena ranges for the session. The world scene's destruction IS
        // that edge, so it runs the same revoke rather than a DB-specific list of its own.
        // Before the member sweep: TileBuild.Revoke does the tile teardown with its census and holds, which
        // the generic object destroy does not know about. What it frees, the sweep below then skips.
        // CX-116: the tiles go with THEIR scene, by handle, before the generic edge. AreaEdge is global and
        // fires from more than this -- measured mid-load, between CNWCArea::LoadArea's tile loop and
        // LoadBorder -- and a global tile teardown there wiped exactly the walkmesh-carrying real tiles,
        // which is the floor every HitCheck then failed to find (CX-185).
        TileBuild.SceneRevoke(scene);
        DbTriggers.SceneRevoke(scene);
        if (SceneDb.SceneIsWorld[i]) AlloyRenderer.AreaEdge("DB mode: the world scene was destroyed");

        DbList? members = SceneDb.SceneObjects[i];
        if (members != null)
        {
            // Walk backwards: the leave removes from this very list. Handles the area edge already freed are
            // skipped on their generation, not on trust -- two teardowns reaching one record is the normal
            // case here, not a bug.
            // DETACH, do not destroy: Scene::~Scene @0x472e40 is a loop of Gob::SetScene(child, NULL). A
            // member outlives its scene and the game deletes it through its own destructor, so destroying it
            // here retires a shell the game still holds. What CX itself owns (the area's tiles and lights)
            // was already freed by the AreaEdge above and is skipped on its generation.
            if (ObjectBridge.Ready(_log))
                for (int k = members.Count - 1; k >= 0; k--)
                {
                    DbId m = members[k];
                    if (SceneDb.Objects.Valid(m)) ObjectBridge.ObjectSceneDestroyed?.Invoke(m);
                }
            members.Clear();
        }
        SceneDb.SceneHighlights[i]?.Clear();
        SceneDb.SceneHighlightN[i] = 0;
        SceneDb.SceneLines[i]?.Clear();
        SceneDb.SceneName[i] = "";
        CX.Alloy.Seam.SceneFns.ReleaseWind(SceneDb.SceneFacade[i]);   // the shell owns it; it dies with it
        SceneShell.Release(scene);   // nulls the shell's list words, then frees the mirror -- in that order
        SceneDb.SceneFacade[i] = 0;
        SceneDb.Scenes.Free(scene);
        _destroyed++;
    }

    /// <summary>The scene's member list, created on first use.</summary>
    public static DbList Members(DbId scene)
    {
        int i = scene.Index;
        return SceneDb.SceneObjects[i] ??= new DbList();
    }

    /// <summary>Live member count -- what <c>GetAurSceneObjectCount</c>'s direct read of the shell's
    /// <c>+0x138</c> must agree with, and what the mirror is kept current against.</summary>
    public static int MemberCount(DbId scene)
        => SceneDb.Scenes.Valid(scene) ? SceneDb.SceneObjects[scene.Index]?.Count ?? 0 : 0;

    /// <summary>Join a record the caller JUST allocated: as <see cref="Join"/> but without the "already a
    /// member" linear scan, which a freshly allocated handle cannot fail. An area load joins ~1150 tiles, so
    /// paying that scan would make the load quadratic.</summary>
    public static void JoinNew(DbId scene, DbId obj) => Join(scene, obj, fresh: true);

    /// <summary>An object joined the scene (<c>Gob::AttachToScene</c> / <c>Scene::AddObject</c> /
    /// <c>ChildAdd</c>): append it in draw order and refresh the shell mirror.</summary>
    public static void Join(DbId scene, DbId obj) => Join(scene, obj, fresh: false);

    private static void Join(DbId scene, DbId obj, bool fresh)
    {
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.Objects.Valid(obj)) return;
        DbList m = Members(scene);
        if (!fresh && m.IndexOf(obj) >= 0) return;
        // One scene per object: the scene/ChildAdd slots join without updating the shell's +0x88 word, so the
        // engine's Gob::SetScene can ChildRemove from a stale word and leave the object drawing in its old panel.
        DbId was = SceneDb.ObjScene[obj.Index];
        if (!fresh && was.Value != scene.Value && Leave(was, obj)) Stranded(was, scene, obj);
        m.Add(obj);
        SceneDb.ObjScene[obj.Index] = scene;
        SceneDb.ObjCrossScene[obj.Index] = scene;
        SceneDb.TouchScene(scene, Dirty.Tree);
        SceneDb.Touch(obj, Dirty.Tree);
        SceneShell.CountChanged(scene);
    }

    /// <summary>An object left the scene (<c>Gob::SetScene(null)</c> / <c>ChildRemove</c> / its destroy). Its own
    /// scene column goes with it: <c>Gob::SetScene</c> @0x43e020 removes through <c>ChildRemove</c> and then fires
    /// "UpdateScenes", whose propagation reads that column -- left set, a dislodged control's riders stayed in the
    /// panel it left (the examine panel's Identify label without its button).</summary>
    public static bool Leave(DbId scene, DbId obj)
    {
        if (!SceneDb.Scenes.Valid(scene)) return false;
        if (SceneDb.Objects.Valid(obj) && SceneDb.ObjScene[obj.Index].Value == scene.Value)
            SceneDb.ObjScene[obj.Index] = default;
        int i = scene.Index;
        if (SceneDb.SceneObjects[i]?.Remove(obj) != true) return false;
        SceneDb.SceneHighlights[i]?.Remove(obj);
        SceneDb.Unhighlight(scene, obj);
        if (SceneDb.SceneBlindLights[i] == obj) SceneDb.SceneBlindLights[i] = default;
        if (SceneDb.SceneShadowGob[i] == obj) SceneDb.SceneShadowGob[i] = default;
        if (SceneDb.SceneSkyFade[i] == obj) SceneDb.SceneSkyFade[i] = default;
        for (int k = 0; k < SceneDb.SkyBoxSlots; k++)
            if (SceneDb.SceneSky(scene, k) == obj) SceneDb.SetSceneSky(scene, k, default);
        SceneDb.TouchScene(scene, Dirty.Tree);
        SceneShell.CountChanged(scene);
        return true;
    }

    /// <summary>Joins that found the object still listed in another scene, and how many of them were said.</summary>
    private sealed class StrandState { public long Moved; public int Said; }
    private static StrandState _strand = new();

    /// <summary>An object joined a scene while still a member of another: said a few times with both scene
    /// names, because each one was a control left painting in a panel it had moved out of.</summary>
    private static void Stranded(DbId was, DbId scene, DbId obj)
    {
        _strand.Moved++;
        if (_strand.Said >= 16) return;
        _strand.Said++;
        _log?.Info($"DbScene: o={obj.Index} \"{SceneDb.ObjModel[obj.Index]?.Name ?? "-"}\" joined scene "
            + $"\"{SceneDb.SceneName[scene.Index]}\" while still listed in \"{SceneDb.SceneName[was.Index]}\" -- "
            + "taken out of the old one (it would have kept drawing there)");
    }

    /// <summary>Move <paramref name="obj"/> to the front of the scene's draw order
    /// (<c>Scene::RenderGobFirst</c>).</summary>
    public static void RenderFirst(DbId scene, DbId obj) => Reorder(scene, obj, 0);

    /// <summary>Move <paramref name="obj"/> to the back of the draw order (<c>Scene::RenderGobLast</c>, and
    /// the free function <c>GobRenderLast</c> the GUI drag source and the area map reach for).</summary>
    public static void RenderLast(DbId scene, DbId obj)
        => Reorder(scene, obj, MemberCount(scene));

    /// <summary>Draw <paramref name="obj"/> immediately after <paramref name="anchor"/>
    /// (<c>Scene::RenderGobAfter</c>). An anchor that is not a member leaves the order alone.</summary>
    public static void RenderAfter(DbId scene, DbId obj, DbId anchor)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int at = Members(scene).IndexOf(anchor);
        if (at < 0) return;
        Reorder(scene, obj, at + 1);
    }

    /// <summary>Draw <paramref name="obj"/> immediately before <paramref name="anchor"/>
    /// (<c>Scene::RenderGobBefore</c>).</summary>
    public static void RenderBefore(DbId scene, DbId obj, DbId anchor)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        int at = Members(scene).IndexOf(anchor);
        if (at < 0) return;
        Reorder(scene, obj, at);
    }

    private static void Reorder(DbId scene, DbId obj, int to)
    {
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.Objects.Valid(obj)) return;
        DbList m = Members(scene);
        if (m.IndexOf(obj) < 0) return;
        m.MoveTo(obj, to);
        _reorders++;
        SceneDb.TouchScene(scene, Dirty.Tree);
        SceneShell.CountChanged(scene);
    }

    /// <summary>Highlight <paramref name="obj"/> in <paramref name="colour"/> (<c>AddHighlightGob</c>): one
    /// entry per object, the colour replaced when the game re-adds it.</summary>
    public static void AddHighlight(DbId scene, DbId obj, Vector3 colour)
    {
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.Objects.Valid(obj)) return;
        int i = scene.Index;
        DbList hl = SceneDb.SceneHighlights[i] ??= new DbList();
        hl.Remove(obj);
        hl.Add(obj, new Vector4(colour, 1f), withAux: true);
        SceneDb.TouchScene(scene, Dirty.Material);
    }

    /// <summary><c>RemoveHighlightGob</c>.</summary>
    public static void RemoveHighlight(DbId scene, DbId obj)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        if (SceneDb.SceneHighlights[scene.Index]?.Remove(obj) == true)
            SceneDb.TouchScene(scene, Dirty.Material);
        SceneDb.Unhighlight(scene, obj);
    }

    /// <summary>Find a member by name (<c>Scene::GetObject(char const*)</c>). Cold: the GUI layer's lookup
    /// when it has lost its own pointer. Linear, which is what the engine's own list walk is.</summary>
    public static DbId Find(DbId scene, string name)
    {
        if (!SceneDb.Scenes.Valid(scene) || name.Length == 0) return default;
        DbList? m = SceneDb.SceneObjects[scene.Index];
        if (m == null) return default;
        for (int k = 0; k < m.Count; k++)
        {
            DbId o = m[k];
            if (SceneDb.Objects.Valid(o) && ObjectBridge.ObjectName != null
             && string.Equals(ObjectBridge.ObjectName(o), name, StringComparison.OrdinalIgnoreCase))
                return o;
        }
        return default;
    }

    /// <summary>Is this scene animating -- <c>Scene::IsAnimating</c> @0x55e000, whose whole body is
    /// <c>return this[0xb9]</c>: a CACHED BYTE, not a walk. So this is a column, written by the animation
    /// step that knows the answer, and the getter costs one load.</summary>
    public static bool IsAnimating(DbId scene)
        => SceneDb.Scenes.Valid(scene) && SceneDb.SceneAnimOn[scene.Index];

    /// <summary>Write the flag. ONE column, because the engine has one byte: <c>EnableAnimations</c> and
    /// <c>DisableAnimations</c> write <c>Scene+0xb9</c> and <c>IsAnimating</c> reads it back. The separate
    /// <c>SceneAnimating</c> column was a second answer to the same question with no writer, so every DB
    /// consumer that asked got false.</summary>
    public static void SetAnimating(DbId scene, bool animating)
    {
        if (SceneDb.Scenes.Valid(scene)) SceneDb.SceneAnimOn[scene.Index] = animating;
    }

    /// <summary>An object record died: drop every reference its scene holds, NOW. Its member list, highlight
    /// set, shadow gob, blind-light object and skybox slots can each name a retired shell, and the count word
    /// at the scene shell is read synchronously by game code, so this cannot wait for a frame-boundary fold.
    /// <see cref="ObjectBridge"/> must call it from the object destroy edge; idempotent and cheap.</summary>
    public static void ObjectDied(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        Leave(SceneDb.ObjScene[obj.Index], obj);
    }

    /// <summary>Counters as they stand, so the off-device suite can put them back: a fixture is not work the
    /// session did, and a period line that counts five created and five destroyed beside a live figure that
    /// never saw them is a line nobody can read.</summary>
    internal static (long, long, long, long) TakeCounters() => (_created, _destroyed, _reorders, _refused);

    /// <inheritdoc cref="TakeCounters"/>
    internal static void PutCounters((long, long, long, long) c)
        => (_created, _destroyed, _reorders, _refused) = c;

    internal static string Stats(int _)
        => $"scenes: {SceneDb.Scenes.Live} live ({_created} created, {_destroyed} destroyed), {_reorders} draw-order moves"
         + (_refused > 0 ? $", {_refused} REFUSED (table full)" : "")
         + (_strand.Moved > 0 ? $", {_strand.Moved} object(s) moved out of a scene that still listed them" : "");
}
