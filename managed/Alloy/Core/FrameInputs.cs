using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>
/// This frame's CONSTANTS: the engine state every Alloy pass shades and fits against -- camera, area terms,
/// sun, fog and the light snapshot. A block that is READ once per world frame by <see cref="FrameRead"/> and
/// <see cref="SceneInputs"/>; nothing is captured off a rendering call site and no value is settled by a
/// contest between passes. The read and every consumer are on the render thread, so plain fields suffice.
/// </summary>
public static class FrameInputs
{
    /// <summary>True once the directional sun light has been seen this session.</summary>
    public static bool HasSun { get => Read.HasSun; set => _cur.HasSun = value; }
    /// <summary>Normalized world-space (Z-up) direction the sunlight travels (from <c>PartLight+0xac</c>).</summary>
    public static Vector3 SunDir { get => Read.SunDir; set => _cur.SunDir = value; }
    /// <summary>True once a camera has been read this session.</summary>
    public static bool HasCamera { get => Read.HasCamera; set => _cur.HasCamera = value; }
    /// <summary>World-space camera position (<c>*CurrentCamera + 0xb0</c>).</summary>
    public static Vector3 CameraPos { get => Read.CameraPos; set => _cur.CameraPos = value; }
    /// <summary>Camera orientation (<c>*CurrentCamera + 0xbc</c>, re-ordered to Numerics' (x, y, z, w)).</summary>
    public static Quaternion CameraRot { get => Read.CameraRot; set => _cur.CameraRot = value; }
    /// <summary>True once the scene camera has been read this session.</summary>
    public static bool HasSceneCam { get => Read.HasSceneCam; set => _cur.HasSceneCam = value; }
    /// <summary>World-space position and forward of the camera the SCENE is drawn with -- the SAME read as
    /// <see cref="CameraPos"/>/<see cref="CameraRot"/>, since <c>CurrentCamera</c> inside the world scene's
    /// render IS the scene camera. The pair survives because the shadow path names it, not because the two
    /// can disagree.</summary>
    public static Vector3 SceneCamPos { get => Read.SceneCamPos; set => _cur.SceneCamPos = value; }
    public static Vector3 SceneCamFwd { get => Read.SceneCamFwd; set => _cur.SceneCamFwd = value; }
    /// <summary>The scene projection's <c>p11</c> (<c>1 / tan(fovy/2)</c>), from <see cref="CamFovy"/>.</summary>
    public static float SceneCamP11 { get => Read.SceneCamP11; set => _cur.SceneCamP11 = value; }
    /// <summary>True once a camera with a real view angle has been read.</summary>
    public static bool HasPerspective { get => Read.HasPerspective; set => _cur.HasPerspective = value; }
    /// <summary>Camera perspective params, the same four the engine hands SetPerspectiveTransform: view
    /// angle in degrees (<c>+0x4c4</c>), viewport aspect (<c>+0x4dc / +0x4e0</c>), near and far
    /// (<c>+0x4cc</c>/<c>+0x4d0</c>).</summary>
    public static float CamFovy { get => Read.CamFovy; set => _cur.CamFovy = value; }
    public static float CamAspect { get => Read.CamAspect; set => _cur.CamAspect = value; }
    public static float CamNear { get => Read.CamNear; set => _cur.CamNear = value; }
    public static float CamFar { get => Read.CamFar; set => _cur.CamFar = value; }
    /// <summary>The camera's own viewport width and height (<c>+0x4dc</c>/<c>+0x4e0</c>). Not a GL
    /// viewport -- the engine upscales both by the same factor before glViewport -- but it moves exactly
    /// when the GL one does, which is what makes it a usable generation key for the frame's target.</summary>
    public static float CamViewW { get => Read.CamViewW; set => _cur.CamViewW = value; }
    public static float CamViewH { get => Read.CamViewH; set => _cur.CamViewH = value; }
    /// <summary>Scene fog, read straight off <c>Scene+0x140..0x14c</c> rather than out of
    /// <c>GLRender::m_fog</c> after asking the engine to fill it. The colour is DISPLAY space, as the block
    /// stores it -- the stages that fog linearise it themselves.</summary>
    public static bool FogOn { get => Read.FogOn; set => _cur.FogOn = value; }
    /// <summary>Fog start and end distance, and its display-space colour. Meaningful only while
    /// <see cref="FogOn"/>.</summary>
    public static float FogStart { get => Read.FogStart; set => _cur.FogStart = value; }
    public static float FogEnd { get => Read.FogEnd; set => _cur.FogEnd = value; }
    /// <summary>Fog colour, display space (see <see cref="FogOn"/>).</summary>
    public static Vector3 FogColor { get => Read.FogColor; set => _cur.FogColor = value; }
    // --- Area ambient / directional diffuse (#83): the engine's own area terms, the two index -1 lights
    // LightManager::ControlLightsFade pushes -- read since #257 where they live (LightManager +0xa0 / +0x98)
    // rather than intercepted at SetLightGL. Colours are stored LINEAR (the engine runs them through
    // GetColorSpaceFromSRGB before its own uniform); the direction is world-space surface->light.

