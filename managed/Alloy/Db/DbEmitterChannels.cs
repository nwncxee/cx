using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>Where an emitter's animated tracks land:</b> the sink <c>DbAnim.ChannelSink</c> calls for
/// emitter parts, read back by <see cref="EmitterSim"/>. Sparse -- an emitter declares ~35 channels
/// (<see cref="Ctl"/>'s <c>Em*</c> block, ids 80..472) and animates two or three, so a block is allocated on
/// its first write and freed with its part. Writes BLEND: <c>lerp(column, key, weight)</c> off the rest keys.</summary>
internal static class DbEmitterChannels
{
    /// <summary>Emitters that may animate at once, which is every emitter part the input table can hold:
    /// a city tileset registers thousands, so any lower ceiling is a cliff an effect falls off silently.
    /// Past it the write is refused and named on the period line, never dropped in silence.</summary>
    internal const int MaxBlocks = DbInputs.MaxEmitters;

    /// <summary>The controller ids this sink accepts, in order. Index into a block is the position here.</summary>
    private static readonly int[] _ids =
    {
        Ctl.EmAlphaEnd, Ctl.EmAlphaStart, Ctl.EmBirthRate, Ctl.EmBounceCo, Ctl.EmColorEnd,
        Ctl.EmColorStart, Ctl.EmCombineTime, Ctl.EmDrag, Ctl.EmFps, Ctl.EmFrameEnd,
        Ctl.EmFrameStart, Ctl.EmGrav, Ctl.EmLifeExp, Ctl.EmMass, Ctl.EmP2PBezier2,
        Ctl.EmP2PBezier3, Ctl.EmParticleRot, Ctl.EmRandVel, Ctl.EmSizeStart, Ctl.EmSizeEnd,
        Ctl.EmSizeStartY, Ctl.EmSizeEndY, Ctl.EmSpread, Ctl.EmThreshold, Ctl.EmVelocity,
        Ctl.EmXSize, Ctl.EmYSize, Ctl.EmBlurLength, Ctl.EmLightningDelay, Ctl.EmLightningRadius,
        Ctl.EmLightningScale, Ctl.EmDetonate, Ctl.EmAlphaMid, Ctl.EmColorMid,
    };

    internal static int Channels => _ids.Length;

    /// <summary>Controller id -> channel index, or -1. Built once from <see cref="_ids"/>; the ids are sparse
    /// over 80..472, so a direct table is 473 bytes and beats a search per sampled key.</summary>
    private static readonly short[] _index = BuildIndex();

    private static short[] BuildIndex()
    {
        int max = 0;
        foreach (int id in _ids) if (id > max) max = id;
        var t = new short[max + 1];
        Array.Fill(t, (short)-1);
        for (int k = 0; k < _ids.Length; k++) t[_ids[k]] = (short)k;
        return t;
    }

    /// <summary>Channel index for a controller id, or -1 when it is not an emitter channel.</summary>
    internal static int ChannelOf(int type)
        => (uint)type < (uint)_index.Length ? _index[type] : -1;

    private static readonly Vector4[] _value = new Vector4[MaxBlocks * 34];
    private static readonly int[] _blockOf = new int[SceneDb.MaxParts];
    private static readonly int[] _partOf = new int[MaxBlocks];
    private static int _blockN;
    private static long _writes, _refused, _unknown;

    internal static int Blocks => _blockN;

    /// <summary>The sink seam-anim calls. Hot: a table lookup, a block index and a lerp.</summary>
    internal static void Write(int part, int type, Vector4 value, float weight)
    {
        int ch = ChannelOf(type);
        if (ch < 0) { _unknown++; return; }
        int b = BlockFor(part);
        if (b < 0) { _refused++; return; }
        int at = b * Channels + ch;
        _value[at] = weight >= 1f ? value : Vector4.Lerp(_value[at], value, weight);
        _writes++;
        SceneDb.TouchPart(part, Dirty.Material);
    }

