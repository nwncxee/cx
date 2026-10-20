using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Engine;

/// <summary>The transform an object or an effect is drawn with: the engine's map from scope to a lerped scale,
/// rotation and translation, empty when nothing transforms it. <see cref="Fingerprint"/> is how CX tells one
/// transform from another without keeping the map.</summary>
public readonly unsafe partial struct ObjectVisualTransformData
{
    /// <summary>How many lerped values one transform holds: ScaleX..Z, RotateX..Z, TranslateX..Z and
    /// AnimationSpeed, in that order.</summary>
    public const int Lerps = 10;   // not-an-offset: how many LerpFloats a CAurObjectVisualTransformData holds

    private const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;

    /// <inheritdoc cref="EngineLayout.TreeCount"/>
    public int Count => Ptr == IntPtr.Zero ? 0 : *(int*)(Ptr + EngineLayout.TreeCount);

    /// <inheritdoc cref="EngineLayout.TreeHead"/>
    private IntPtr Head => OperatingSystem.IsWindows() ? *(IntPtr*)(Ptr + EngineLayout.TreeHead) : Ptr + EngineLayout.TreeHead;

    /// <summary>The tree's first entry, or zero while the map is empty -- which each runtime says its own way.</summary>
    private IntPtr First
    {
        get
        {
            IntPtr head = Head;
            if (Count == 0 || head == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = *(IntPtr*)(head + EngineLayout.TreeParent);
            return root == head ? IntPtr.Zero : root;
        }
    }

    /// <summary>A number that changes when this transform's contents change: the entry count, the first entry's
    /// scope and the declared bytes of its lerped values. The identity transform is the count alone. A lerp that
    /// repeats swaps its own ends as it turns, so such a transform fingerprints anew once a cycle.</summary>
    public ulong Fingerprint
    {
        get
        {
            ulong h = Mix(FnvOffset, (ulong)(uint)Count);
            IntPtr first = First;
            if (first == IntPtr.Zero) return h;
            h = Mix(h, (ulong)(uint)*(int*)(first + EngineLayout.TreeKey));
            // ponytail: the first entry only. A second scope changes the count, so it shows; an edit inside one does
            // not. The upgrade is a walk of every node -- parent links, nil being null on Linux and the head on Windows.
            for (int i = 0; i < Lerps; i++)
                foreach (ulong word in MemoryMarshal.Cast<byte, ulong>(LerpAt(first, i).Declared))
                    h = Mix(h, word);
            return h;
        }
    }

    private static LerpFloat LerpAt(IntPtr node, int i) =>
        new(node + EngineLayout.TreeMapped + i * EngineLayout.LerpFloatSizeof);

    private static ulong Mix(ulong h, ulong v) => (h ^ v) * FnvPrime;
}