    /// <summary>True once each area term has been read this session.</summary>
    public static bool HasAreaAmbient { get => Read.HasAreaAmbient; set => _cur.HasAreaAmbient = value; }
    public static bool HasAreaDiffuse { get => Read.HasAreaDiffuse; set => _cur.HasAreaDiffuse = value; }
    /// <summary>Linear area ambient / directional diffuse colour, and the world-space surface-&gt;light dir.</summary>
    public static Vector3 AreaAmbient { get => Read.AreaAmbient; set => _cur.AreaAmbient = value; }
    public static Vector3 AreaDiffuse { get => Read.AreaDiffuse; set => _cur.AreaDiffuse = value; }
    public static Vector3 AreaDiffuseDir { get => Read.AreaDiffuseDir; set => _cur.AreaDiffuseDir = value; }
    /// <summary><c>Scene::SetBlindLights</c> is up for the walked scene: one object's lights alone light it, so
    /// the baked pages (every static light) and the fog's sky term stand down with the area pair.</summary>
    public static bool Blind { get => Read.Blind; set => _cur.Blind = value; }
    // --- The light snapshot: every active point light in the area, read from the LightManager MASTER list
    // once per world frame by SceneInputs. Values are COPIED, never the PartLight* (the engine frees it on
    // area/module boundaries -- the AurTexture lifetime lesson); the id is kept for equality only.
    //
    // #257: no fixed worst-case array survives here. Both sets grow to whatever the master list holds, so
    // there is no ceiling to overflow, no overflow counter to read, and no nearest-K eviction to churn --
    // CasterPass::CubePass does its own nearest-first pick against its cube budget, which is the only
    // selection that was ever load-bearing.