    /// <summary>This part's animated value for <paramref name="type"/>, or <paramref name="rest"/> when no
    /// animation has written it. The same gate shape the light columns keep: a block that does not exist
    /// means "unwritten", so a real value of zero is not mistaken for one.</summary>
    internal static Vector4 Value(int part, int type, Vector4 rest)
    {
        int ch = ChannelOf(type);
        if (ch < 0 || (uint)part >= SceneDb.MaxParts) return rest;
        int b = _blockOf[part] - 1;
        return b < 0 ? rest : _value[b * Channels + ch];
    }

    /// <summary>This part's block index, or -1 when no animation has ever written it. The fill reads about
    /// thirty channels per emitter per frame; asked once here rather than per channel in <see cref="Value"/>
    /// (3,0% of the game thread), the whole unanimated case never touches this table.</summary>
    internal static int BlockOf(int part)
        => (uint)part < SceneDb.MaxParts ? _blockOf[part] - 1 : -1;

    /// <summary>One channel of a block <see cref="BlockOf"/> handed back, by channel INDEX -- the caller
    /// resolved the controller id once, so this is the array read with nothing in front of it. Out of range
    /// reads default, which is what a block cannot hold.</summary>
    internal static Vector4 At(int b, int ch)
        => (uint)b < (uint)_blockN && (uint)ch < (uint)Channels ? _value[b * Channels + ch] : default;

    private static int BlockFor(int part)
    {
        if ((uint)part >= SceneDb.MaxParts) return -1;
        int b = _blockOf[part] - 1;
        if (b >= 0) return b;
        if (_blockN >= MaxBlocks) return -1;
        b = _blockN++;
        _blockOf[part] = b + 1;
        _partOf[b] = part;
        Seed(part, b);
        return b;
    }

    /// <summary>Seed a fresh block from the node's own rest keys, so the first blended write lerps from the
    /// value the emitter actually had rather than from zero.</summary>
    private static void Seed(int part, int b)
    {
        ModelDef? m = SceneDb.PartModel[part];
        NodeDef? nd = m != null && (uint)SceneDb.PartNode[part] < (uint)m.Nodes.Length
                    ? m.Nodes[SceneDb.PartNode[part]] : null;
        for (int ch = 0; ch < Channels; ch++)
        {
            ControllerTrack? c = nd?.Controller(_ids[ch]);
            _value[b * Channels + ch] = c == null || c.Values.Length == 0 ? default
                : new Vector4(c.Values[0],
                              c.Columns > 1 && c.Values.Length > 1 ? c.Values[1] : 0f,
                              c.Columns > 2 && c.Values.Length > 2 ? c.Values[2] : 0f,
                              c.Columns > 3 && c.Values.Length > 3 ? c.Values[3] : 0f);
        }
    }

    /// <summary>This part died or was re-tenanted: drop its block, so a recycled part index cannot inherit
    /// another emitter's animation. Swap-with-last, like every other table here.</summary>
    internal static void Forget(int part)
    {
        if ((uint)part >= SceneDb.MaxParts) return;
        int b = _blockOf[part] - 1;
        if (b < 0) return;
        _blockOf[part] = 0;
        int last = --_blockN;
        if (b != last)
        {
            Array.Copy(_value, last * Channels, _value, b * Channels, Channels);
            _partOf[b] = _partOf[last];
            _blockOf[_partOf[b]] = b + 1;
        }
    }

    /// <summary>Install the sink. Called at boot beside the feed's other lifetime wiring.</summary>
    internal static void Attach()
    {
        DbAnim.ChannelSink = Write;
    }

    /// <summary>The session edge <c>DbInputs.Reset</c> is: every block at once, because the part table that
    /// drives the per-part <see cref="Forget"/> is cleared in the same breath and a block left behind is one
    /// a re-tenanted part index inherits.</summary>
    internal static void ForgetAll()
    {
        for (int b = 0; b < _blockN; b++) _blockOf[_partOf[b]] = 0;
        _blockN = 0; _writes = 0; _refused = 0; _unknown = 0;
    }

