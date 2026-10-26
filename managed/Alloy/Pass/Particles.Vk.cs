using System;
using System.Numerics;
using CX.Engine;

namespace CX.Alloy;

internal static unsafe partial class Particles
{
    /// <summary>The Vulkan pass's sort scratch: distance keys and record indices, grown with the frame's particles.</summary>
    private sealed class PartVkState
    {
        public float[] Keys = new float[MinRecs];
        public uint[] Vals = new uint[MinRecs];
    }

    private static PartVkState _vk = new();

    /// <summary>One draw group as the Vulkan pass issues it.</summary>
    internal readonly record struct VkGroup(int Start, int Parts, int Cls, bool Cull, uint Tex, float Emissive);

    /// <summary>The alpha-blended class: sorted back to front, blended over, lit when <see cref="Lit"/> holds.</summary>
    internal const int VkBlend = ClsBlend;
    /// <summary>The additive class, and the punch-through one that writes depth.</summary>
    internal const int VkAdd = ClsAdd, VkPunch = ClsPunch;
    /// <summary>The alpha cut of the punch-through class and of every other.</summary>
    internal const float VkPunchCut = PunchDiscard, VkNoCut = NoDiscard;

    /// <summary>The pass's staging for the Vulkan frame: the records built, the draw-order list filled with the blended
    /// groups sorted back to front on the CPU (GL's compute sort, same key), and every emitter stamped as drawn.
    /// The particles placed; 0 = nothing to draw. ponytail: a CPU sort, a few thousand keys a frame at most.</summary>
    internal static int StageVk(Matrix4x4* view, bool lightingOn, out Vector3 eye, out bool lit, out int groups)
    {
        // the A/B frame's GL half advances the clock for both halves
        if (!VkParity.Active) _frame++;
        eye = default; lit = false; groups = 0;
        if (_off || !Enabled.V || _orderN == 0) return 0;
        int at = Build(view, lightingOn, false, out eye, out lit, out groups);
        if (at == 0) return 0;
        for (int i = 0; i < _drawN; i++)
            for (int j = 0; j < _wrote[i]; j++)
                _idx[_span[i * 4 + 2] + j] = _span[i * 4] + (uint)j;
        PartVkState s = _vk;
        for (int gi = 0; gi < groups; gi++)
        {
            int n = _gParts[gi], from = _gStart[gi];
            if (!Sort.V || !WillSort(_gCls[gi], n)) continue;
            if (s.Keys.Length < n) { s.Keys = new float[n * 2]; s.Vals = new uint[n * 2]; }
            for (int k = 0; k < n; k++)
            {
                uint r = _idx[from + k];
                Vector3 d = _recs[r].Centre - eye;
                s.Keys[k] = -d.LengthSquared();
                s.Vals[k] = r;
            }
            Array.Sort(s.Keys, s.Vals, 0, n);
            for (int k = 0; k < n; k++) _idx[from + k] = s.Vals[k];
            _sorted++;
        }
        for (int i = 0; i < _drawN; i++)
            if (_wrote[i] > 0) { _tab[_sSlot[_draw[i]]].IssuedGen = AlloyRenderer.PassGen; _emitters++; }
        _groups += groups;
        return at;
    }

    /// <summary>Every record block allocated, as the vertex stage indexes them.</summary>
    internal static ReadOnlySpan<ParticleRec> VkRecords => new(_recs, _recTop);

    /// <summary>This frame's draw-order list, <paramref name="at"/> entries.</summary>
    internal static ReadOnlySpan<uint> VkIndices(int at) => new(_idx, at);

    /// <summary>Draw group <paramref name="gi"/>: a DB emitter's texture is its registry name, an engine emitter's
    /// none (it draws the stand-in, counted by the caller).</summary>
    internal static VkGroup VkGroupAt(int gi)
        => new(_gStart[gi], _gParts[gi], _gCls[gi], _gCull[gi] != 0, _gDb[gi] ? (uint)(nint)_gTexObj[gi] : 0u, _gEmis[gi]);

    /// <summary><c>/particles soft</c>, <c>shadows</c> and <c>viz</c>, for the Vulkan pass.</summary>
    internal static bool SoftOn => Soft.V;
    internal static bool ShadowsOn => Shadows.V;
    internal static int VizMode => Viz.V;
}