    /// <summary>Shadow-worthy point lights recorded this frame.</summary>
    public static int LocalLightCount { get => Read.LocalLightCount; set => _cur.LocalLightCount = value; }
    /// <summary>Engine light identity (the <c>PartLight*</c> from the master-list walk) -- the slot-binding
    /// key. NEVER dereferenced outside the walk; used only for equality, where a recycled pointer simply
    /// reads as a new light.</summary>
    public static IntPtr[] LocalLightId { get => Read.LocalLightId; set => _cur.LocalLightId = value; }
    /// <summary>World-space light position (<c>PlPos</c>, +0xac) -- the cube's bake origin. It is the SAME
    /// value <see cref="AreaLightPosRange"/> shades from, deliberately: a cube baked from anywhere else
    /// computes occlusion for a light that is not where the BRDF thinks it is.</summary>
    public static Vector3[] LocalLightPos { get => Read.LocalLightPos; set => _cur.LocalLightPos = value; }
    /// <summary>Light reach (cube far plane) = <c>GetLightAdjustedRadius</c>, the engine's own lit radius off
    /// <c>PartLight+0x70</c>: exactly the sphere the receiver's attenuation has support over, so nothing
    /// outside it is worth baking.</summary>
    public static float[] LocalLightRange { get => Read.LocalLightRange; set => _cur.LocalLightRange = value; }
    /// <summary>Light RGB (<c>+0x64</c>) -- for light-&gt;slot matching / debug, not the cube itself.</summary>
    public static Vector3[] LocalLightColor { get => Read.LocalLightColor; set => _cur.LocalLightColor = value; }
    /// <summary>Current area carries the ARE interior flag -- no sun, so nothing directional is worth
    /// rendering there. Recomputed once per frame by <see cref="SceneInputs"/> (which walks the engine's
    /// own area chain); false until an area is known, which is the exterior-shaped default.</summary>
    public static bool Interior { get => Read.Interior; set => _cur.Interior = value; }
    /// <summary>The area is on its night palette: the area directional is the moon. False until an area is known.</summary>
    public static bool Night { get => Read.Night; set => _cur.Night = value; }
    /// <summary>The current area's world XY extent in metres -- its tile grid times
    /// <see cref="AreaBorder.TileSize"/>, so tile (0,0) spans [0,10) and the area runs to
    /// (TileWidth, TileHeight) * 10. Zero until an area is known. Read once per frame beside
    /// <see cref="Interior"/>; R2's wetness map is fitted to it.</summary>
    public static Vector2 AreaExtent { get => Read.AreaExtent; set => _cur.AreaExtent = value; }
    /// <summary>The <c>CNWCArea*</c> <see cref="AreaExtent"/> and <see cref="Interior"/> were read from:
    /// compared for the area edge, never dereferenced by a consumer.</summary>
    public static IntPtr AreaPtr { get => Read.AreaPtr; set => _cur.AreaPtr = value; }
    /// <summary>True once <see cref="Focus"/> holds this frame's shadow focus (the player anchor).</summary>
    public static bool HasFocus { get => Read.HasFocus; set => _cur.HasFocus = value; }
    /// <summary>The shadow focus (player position) -- the nearest-K key for overflow areas.</summary>
    public static Vector3 Focus { get => Read.Focus; set => _cur.Focus = value; }
    /// <summary>Starting capacity of both snapshots. Not a ceiling: a denser area grows them once and keeps
    /// the larger arrays for the session. A dense live area runs ~120 lights.</summary>
    private const int InitialLights = 256;

    /// <summary>Record one shadow-casting point light (identity, world pos, reach, RGB). Dedup by IDENTITY,
    /// so a repeat walk in the same frame refreshes the entry rather than doubling it. Alloc-free in the
    /// steady state -- <see cref="ResetLocalLights"/> has already sized them off the master list, so the
    /// growth branch below is a guard rather than a path.</summary>
    public static void RecordLocalLight(IntPtr id, Vector3 pos, float range, Vector3 color)
    {
        for (int i = 0; i < LocalLightCount; i++)
            if (LocalLightId[i] == id)
            { LocalLightPos[i] = pos; LocalLightRange[i] = range; LocalLightColor[i] = color; return; }
        if (LocalLightCount == LocalLightId.Length)
        {
            int n = LocalLightCount * 2;
            Array.Resize(ref _cur.LocalLightId, n); Array.Resize(ref _cur.LocalLightPos, n);
            Array.Resize(ref _cur.LocalLightRange, n); Array.Resize(ref _cur.LocalLightColor, n);
        }
        int at = LocalLightCount++;
        LocalLightId[at] = id; LocalLightPos[at] = pos; LocalLightRange[at] = range; LocalLightColor[at] = color;
    }

    /// <summary>Clear the per-frame shadow-candidate set (top of each master-list walk). <paramref
    /// name="expect"/> is the master list's own count, which the recorded set is a subset of -- so sizing
    /// off it here is what keeps <see cref="RecordLocalLight"/> from ever allocating on the render thread.</summary>
    public static void ResetLocalLights(int expect)
    {
        LocalLightCount = 0;
        if (expect <= LocalLightId.Length) return;
        Array.Resize(ref _cur.LocalLightId, expect); Array.Resize(ref _cur.LocalLightPos, expect);
        Array.Resize(ref _cur.LocalLightRange, expect); Array.Resize(ref _cur.LocalLightColor, expect);
    }