    /// <summary>For the period line. A refusal is spelt out rather than counted quietly: the emitter it hit
    /// falls back to its node's rest keys, which for a VFX whose size or alpha is entirely animated is
    /// nothing drawn at all.</summary>
    internal static string Stats()
        => $"dbemit: {_blockN} animated, {_writes} writes"
         + (_refused > 0 ? $" REFUSED {_refused} write(s) past {MaxBlocks} blocks -- those emitters are "
                         + "drawing their REST keys" : "")
         + (_unknown > 0 ? $", {_unknown} non-emitter channel(s)" : "");

    /// <summary>Off-device: the sparse mapping, the blend, the rest-key seed and the recycle. An emitter
    /// whose block leaked to a reused part index would animate with a stranger's rates, which reads as a
    /// visual effect behaving differently each time it plays.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbEmitterChannels.SelfCheck");
        ForgetAll();

        check.Expect(ChannelOf(Ctl.EmBirthRate) >= 0, "an emitter channel maps");
        check.Expect(ChannelOf(Ctl.LightColor) < 0, "a light channel is not an emitter channel");
        check.Expect(ChannelOf(Ctl.Position) < 0, "...nor is a geometry one");
        check.Expect(ChannelOf(int.MaxValue) < 0, "...nor is an id past the table");

        ModelDef m = EmitterModel();
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId h = SceneDb.Parts.Alloc();
        int p = h.Index;
        DbFeed.InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = m;
        SceneDb.PartNode[p] = 1;

        // Unwritten reads the caller's rest value, not zero.
        check.Expect(Value(p, Ctl.EmBirthRate, new Vector4(9f, 0f, 0f, 0f)).X == 9f,
                     "an unwritten channel reads the caller's rest value");
        check.Expect(Blocks == 0, "...and allocates no block for asking");

        // A full-weight write lands exactly; the block seeds from the node's rest keys.
        Write(p, Ctl.EmBirthRate, new Vector4(20f, 0f, 0f, 0f), 1f);
        check.Expect(Blocks == 1, "the first write allocates one block");
        check.Expect(Value(p, Ctl.EmBirthRate, default).X == 20f, "a full-weight write lands exactly");
        // The seed is the node's own rest key (5), so a half-weight write from it lands halfway.
        check.Expect(Value(p, Ctl.EmVelocity, default).X == 5f,
                     "an untouched channel of an allocated block still reads its REST key, not zero");
        Write(p, Ctl.EmVelocity, new Vector4(15f, 0f, 0f, 0f), 0.5f);
        check.Expect(MathF.Abs(Value(p, Ctl.EmVelocity, default).X - 10f) < 1e-4f,
                     "a half-weight write blends from the value the emitter had (5 -> 15 at 0.5 = 10)");

        // A non-emitter channel is counted, not applied.
        long before = _unknown;
        Write(p, Ctl.LightColor, Vector4.One, 1f);
        check.Expect(_unknown == before + 1, "a non-emitter channel is counted rather than applied");

        // The recycle: a dead part's block must not reach its successor.
        Forget(p);
        check.Expect(Blocks == 0, "a destroyed emitter drops its block");
        check.Expect(Value(p, Ctl.EmBirthRate, new Vector4(7f, 0f, 0f, 0f)).X == 7f,
                     "...and the part index reads unwritten again, not the dead emitter's rate");

