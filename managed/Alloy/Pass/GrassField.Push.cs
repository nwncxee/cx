using System.Collections.Generic;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The push field: a top-down RGBA8 displacement map held on the CPU around the player, rebuilt only where
/// this frame's pusher list differs from the one the texture holds, so an unchanged list costs no clear, no splat
/// and no upload.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>The grass displacement field: a top-down RGBA8 push map maintained on the CPU around the
    /// player. 256 texels over <see cref="Grass.PushExtent"/> world units is ~0.25u per texel at the default
    /// 64u window, fine enough to shape a footfall. At 256KB the rebuild and the upload are NOT noise, which
    /// is why <see cref="BuildPush"/> is incremental: an unchanged push list skips clear, splats and upload.</summary>
    private const int PushRes = 256;
    /// <summary>Bytes per push texel: RG is the signed lateral direction (128 = none), B is how hard this
    /// texel is being pressed DOWN, and A is the CULL mask -- where a solid object stands, grass is collapsed
    /// in the vertex stage rather than removed from the scatter, because objects move. The flatten term needs
    /// its own channel: directly under a gob there is no direction to lean in, and that is where it is flattest.</summary>
    private const int PushChannels = 4;
    /// <summary>Texture unit the push map binds on. It SHARES its unit with the post resolve, which is safe
    /// only because they are different programs that never run in the same bracket -- and both rebind their
    /// units every pass. <see cref="TextureSlots"/> is the ladder and the only place it is written down.</summary>
    private const uint PushUnit = TextureSlots.GrassPush;

    /// <summary>The push field, CPU side -- four bytes a texel like the <c>RGBA8</c> texture it uploads to,
    /// with 128 as "no push". Allocated once.</summary>
    private static byte[]? _push, _pushClear;
    private static float _pushOriginX, _pushOriginY, _pushPerUnit;
    private static int _pushers, _cullers, _reticles;

    private static long _pushWalked;

    /// <summary>Texels one gob contributes to the push field, in the field's OWN space: every term the two
    /// writers read and nothing else, so two equal lists cannot produce different texels. That is what makes
    /// "the same list as last frame" a sound reason to leave the field and its upload alone.</summary>
    private struct Pusher
    {
        public float X, Y, R, Hx, Hy;
        public bool Same(in Pusher o) => X == o.X && Y == o.Y && R == o.R && Hx == o.Hx && Hy == o.Hy;
    }
    /// <summary>Pushers one frame's list holds. The population is the gobs whose reach touches a window at most
    /// <see cref="Grass.PushExtent"/> (128u) across, which is a fraction of any area -- but it is not a bound we
    /// can prove, so an overflow frame takes the whole-field path (nothing dropped) and says so.</summary>
    private const int MaxPushers = 2048;
    private static readonly Pusher[] _pushNow = new Pusher[MaxPushers], _pushWas = new Pusher[MaxPushers];
    /// <summary>Pushers in this frame's list.</summary>
    private static int _pushNowN;
    /// <summary>The texels <see cref="_push"/> is NOT neutral over -- what a rebuild has to undo and what the
    /// upload has to carry. Empty is <c>x1 &lt; x0</c>.</summary>
    private static int _pushRx0 = PushRes, _pushRy0 = PushRes, _pushRx1 = -1, _pushRy1 = -1;
    private static long _pushSkips, _pushTexels, _pushOver;
    /// <summary>One frame's walk, broken down: they sum to <see cref="_pushWalkedNow"/>. Snapshots of the
    /// last walk rather than period totals, because a breakdown is only readable against the walk it
    /// describes -- the same way <see cref="_pushers"/> and <see cref="_reticles"/> beside them read.</summary>
    private static int _pushOut, _pushNoGeom, _pushIdle, _pushWalkedNow;
    /// <summary>Frames this actually RAN: Prepare counts a frame before it knows whether any grass is
    /// in sight, and returns without a push field on the ones where none is.</summary>
    private static long _pushFrames;
    private static bool _pushOverSaid;

    /// <summary>Bring the displacement field up to date and upload what moved: each gob standing in the field
    /// becomes a radial push away from its centre, summed and clamped. Incremental -- an unchanged list skips
    /// the clear, the splats and the upload outright. ponytail: no persistence, so the field springs back
    /// instead of laying a trail, and one radius for every gob, there being no bounding sphere mapped.</summary>
    private static void BuildPush()
    {
        if (_push == null) return;
        _pushFrames++;
        _pushers = 0; _cullers = 0; _reticles = 0;
        _pushOut = 0; _pushNoGeom = 0; _pushIdle = 0; _pushWalkedNow = 0;
        float extent = MathF.Max(Grass.PushExtent.V, 1f);
        _pushPerUnit = PushRes / extent;
        Vector3 centre = AlloyRenderer.PlayerPos;
        // Snapped to the texel grid: an origin that slides sub-texel makes every blade's sampled push
        // shimmer as the player walks, which reads as the grass twitching for no reason.
        float texel = 1f / _pushPerUnit;
        _pushOriginX = MathF.Floor(centre.X / texel) * texel - extent * 0.5f;
        _pushOriginY = MathF.Floor(centre.Y / texel) * texel - extent * 0.5f;

        // The frame's writes, COLLECTED before the field is touched. The list is texel-space and complete, so
        // a list that repeats cannot produce different texels -- which is the whole gate: a standing player
        // looking at a field nothing is walking through pays the walk and neither the 256KB clear nor the
        // 256KB upload. With `push` at 0 the list is empty, and an empty list that repeats is the same skip.
        EnsureMask();
        bool over = WalkPushers(collect: true);
        if (!over && _pushNowN == _vkg.PushWasN && SamePushers(_pushNowN))
        { _pushSkips++; return; }

        // Only what the LAST list wrote needs undoing; the rest of the field is still the neutral template it
        // was allocated as. An overflow (or a texture whose storage has never been written) takes the whole
        // field instead -- correct, counted, and never the silent half-measure.
        bool full = over;
        _vkg.PushGen++;
        int cx0 = full ? 0 : _pushRx0, cy0 = full ? 0 : _pushRy0;
        int cx1 = full ? PushRes - 1 : _pushRx1, cy1 = full ? PushRes - 1 : _pushRy1;
        ClearPush(_push, cx0, cy0, cx1, cy1);
        _pushRx0 = PushRes; _pushRy0 = PushRes; _pushRx1 = -1; _pushRy1 = -1;
        if (over) WalkPushers(collect: false);
        else for (int i = 0; i < _pushNowN; i++) WritePusher(in _pushNow[i]);
        if (over) _vkg.PushWasN = -1;   // a truncated list must never match next frame
        else { Array.Copy(_pushNow, _pushWas, _pushNowN); _vkg.PushWasN = _pushNowN; }
    }

    /// <summary>Walk the push population once: in <paramref name="collect"/> mode nothing is written and each
    /// accepted object's contribution lands in <see cref="_pushNow"/>; otherwise each is written straight into
    /// the field. True = the list overflowed <see cref="MaxPushers"/>. A creature bends the grass, anything
    /// else also clears it under its world box, and an attached object rides its root's push.</summary>
    private static bool WalkPushers(bool collect)
    {
        if (collect) _pushNowN = 0;
        DbId scene = AlloyRenderer._dbPassScene;
        if (!(Grass.Push.V > 0f) && !Grass.Cull.V || !SceneDb.Scenes.Valid(scene)) return false;
        bool over = false, cullOn = Grass.Cull.V;
        float shrink = Grass.CullShrink.V, cap = Grass.CullMax.V;
        float rTex = Grass.Push.V > 0f ? Grass.PushRadius.V * _pushPerUnit : 0f;
        int high = SceneDb.Objects.High;
        for (int i = 1; i < high; i++)
        {
            if (!SceneDb.Objects.LiveAt(i) || SceneDb.ObjScene[i].Value != scene.Value) continue;
            if (collect) _pushWalkedNow++;
            ObjFlag of = SceneDb.ObjFlags[i];
            if (SceneDb.ObjKindOf[i] != ObjKind.Model || !SceneDb.ObjVisible[i]
                || (of & (ObjFlag.Tile | ObjFlag.Sky | ObjFlag.Gui)) != 0
                || SceneDb.Objects.Valid(SceneDb.ObjAttachTo[i]))
            { if (collect) _pushNoGeom++; continue; }
            if (SceneDb.HitObjClass[i] == ModelClassEffect) { if (collect) _reticles++; continue; }
            Vector3 lo = SceneDb.HitObjBMin[i], hi = SceneDb.HitObjBMax[i];
            if (lo.X > hi.X) { if (collect) _pushNoGeom++; continue; }
            bool creature = (of & ObjFlag.Creature) != 0;
            Vector3 c = creature ? SceneDb.ObjPos[i] : (lo + hi) * 0.5f;
            float hx = 0f, hy = 0f;
            if (cullOn && !creature)
            {
                hx = (hi.X - lo.X) * 0.5f * shrink;
                hy = (hi.Y - lo.Y) * 0.5f * shrink;
                if (cap > 0f) { hx = MathF.Min(hx, cap); hy = MathF.Min(hy, cap); }
                if (!(hx > 0.01f) || !(hy > 0.01f) || hx > 500f || hy > 500f) { hx = 0f; hy = 0f; }
            }
            Pusher p = new() { X = (c.X - _pushOriginX) * _pushPerUnit, Y = (c.Y - _pushOriginY) * _pushPerUnit,
                               R = rTex, Hx = hx * _pushPerUnit, Hy = hy * _pushPerUnit };
            bool splat = SplatBounds(p.X, p.Y, p.R, out int sx0, out int sy0, out int sx1, out int sy1);
            bool cull = CullBounds(p.X, p.Y, p.Hx, p.Hy, out int cx0, out int cy0, out int cx1, out int cy1);
            if (!(splat && OnGrass(sx0, sy0, sx1, sy1)) && !(cull && OnGrass(cx0, cy0, cx1, cy1)))
            { if (collect) _pushOut++; continue; }
            if (!collect) { WritePusher(in p); continue; }
            _pushers++;
            if (p.Hx > 0f) _cullers++;
            if (_pushNowN < MaxPushers) _pushNow[_pushNowN++] = p;
            else if (!over)
            {
                over = true;
                _pushOver++;
                if (!_pushOverSaid)
                {
                    _pushOverSaid = true;
                    _log?.Always($"GrassField: more than {MaxPushers} objects displace the grass at once -- the push "
                               + "field is rebuilt whole every such frame (nothing is dropped, it is just not "
                               + "incremental). Census the area and raise MaxPushers.");
                }
            }
        }
        if (collect) _pushWalked += _pushWalkedNow;
        return over;
    }

    /// <summary>One collected pusher into the field, growing <see cref="_pushRx0"/>..<see cref="_pushRy1"/> by
    /// exactly what the two writers can touch.</summary>
    private static void WritePusher(in Pusher p)
    {
        if (SplatBounds(p.X, p.Y, p.R, out int x0, out int y0, out int x1, out int y1))
        {
            Splat(_push!, p.X, p.Y, p.R);
            GrowPushRect(x0, y0, x1, y1);
        }
        if (CullBounds(p.X, p.Y, p.Hx, p.Hy, out x0, out y0, out x1, out y1))
        {
            CullRect(_push!, p.X, p.Y, p.Hx, p.Hy);
            GrowPushRect(x0, y0, x1, y1);
        }
    }

    /// <summary>Where resident grass lies in the push window, one flag per <see cref="MaskCell"/> texels square: a
    /// pusher whose rects touch no grass bends no blade, so it never enters the list -- in a town that is nearly
    /// every placeable, whose footprint was re-stamped whenever anything else in the window moved.</summary>
    private sealed class PushMaskState
    {
        public readonly bool[] Cell = new bool[MaskRes * MaskRes];
        public float OriginX = float.NaN, OriginY, PerUnit;
        public int Patches = -1, Faces = -1;
    }
    private const int MaskCell = 4, MaskRes = PushRes / MaskCell;
    private static readonly PushMaskState _mask = new();

    /// <summary>Re-mark the grass cells when the window has moved or a patch has baked since the last mark.</summary>
    private static void EnsureMask()
    {
        PushMaskState m = _mask;
        if (m.OriginX == _pushOriginX && m.OriginY == _pushOriginY && m.PerUnit == _pushPerUnit
            && m.Patches == _patchCount && m.Faces == _faceCount) return;
        m.OriginX = _pushOriginX; m.OriginY = _pushOriginY; m.PerUnit = _pushPerUnit;
        m.Patches = _patchCount; m.Faces = _faceCount;
        Array.Clear(m.Cell);
        float k = _pushPerUnit / MaskCell;
        for (int p = 0; p < _patchCount; p++)
        {
            ref Patch q = ref _patches[p];
            if (q.Count <= 0) continue;
            int x0 = (int)MathF.Floor((q.Min.X - _pushOriginX) * k), x1 = (int)MathF.Floor((q.Max.X - _pushOriginX) * k);
            int y0 = (int)MathF.Floor((q.Min.Y - _pushOriginY) * k), y1 = (int)MathF.Floor((q.Max.Y - _pushOriginY) * k);
            if (x1 < 0 || y1 < 0 || x0 >= MaskRes || y0 >= MaskRes) continue;
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, MaskRes - 1); y1 = Math.Min(y1, MaskRes - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) m.Cell[y * MaskRes + x] = true;
        }
    }

    /// <summary>Does the texel rect (inclusive, already clamped to the window) touch a grass cell?</summary>
    private static bool OnGrass(int x0, int y0, int x1, int y1)
    {
        bool[] c = _mask.Cell;
        for (int y = y0 / MaskCell; y <= y1 / MaskCell; y++)
            for (int x = x0 / MaskCell; x <= x1 / MaskCell; x++)
                if (c[y * MaskRes + x]) return true;
        return false;
    }

    private static bool SamePushers(int n)
    {
        for (int i = 0; i < n; i++) if (!_pushNow[i].Same(in _pushWas[i])) return false;
        return true;
    }

    private static void GrowPushRect(int x0, int y0, int x1, int y1)
    {
        if (x0 < _pushRx0) _pushRx0 = x0;
        if (y0 < _pushRy0) _pushRy0 = y0;
        if (x1 > _pushRx1) _pushRx1 = x1;
        if (y1 > _pushRy1) _pushRy1 = y1;
    }

    /// <summary>Put a rectangle of the field back to neutral -- 128,128 for the direction and 0 for the flatten
    /// and the cull, which is not one byte value, so it comes from a template rather than an
    /// <c>Array.Fill</c>.</summary>
    internal static void ClearPush(byte[]? field, int x0, int y0, int x1, int y1)
    {
        if (field == null || _pushClear == null || x1 < x0 || y1 < y0) return;
        int w = (x1 - x0 + 1) * PushChannels;
        for (int y = y0; y <= y1; y++)
        {
            int o = (y * PushRes + x0) * PushChannels;
            Array.Copy(_pushClear, o, field, o, w);
        }
    }

    /// <summary>The texels <see cref="Splat"/> can touch, and false for a splat that touches none. The one
    /// expression: the writer reads it too, so the rect the clear and the upload trust cannot drift from
    /// it.</summary>
    internal static bool SplatBounds(float gx, float gy, float rTex,
                                     out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = 0; x1 = y1 = -1;
        if (rTex <= 0f) return false;
        int lo = (int)(rTex + 1f);
        x0 = Math.Max((int)gx - lo, 0); x1 = Math.Min((int)gx + lo, PushRes - 1);
        y0 = Math.Max((int)gy - lo, 0); y1 = Math.Min((int)gy + lo, PushRes - 1);
        return x1 >= x0 && y1 >= y0;
    }

    /// <summary><see cref="SplatBounds"/>, for <see cref="CullRect"/>.</summary>
    internal static bool CullBounds(float gx, float gy, float hx, float hy,
                                    out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = 0; x1 = y1 = -1;
        if (hx <= 0f || hy <= 0f) return false;
        x0 = Math.Max((int)(gx - hx), 0); x1 = Math.Min((int)(gx + hx) + 1, PushRes - 1);
        y0 = Math.Max((int)(gy - hy), 0); y1 = Math.Min((int)(gy + hy) + 1, PushRes - 1);
        return x1 >= x0 && y1 >= y0;
    }

    /// <summary>One gob's radial push, summed into the field in texel space. Linear falloff outward from the
    /// centre; at the centre itself there is no direction to push in and 0 is the honest answer -- a blade
    /// under the middle of a creature is flattened by the Z term, not flung sideways.</summary>
    internal static void Splat(byte[] field, float gx, float gy, float rTex)
    {
        if (!SplatBounds(gx, gy, rTex, out int x0, out int y0, out int x1, out int y1)) return;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = x + 0.5f - gx, dy = y + 0.5f - gy;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d >= rTex) continue;
            // Smoothstep, not a linear cone: a linear falloff still has a first-derivative step at the rim,
            // and over a field of blades that reads as a visible ring around every creature.
            float t = 1f - d / rTex;
            float m = t * t * (3f - 2f * t);
            // The lateral push SUMS between overlapping gobs (two creatures either side squeeze the grass
            // between them, which is right) ...
            float inv = d > 1e-3f ? m / d : 0f;
            int o = (y * PushRes + x) * PushChannels;
            field[o] = Sat(field[o] + dx * inv * 127f);
            field[o + 1] = Sat(field[o + 1] + dy * inv * 127f);
            // ...while the flatten takes the MAX: grass under two overlapping things is flat, not twice flat.
            byte f = Sat(m * 255f);
            if (f > field[o + 2]) field[o + 2] = f;
        }
    }

    /// <summary>Stamp an object's footprint into the cull channel: a soft-edged rectangle, because a hard one
    /// ends the grass in a visible straight line no object actually has. The edge softness is what the vertex
    /// stage turns into thinning rather than a cut, using each blade's own stable jitter as its threshold.</summary>
    internal static void CullRect(byte[] field, float gx, float gy, float hx, float hy)
    {
        if (!CullBounds(gx, gy, hx, hy, out int x0, out int y0, out int x1, out int y1)) return;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // Normalised distance to the rectangle's edge along whichever axis is closer to it.
            float ex = MathF.Abs(x + 0.5f - gx) / hx, ey = MathF.Abs(y + 0.5f - gy) / hy;
            float e = MathF.Max(ex, ey);
            if (e >= 1f) continue;
            float t = 1f - e;
            byte v = Sat(MathF.Min(t * 3f, 1f) * 255f);
            int o = (y * PushRes + x) * PushChannels;
            if (v > field[o + 3]) field[o + 3] = v;
        }
    }

    private static byte Sat(float v) => (byte)MathF.Round(v < 0f ? 0f : v > 255f ? 255f : v);
}