    // --- Extender-owned point lighting: EVERY active point light in the area (shadow-casting or not),
    // snapshotted from the same master-list walk and fed to the Alloy pass's clustered light SSBO (#83).

    /// <summary>Point lights snapshotted this walk -- the Alloy path's set. The deferred path has no light
    /// ceiling by design, and neither has the snapshot that feeds it.</summary>
    public static int LightCount { get => Read.LightCount; set => _cur.LightCount = value; }
    /// <summary>World pos (xyz) + lit range (w), packed for a single glUniform4fv.</summary>
    public static Vector4[] AreaLightPosRange { get => Read.AreaLightPosRange; set => _cur.AreaLightPosRange = value; }
    /// <summary>Light RGB (sRGB as stored in PartLight; the shader linearizes).</summary>
    public static Vector3[] AreaLightColor { get => Read.AreaLightColor; set => _cur.AreaLightColor = value; }
    /// <summary>PartLight identity per area light (equality only) -- keys the CPU-side light-&gt;cube-slot
    /// bind.</summary>
    public static IntPtr[] AreaLightId { get => Read.AreaLightId; set => _cur.AreaLightId = value; }
    /// <summary>Cube slot per area light (-1 = casts no shadow / no cube) -- resolved at the pass slot right
    /// after the bake, so the mapping and the cube contents are the same frame's, and read by the owned
    /// lit stage.</summary>
    public static int[] AreaLightSlot { get => Read.AreaLightSlot; set => _cur.AreaLightSlot = value; }
    /// <summary>Ambient-class light (PartLight type@0x80 != 0): vanilla adds <c>c * att</c> straight to the
    /// ambient term, no N.L and no shadow. Uploaded as cube slot <see cref="AmbientSlot"/>.</summary>
    public static bool[] AreaLightAmbient { get => Read.AreaLightAmbient; set => _cur.AreaLightAmbient = value; }
    /// <summary>The slot value the lit stage reads as "ambient class"; -1 stays "no cube".</summary>
    public const int AmbientSlot = -2;
    /// <summary>This light is in the lightmap pages (the three-branch static rule, classified at the walk).
    /// A baked surface skips it in the real-time loop; uploaded as the static bit in the record's w.</summary>
    public static bool[] AreaLightStatic { get => Read.AreaLightStatic; set => _cur.AreaLightStatic = value; }
    /// <summary>A static light's index in the bake's set (-1 = none), uploaded above the static bit: what a
    /// baked surface's pick names its mover shares by.</summary>
    public static int[] AreaLightSetIndex { get => Read.AreaLightSetIndex; set => _cur.AreaLightSetIndex = value; }
    /// <summary>Area lights that resolved to a real cube slot on the last walk. Zero while cubes render is
    /// exactly the "shadows are on but invisible" state -- the receiver's no-slot path returns full light
    /// and says nothing.</summary>
    public static int LightsWithCube { get => Read.LightsWithCube; set => _cur.LightsWithCube = value; }
    /// <summary>Restart the snapshot (top of each master-list walk -- the walk is idempotent, so the latest
    /// walk simply wins). <paramref name="expect"/> is the master list's own count: sizing off it means a
    /// dense area grows the arrays once, at the top of its first walk, instead of doubling mid-walk.</summary>
    public static void BeginAreaLights(int expect)
    {
        LightCount = 0;
        if (expect > AreaLightPosRange.Length) GrowArea(expect);
    }

    private static void GrowArea(int n)
    {
        Array.Resize(ref _cur.AreaLightPosRange, n); Array.Resize(ref _cur.AreaLightColor, n);
        Array.Resize(ref _cur.AreaLightId, n); Array.Resize(ref _cur.AreaLightSlot, n); Array.Resize(ref _cur.AreaLightAmbient, n);
        Array.Resize(ref _cur.AreaLightStatic, n); Array.Resize(ref _cur.AreaLightSetIndex, n);
    }

