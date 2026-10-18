using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Physics;

/// <summary>How a dangly node moves, chosen once from its authored numbers (docs/spec/dangly-motions.md).
/// Static has no moving vertex; Sway is free at the top (vegetation); Jiggle is well damped (body parts,
/// creature appendages); Swing is everything that hangs (cloth, rigging).</summary>
public enum DanglyClass : byte { Static, Sway, Jiggle, Swing }

/// <summary>A dangly node's generated rig: its class, vanilla's frequency, damping and wind gain carried over,
/// the joints the solver moves and the skin weights that bind the mesh to them. Built once per node at load,
/// immutable and shared by every instance, like the <see cref="NodeDef"/> it hangs off.</summary>
public sealed partial class DanglyRig
{
    /// <summary>Joints one rig may carry: the largest grid the spec allows, four columns of four segments
    /// plus each column's kinematic root.</summary>
    public const int MaxJoints = 20;

    public DanglyClass Class;
    /// <summary>Per render vertex: 0 for an anchor (constraint 0 or 255+), else <c>1 - c/255</c>.</summary>
    public float[] Mobility = Array.Empty<float>();
    /// <summary>Vanilla's spring as a rate (rad/s), its damping ratio, and its steady wind gain per unit wind.</summary>
    public float Omega, Zeta, WindGain;
    /// <summary>Model-down in this node's rest frame: where gravity pointed when the node was authored.</summary>
    public Vector3 RestDown = -Vector3.UnitZ;
    /// <summary>Swing's grid; 1 x 1 for Sway and Jiggle.</summary>
    public int Columns = 1, Segments = 1;
    /// <summary>Per joint, node-space rest position, parent joint (-1 = kinematic root, moves with the part)
    /// and excursion cap in node units.</summary>
    public Vector3[] JointRest = Array.Empty<Vector3>();
    public int[] JointParent = Array.Empty<int>();
    public float[] JointCap = Array.Empty<float>();
    /// <summary>Per joint, the symmetric drag tensor: (xx, yy, zz) and (xy, xz, yz).</summary>
    public Vector3[] DragDiag = Array.Empty<Vector3>(), DragOff = Array.Empty<Vector3>();
    /// <summary>Bone <c>b</c> is the segment ending at joint <c>BoneJoint[b]</c>; bone 0 is the part itself
    /// (<c>BoneJoint[0] = -1</c>, always identity).</summary>
    public int[] BoneJoint = Array.Empty<int>();
    /// <summary>The weights <c>MeshArena.Acquire</c> lays into the skinned lanes; slots index bones.</summary>
    public SkinDef? Skin;
    /// <summary>Jiggle: the tip may leave its rest distance. Sway: the bone is a pure translation (a shear).</summary>
    public bool Stretch, Shear;

    public int BoneCount => BoneJoint.Length;

    /// <summary>Build every dangly node's rig after the post-process has settled its rest pose.</summary>
    public static void BuildAll(ModelDef m)
    {
        foreach (NodeDef n in m.Nodes)
            if (n.Kind == NodeKind.Dangly && n.Mesh != null && n.Dangly != null) n.Rig = Build(m, n);
    }

}
