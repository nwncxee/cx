namespace CX.Engine;

/// <summary>Which kind of scene this is, read as the engine reads it.</summary>
public readonly unsafe partial struct Scene
{
    /// <summary>The area's world scene, not a panel's 3D view: only it holds a static tile BSP root, and
    /// <c>Scene::Render</c> forks on the same field.</summary>
    public bool IsWorld => BspRoot != System.IntPtr.Zero;
}