    public static void RecordAreaLight(IntPtr id, Vector3 pos, float range, Vector3 color, bool ambient = false, bool isStatic = false, int setIndex = -1)
    {
        if (LightCount == AreaLightPosRange.Length) GrowArea(LightCount * 2);
        AreaLightPosRange[LightCount] = new Vector4(pos, range);
        AreaLightColor[LightCount] = color;
        AreaLightId[LightCount] = id;
        AreaLightAmbient[LightCount] = ambient;
        AreaLightStatic[LightCount] = isStatic && !ambient;
        AreaLightSetIndex[LightCount] = setIndex;
        AreaLightSlot[LightCount++] = -1;
    }

    /// <summary>The frame's inputs as a VALUE the frame owns, in two instances: <see cref="_cur"/> is the game
    /// thread's, written by the walks, and <see cref="_pub"/> is what the render side reads once
    /// <see cref="Publish"/> copies one into the other at the frame boundary. Not armed: nothing publishes, and
    /// the array properties return the READER's array, so a writer of ELEMENTS goes through <see cref="Cur"/>.</summary>
    public sealed class Frame
    {
        public bool HasSun;
        public Vector3 SunDir;
        public bool HasCamera;
        public Vector3 CameraPos;
        public Quaternion CameraRot;
        public bool HasSceneCam;
        public Vector3 SceneCamPos;
        public Vector3 SceneCamFwd;
        public float SceneCamP11;
        public bool HasPerspective;
        public float CamFovy;
        public float CamAspect;
        public float CamNear;
        public float CamFar;
        public float CamViewW;
        public float CamViewH;
        public bool FogOn;
        public float FogStart;
        public float FogEnd;
        public Vector3 FogColor;
        public bool HasAreaAmbient;
        public bool HasAreaDiffuse;
        public Vector3 AreaAmbient;
        public Vector3 AreaDiffuse;
        public Vector3 AreaDiffuseDir;
        public bool Blind;
        public int LocalLightCount;
        public IntPtr[] LocalLightId = new IntPtr[InitialLights];
        public Vector3[] LocalLightPos = new Vector3[InitialLights];
        public float[] LocalLightRange = new float[InitialLights];
        public Vector3[] LocalLightColor = new Vector3[InitialLights];
        public bool Interior;
        public bool Night;
        public Vector2 AreaExtent;
        public IntPtr AreaPtr;
        public bool HasFocus;
        public Vector3 Focus;
        public int LightCount;
        public Vector4[] AreaLightPosRange = new Vector4[InitialLights];
        public Vector3[] AreaLightColor = new Vector3[InitialLights];
        public IntPtr[] AreaLightId = new IntPtr[InitialLights];
        public int[] AreaLightSlot = new int[InitialLights];
        public bool[] AreaLightAmbient = new bool[InitialLights];
        public bool[] AreaLightStatic = new bool[InitialLights];
        public int[] AreaLightSetIndex = new int[InitialLights];
        public int LightsWithCube;
    }

    private static readonly Frame _cur = new();
    private static Frame? _pub;

    /// <summary>What a READER sees: the published frame once one exists, else the live one. Every property
    /// above reads through this and writes to <see cref="_cur"/>, which is what makes the switch a single
    /// assignment rather than 26 files of edits.</summary>
    private static Frame Read => _pub ?? _cur;

    /// <summary>The game thread's own copy -- what the walks write, and what a producer must use when it
    /// needs the array objects themselves (growth, <c>Array.Resize</c>).</summary>
    internal static Frame Cur => _cur;

    /// <summary>Publish this frame's inputs for the render side: on the GAME thread at the frame boundary,
    /// after the walks and before the draw is handed over, allocation-free in the steady state. The renderer
    /// is single-threaded today, so this has no caller at all; the <see cref="Frame"/> header says what the
    /// first one owes.</summary>
    public static void Publish()
    {
        Frame dst = _pub ??= new Frame();
        Copy(_cur, dst);
    }

    /// <summary>Stop publishing: the render thread stood down, so readers go back to the live frame. The
    /// published copy is kept rather than dropped -- re-arming reuses its arrays.</summary>
    public static void Unpublish() => _pub = null;

