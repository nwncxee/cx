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
    public static bool HasSun { get => _cur.HasSun; set => _cur.HasSun = value; }
    /// <summary>Normalized world-space (Z-up) direction the sunlight travels (from <c>PartLight+0xac</c>).</summary>
    public static Vector3 SunDir { get => _cur.SunDir; set => _cur.SunDir = value; }
    /// <summary>True once a camera has been read this session.</summary>
    public static bool HasCamera { get => _cur.HasCamera; set => _cur.HasCamera = value; }
    /// <summary>World-space camera position (<c>*CurrentCamera + 0xb0</c>).</summary>
    public static Vector3 CameraPos { get => _cur.CameraPos; set => _cur.CameraPos = value; }
    /// <summary>Camera orientation (<c>*CurrentCamera + 0xbc</c>, re-ordered to Numerics' (x, y, z, w)).</summary>
    public static Quaternion CameraRot { get => _cur.CameraRot; set => _cur.CameraRot = value; }
    /// <summary>True once the scene camera has been read this session.</summary>
    public static bool HasSceneCam { get => _cur.HasSceneCam; set => _cur.HasSceneCam = value; }
    /// <summary>World-space position and forward of the camera the SCENE is drawn with -- the SAME read as
    /// <see cref="CameraPos"/>/<see cref="CameraRot"/>, since <c>CurrentCamera</c> inside the world scene's
    /// render IS the scene camera. The pair survives because the shadow path names it, not because the two
    /// can disagree.</summary>
    public static Vector3 SceneCamPos { get => _cur.SceneCamPos; set => _cur.SceneCamPos = value; }
    public static Vector3 SceneCamFwd { get => _cur.SceneCamFwd; set => _cur.SceneCamFwd = value; }
    /// <summary>The scene projection's <c>p11</c> (<c>1 / tan(fovy/2)</c>), from <see cref="CamFovy"/>.</summary>
    public static float SceneCamP11 { get => _cur.SceneCamP11; set => _cur.SceneCamP11 = value; }
    /// <summary>True once a camera with a real view angle has been read.</summary>
    public static bool HasPerspective { get => _cur.HasPerspective; set => _cur.HasPerspective = value; }
    /// <summary>Camera perspective params, the same four the engine hands SetPerspectiveTransform: view
    /// angle in degrees (<c>+0x4c4</c>), viewport aspect (<c>+0x4dc / +0x4e0</c>), near and far
    /// (<c>+0x4cc</c>/<c>+0x4d0</c>).</summary>
    public static float CamFovy { get => _cur.CamFovy; set => _cur.CamFovy = value; }
    public static float CamAspect { get => _cur.CamAspect; set => _cur.CamAspect = value; }
    public static float CamNear { get => _cur.CamNear; set => _cur.CamNear = value; }
    public static float CamFar { get => _cur.CamFar; set => _cur.CamFar = value; }
    /// <summary>The camera's own viewport width and height (<c>+0x4dc</c>/<c>+0x4e0</c>). Not a GL
    /// viewport -- the engine upscales both by the same factor before glViewport -- but it moves exactly
    /// when the GL one does, which is what makes it a usable generation key for the frame's target.</summary>
    public static float CamViewW { get => _cur.CamViewW; set => _cur.CamViewW = value; }
    public static float CamViewH { get => _cur.CamViewH; set => _cur.CamViewH = value; }
    /// <summary>Scene fog, read straight off <c>Scene+0x140..0x14c</c> rather than out of
    /// <c>GLRender::m_fog</c> after asking the engine to fill it. The colour is DISPLAY space, as the block
    /// stores it -- the stages that fog linearise it themselves.</summary>
    public static bool FogOn { get => _cur.FogOn; set => _cur.FogOn = value; }
    /// <summary>Fog start and end distance, and its display-space colour. Meaningful only while
    /// <see cref="FogOn"/>.</summary>
    public static float FogStart { get => _cur.FogStart; set => _cur.FogStart = value; }
    public static float FogEnd { get => _cur.FogEnd; set => _cur.FogEnd = value; }
    /// <summary>Fog colour, display space (see <see cref="FogOn"/>).</summary>
    public static Vector3 FogColor { get => _cur.FogColor; set => _cur.FogColor = value; }
    // --- Area ambient / directional diffuse (#83): the engine's own area terms, the two index -1 lights
    // LightManager::ControlLightsFade pushes -- read since #257 where they live (LightManager +0xa0 / +0x98)
    // rather than intercepted at SetLightGL. Colours are stored LINEAR (the engine runs them through
    // GetColorSpaceFromSRGB before its own uniform); the direction is world-space surface->light.

    /// <summary>True once each area term has been read this session.</summary>
    public static bool HasAreaAmbient { get => _cur.HasAreaAmbient; set => _cur.HasAreaAmbient = value; }
    public static bool HasAreaDiffuse { get => _cur.HasAreaDiffuse; set => _cur.HasAreaDiffuse = value; }
    /// <summary>Linear area ambient / directional diffuse colour, and the world-space surface-&gt;light dir.</summary>
    public static Vector3 AreaAmbient { get => _cur.AreaAmbient; set => _cur.AreaAmbient = value; }
    public static Vector3 AreaDiffuse { get => _cur.AreaDiffuse; set => _cur.AreaDiffuse = value; }
    public static Vector3 AreaDiffuseDir { get => _cur.AreaDiffuseDir; set => _cur.AreaDiffuseDir = value; }
    /// <summary><c>Scene::SetBlindLights</c> is up for the walked scene: one object's lights alone light it, so
    /// the baked pages (every static light) and the fog's sky term stand down with the area pair.</summary>
    public static bool Blind { get => _cur.Blind; set => _cur.Blind = value; }
    // --- The light snapshot: every active point light in the area, read from the LightManager MASTER list
    // once per world frame by SceneInputs. Values are COPIED, never the PartLight* (the engine frees it on
    // area/module boundaries -- the AurTexture lifetime lesson); the id is kept for equality only.
    //
    // #257: no fixed worst-case array survives here. Both sets grow to whatever the master list holds, so
    // there is no ceiling to overflow, no overflow counter to read, and no nearest-K eviction to churn --
    // CasterPass::CubePass does its own nearest-first pick against its cube budget, which is the only
    // selection that was ever load-bearing.

    /// <summary>Shadow-worthy point lights recorded this frame.</summary>
    public static int LocalLightCount { get => _cur.LocalLightCount; set => _cur.LocalLightCount = value; }
    /// <summary>Engine light identity (the <c>PartLight*</c> from the master-list walk) -- the slot-binding
    /// key. NEVER dereferenced outside the walk; used only for equality, where a recycled pointer simply
    /// reads as a new light.</summary>
    public static IntPtr[] LocalLightId { get => _cur.LocalLightId; set => _cur.LocalLightId = value; }
    /// <summary>World-space light position (<c>PlPos</c>, +0xac) -- the cube's bake origin. It is the SAME
    /// value <see cref="AreaLightPosRange"/> shades from, deliberately: a cube baked from anywhere else
    /// computes occlusion for a light that is not where the BRDF thinks it is.</summary>
    public static Vector3[] LocalLightPos { get => _cur.LocalLightPos; set => _cur.LocalLightPos = value; }
    /// <summary>Light reach (cube far plane) = <c>GetLightAdjustedRadius</c>, the engine's own lit radius off
    /// <c>PartLight+0x70</c>: exactly the sphere the receiver's attenuation has support over, so nothing
    /// outside it is worth baking.</summary>
    public static float[] LocalLightRange { get => _cur.LocalLightRange; set => _cur.LocalLightRange = value; }
    /// <summary>Light RGB (<c>+0x64</c>) -- for light-&gt;slot matching / debug, not the cube itself.</summary>
    public static Vector3[] LocalLightColor { get => _cur.LocalLightColor; set => _cur.LocalLightColor = value; }
    /// <summary>Current area carries the ARE interior flag -- no sun, so nothing directional is worth
    /// rendering there. Recomputed once per frame by <see cref="SceneInputs"/> (which walks the engine's
    /// own area chain); false until an area is known, which is the exterior-shaped default.</summary>
    public static bool Interior { get => _cur.Interior; set => _cur.Interior = value; }
    /// <summary>The area is on its night palette: the area directional is the moon. False until an area is known.</summary>
    public static bool Night { get => _cur.Night; set => _cur.Night = value; }
    /// <summary>The current area's world XY extent in metres -- its tile grid times
    /// <see cref="AreaBorder.TileSize"/>, so tile (0,0) spans [0,10) and the area runs to
    /// (TileWidth, TileHeight) * 10. Zero until an area is known. Read once per frame beside
    /// <see cref="Interior"/>; R2's wetness map is fitted to it.</summary>
    public static Vector2 AreaExtent { get => _cur.AreaExtent; set => _cur.AreaExtent = value; }
    /// <summary>The <c>CNWCArea*</c> <see cref="AreaExtent"/> and <see cref="Interior"/> were read from:
    /// compared for the area edge, never dereferenced by a consumer.</summary>
    public static IntPtr AreaPtr { get => _cur.AreaPtr; set => _cur.AreaPtr = value; }
    /// <summary>True once <see cref="Focus"/> holds this frame's shadow focus (the player anchor).</summary>
    public static bool HasFocus { get => _cur.HasFocus; set => _cur.HasFocus = value; }
    /// <summary>The shadow focus (player position) -- the nearest-K key for overflow areas.</summary>
    public static Vector3 Focus { get => _cur.Focus; set => _cur.Focus = value; }
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
    public static int LightCount { get => _cur.LightCount; set => _cur.LightCount = value; }
    /// <summary>World pos (xyz) + lit range (w), packed as one vec4 per light.</summary>
    public static Vector4[] AreaLightPosRange { get => _cur.AreaLightPosRange; set => _cur.AreaLightPosRange = value; }
    /// <summary>Light RGB (sRGB as stored in PartLight; the shader linearizes).</summary>
    public static Vector3[] AreaLightColor { get => _cur.AreaLightColor; set => _cur.AreaLightColor = value; }
    /// <summary>PartLight identity per area light (equality only) -- keys the CPU-side light-&gt;cube-slot
    /// bind.</summary>
    public static IntPtr[] AreaLightId { get => _cur.AreaLightId; set => _cur.AreaLightId = value; }
    /// <summary>Cube slot per area light (-1 = casts no shadow / no cube) -- resolved at the pass slot right
    /// after the bake, so the mapping and the cube contents are the same frame's, and read by the owned
    /// lit stage.</summary>
    public static int[] AreaLightSlot { get => _cur.AreaLightSlot; set => _cur.AreaLightSlot = value; }
    /// <summary>Ambient-class light (PartLight type@0x80 != 0): vanilla adds <c>c * att</c> straight to the
    /// ambient term, no N.L and no shadow. Uploaded as cube slot <see cref="AmbientSlot"/>.</summary>
    public static bool[] AreaLightAmbient { get => _cur.AreaLightAmbient; set => _cur.AreaLightAmbient = value; }
    /// <summary>The slot value the lit stage reads as "ambient class"; -1 stays "no cube".</summary>
    public const int AmbientSlot = -2;
    /// <summary>This light is in the lightmap pages (the three-branch static rule, classified at the walk).
    /// A baked surface skips it in the real-time loop; uploaded as the static bit in the record's w.</summary>
    public static bool[] AreaLightStatic { get => _cur.AreaLightStatic; set => _cur.AreaLightStatic = value; }
    /// <summary>A static light's index in the bake's set (-1 = none), uploaded above the static bit: what a
    /// baked surface's pick names its mover shares by.</summary>
    public static int[] AreaLightSetIndex { get => _cur.AreaLightSetIndex; set => _cur.AreaLightSetIndex = value; }
    /// <summary>Area lights that resolved to a real cube slot on the last walk. Zero while cubes render is
    /// exactly the "shadows are on but invisible" state -- the receiver's no-slot path returns full light
    /// and says nothing.</summary>
    public static int LightsWithCube { get => _cur.LightsWithCube; set => _cur.LightsWithCube = value; }
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

    /// <summary>The frame's inputs as a VALUE the frame owns, in the one instance <see cref="_cur"/> that the
    /// walks write and every pass reads. Every property above is a view on it.</summary>
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
}