        // The CEILING, over the population a real area has. A refused block is not a dropped diagnostic:
        // the emitter falls back to its node's REST keys, and a VFX whose size or alpha is entirely
        // animated (zero size, negative alpha at rest) then draws nothing at all.
        const int Crowd = 300;
        var crowd = new DbId[Crowd];
        for (int k = 0; k < Crowd; k++)
        {
            crowd[k] = SceneDb.Parts.Alloc();
            int c = crowd[k].Index;
            DbFeed.InitPart(c);
            SceneDb.PartObj[c] = obj; SceneDb.PartModel[c] = m; SceneDb.PartNode[c] = 1;
            Write(c, Ctl.EmBirthRate, new Vector4(k + 1f, 0f, 0f, 0f), 1f);
        }
        check.Expect(Value(crowd[Crowd - 1].Index, Ctl.EmBirthRate, default).X == 300f,
                     "the 300th animated emitter of an area keeps its own birth rate rather than falling "
                     + "back to its node's rest key");
        check.Expect(!Stats().Contains("REFUSED", StringComparison.Ordinal),
                     "...with no block refused along the way");

        // The session edge releases every block at once; the per-part path cannot be relied on there,
        // because the table it walks is cleared in the same breath.
        ForgetAll();
        check.Expect(Blocks == 0, "a session edge releases every block");
        check.Expect(Value(crowd[0].Index, Ctl.EmBirthRate, new Vector4(4f, 0f, 0f, 0f)).X == 4f,
                     "...and every part reads unwritten again, not the dead area's rates");
        for (int k = 0; k < Crowd; k++) SceneDb.Parts.Free(crowd[k]);
        SceneDb.Parts.TakeDirty();

        // A node whose REST alpha is negative is the shape this ceiling bites: the file states -0.4 and the
        // animation lifts it to 0.5, so reading the rest key is an invisible effect rather than a dim one.
        Write(p, Ctl.EmAlphaStart, new Vector4(0.5f, 0f, 0f, 0f), 1f);
        check.Expect(Value(p, Ctl.EmAlphaStart, default).X == 0.5f,
                     "an animated alpha reads the key, not the node's negative rest value");
        Forget(p);
        check.Expect(Value(p, Ctl.EmAlphaStart, new Vector4(-0.4f, 0f, 0f, 0f)).X == -0.4f,
                     "...and without a block it reads that rest value, which clamps to nothing drawn");

        // Two blocks, drop the FIRST: the swap-with-last must keep the survivor addressable.
        DbId h2 = SceneDb.Parts.Alloc();
        int q = h2.Index;
        DbFeed.InitPart(q);
        SceneDb.PartObj[q] = obj; SceneDb.PartModel[q] = m; SceneDb.PartNode[q] = 1;
        Write(p, Ctl.EmBirthRate, new Vector4(1f, 0f, 0f, 0f), 1f);
        Write(q, Ctl.EmBirthRate, new Vector4(2f, 0f, 0f, 0f), 1f);
        check.Expect(Blocks == 2, "two animated emitters, two blocks");
        Forget(p);
        check.Expect(Blocks == 1 && Value(q, Ctl.EmBirthRate, default).X == 2f,
                     "dropping the first block leaves the second addressable and unchanged");

        Forget(q);
        SceneDb.Parts.Free(h); SceneDb.Parts.Free(h2); SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
        ForgetAll();

        log.Info(check.Ok ? "DbEmitterChannels.SelfCheck: passed"
                          : "DbEmitterChannels.SelfCheck: FAILED -- an emitter would animate with the wrong "
                          + "rates, or with a dead emitter's");
        return check.Ok;
    }

    private static ModelDef EmitterModel()
    {
        var m = new ModelDef { Name = "cx_emitcheck", ContentHash = 0xE817E8 };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var emit = new NodeDef
        {
            Name = "puff", Index = 1, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef { Update = "fountain", Render = "normal" },
            Controllers = new[]
            {
                new ControllerTrack { Type = Ctl.EmVelocity, Columns = 1, Times = new[] { 0f }, Values = new[] { 5f } },
            },
        };
        m.Nodes = new[] { root, emit };
        m.NodeByName["root"] = 0; m.NodeByName["puff"] = 1;
        return m;
    }
}