    private static void Copy(Frame src, Frame dst)
    {
            dst.HasSun = src.HasSun;
            dst.SunDir = src.SunDir;
            dst.HasCamera = src.HasCamera;
            dst.CameraPos = src.CameraPos;
            dst.CameraRot = src.CameraRot;
            dst.HasSceneCam = src.HasSceneCam;
            dst.SceneCamPos = src.SceneCamPos;
            dst.SceneCamFwd = src.SceneCamFwd;
            dst.SceneCamP11 = src.SceneCamP11;
            dst.HasPerspective = src.HasPerspective;
            dst.CamFovy = src.CamFovy;
            dst.CamAspect = src.CamAspect;
            dst.CamNear = src.CamNear;
            dst.CamFar = src.CamFar;
            dst.CamViewW = src.CamViewW;
            dst.CamViewH = src.CamViewH;
            dst.FogOn = src.FogOn;
            dst.FogStart = src.FogStart;
            dst.FogEnd = src.FogEnd;
            dst.FogColor = src.FogColor;
            dst.HasAreaAmbient = src.HasAreaAmbient;
            dst.HasAreaDiffuse = src.HasAreaDiffuse;
            dst.AreaAmbient = src.AreaAmbient;
            dst.AreaDiffuse = src.AreaDiffuse;
            dst.AreaDiffuseDir = src.AreaDiffuseDir;
            dst.Blind = src.Blind;
            dst.LocalLightCount = src.LocalLightCount;
            dst.Interior = src.Interior;
            dst.Night = src.Night;
            dst.AreaExtent = src.AreaExtent;
            dst.AreaPtr = src.AreaPtr;
            dst.HasFocus = src.HasFocus;
            dst.Focus = src.Focus;
            dst.LightCount = src.LightCount;
            dst.LightsWithCube = src.LightsWithCube;
            Grow(ref dst.LocalLightId, src.LocalLightId.Length);
            Array.Copy(src.LocalLightId, dst.LocalLightId, src.LocalLightId.Length);
            Grow(ref dst.LocalLightPos, src.LocalLightPos.Length);
            Array.Copy(src.LocalLightPos, dst.LocalLightPos, src.LocalLightPos.Length);
            Grow(ref dst.LocalLightRange, src.LocalLightRange.Length);
            Array.Copy(src.LocalLightRange, dst.LocalLightRange, src.LocalLightRange.Length);
            Grow(ref dst.LocalLightColor, src.LocalLightColor.Length);
            Array.Copy(src.LocalLightColor, dst.LocalLightColor, src.LocalLightColor.Length);
            Grow(ref dst.AreaLightPosRange, src.AreaLightPosRange.Length);
            Array.Copy(src.AreaLightPosRange, dst.AreaLightPosRange, src.AreaLightPosRange.Length);
            Grow(ref dst.AreaLightColor, src.AreaLightColor.Length);
            Array.Copy(src.AreaLightColor, dst.AreaLightColor, src.AreaLightColor.Length);
            Grow(ref dst.AreaLightId, src.AreaLightId.Length);
            Array.Copy(src.AreaLightId, dst.AreaLightId, src.AreaLightId.Length);
            Grow(ref dst.AreaLightSlot, src.AreaLightSlot.Length);
            Array.Copy(src.AreaLightSlot, dst.AreaLightSlot, src.AreaLightSlot.Length);
            Grow(ref dst.AreaLightAmbient, src.AreaLightAmbient.Length);
            Array.Copy(src.AreaLightAmbient, dst.AreaLightAmbient, src.AreaLightAmbient.Length);
            Grow(ref dst.AreaLightStatic, src.AreaLightStatic.Length);
            Array.Copy(src.AreaLightStatic, dst.AreaLightStatic, src.AreaLightStatic.Length);
            Grow(ref dst.AreaLightSetIndex, src.AreaLightSetIndex.Length);
            Array.Copy(src.AreaLightSetIndex, dst.AreaLightSetIndex, src.AreaLightSetIndex.Length);
    }

    private static void Grow<T>(ref T[] a, int n) { if (a.Length < n) a = new T[n]; }

}
