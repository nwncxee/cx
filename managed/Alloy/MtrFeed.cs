using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The live source of a <c>.mtr</c>'s render hint: a mesh with no <c>renderhint</c> of its own still gets a
/// tangent frame when the material it names carries one, and only a resource lookup can say so. The hint is
/// wanted on the PARSE WORKER while a lookup is game-thread-only, so a name the cache does not hold yet answers
/// 0 and is queued; the game thread drains the queue at the next model load, and every miss is counted.
/// </summary>
internal static unsafe class MtrFeed
{
    /// <summary>The <c>.mtr</c> restype, measured: scanning <c>nwn_base.key</c> for a resource whose bytes are
    /// <c>.mtr</c> text lands on 2072 and nothing else; 2069 is the neighbouring <c>.shd</c>.</summary>
    private const ushort MtrResType = 2072;   // not-an-offset: a resource type id
    /// <summary>The <c>.shd</c> restype (<c>CResHelper&lt;CResSHD, 2069&gt;</c>, <see cref="ShaderResource"/>'s own).</summary>
    private const ushort ShdResType = 2069;   // not-an-offset: a resource type id
    /// <summary>Materials resolved per pump: the game thread is doing a model read anyway, and a
    /// <c>.mtr</c> is a few hundred bytes.</summary>
    private const int Budget = 32;
    private const int MaxQueued = 4096;

    private static readonly object Gate = new();
    /// <summary>Name -> render hint, 0 when there is no such material. The answer the parse wants.</summary>
    private static readonly Dictionary<string, int> _hint = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Name -> its bytes, so <see cref="MtrDef.Resolve"/> (the parts stream's own consumer) reads
    /// the same cache instead of asking a resource manager from whatever thread it is on.</summary>
    private static readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Name -> the text of a content shader a read material names (<c>customShaderVS</c>/<c>customShaderFS</c>),
    /// null when there is no such file: what <see cref="ContentPrograms.Register"/> tells a stock-shaped pair by.</summary>
    private static readonly Dictionary<string, string?> _shd = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> _want = new();
    private static readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    private static ExtenderLog? _log;
    private static bool _armed;
    private static int _misses, _loaded, _absent, _dropped;
    /// <summary>Materials that exist but could not be read: their meshes draw the bitmap as a texture, so this
    /// is said loudly rather than counted alone.</summary>
    private static int _unreadable;

    /// <summary>Wire both halves. A client where the lookup cannot answer keeps the parser's hint unwired,
    /// which is the loud case the parse already counts.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        if (!ResLocate.Attach(out string? why))
        {
            log.Always($"MtrFeed: stood down -- {why}; a mesh whose render hint lives in a .mtr gets no tangent frame");
            return;
        }
        _armed = true;
        MdlParser.MaterialHint = Hint;
        MtrDef.Bytes = Cached;
        MtrDef.Answered = Answered;
        TileBuild.WarmMaterials = Warm;
    }

    /// <summary>Has this material been ANSWERED -- read, or established absent? A name only
    /// <see cref="Hint"/> has queued has not, and a part built against it may still gain a material.</summary>
    private static bool Answered(string name)
    {
        lock (Gate) return _bytes.ContainsKey(name);
    }

    /// <summary>Parse worker: the hint, or 0 and a queued read.</summary>
    private static int Hint(string name)
    {
        if (name.Length == 0) return 0;
        lock (Gate)
        {
            if (_hint.TryGetValue(name, out int h)) return h;
            if (_want.Count >= MaxQueued) { _dropped++; return 0; }
            if (_queued.Add(name)) _want.Enqueue(name);
        }
        Interlocked.Increment(ref _misses);
        return 0;
    }

    /// <summary>Whatever the cache already holds; null for a name no pump has reached, which is queued for the
    /// next one. Never reads.</summary>
    private static byte[]? Cached(string name)
    {
        lock (Gate) if (_bytes.TryGetValue(name, out byte[]? b)) return b;
        Hint(name);
        return null;
    }

    /// <summary>GAME thread: resolve up to <see cref="Budget"/> queued materials. Called from the model-load
    /// funnel, which is the one place a material is about to be wanted and the thread is already on I/O.</summary>
    internal static void Pump() => Pump(Budget);

    /// <summary>GAME thread: resolve up to <paramref name="budget"/> queued materials.</summary>
    internal static void Pump(int budget)
    {
        if (!_armed) return;
        for (int i = 0; i < budget; i++)
        {
            string? name;
            lock (Gate)
            {
                if (_want.Count == 0) return;
                name = _want.Dequeue();
                _queued.Remove(name);
                if (_hint.ContainsKey(name)) continue;
            }
            Read(name);
        }
    }

    /// <summary>GAME thread: read every material <paramref name="model"/>'s meshes draw with that the cache
    /// does not know yet, so the part tree built next resolves them instead of drawing the bitmap as a
    /// texture. Cold: object creation, one read per new name per module.</summary>
    internal static void Warm(ModelDef model)
    {
        if (!_armed) return;
        foreach (NodeDef n in model.Nodes)
        {
            if (n.Mesh == null) continue;
            string name = MaterialOf(n.Mesh);
            if (name.Length == 0) continue;
            bool known;
            lock (Gate) known = _bytes.ContainsKey(name);
            if (!known) Read(name);
        }
        LateTangents(model);
    }

    /// <summary><c>GetMaterialName</c>: <c>materialname</c>, or <c>texture0</c> when it is empty.</summary>
    private static string MaterialOf(MeshDef m) => m.MaterialName.Length > 0 ? m.MaterialName : m.Textures[0];

    /// <summary>The parse worker asked for a hint the cache could not answer, so <see cref="MdlPost.Tangents"/>
    /// skipped the mesh; the read above has the answer now. Re-runs that one step -- never the whole
    /// post-process, which would re-weld an already welded model -- BEFORE the part tree takes the mesh's
    /// arena range, which is the order both callers of <see cref="Warm"/> hold.</summary>
    private static void LateTangents(ModelDef model)
    {
        int late = 0;
        foreach (NodeDef n in model.Nodes)
        {
            MeshDef? m = n.Mesh;
            if (m == null || m.Tangents.Length != 0 || m.Positions.Length == 0) continue;
            string name = MaterialOf(m);
            if (name.Length == 0) continue;
            int hint;
            lock (Gate) if (!_hint.TryGetValue(name, out hint)) continue;
            if (hint is not (2 or 3)) continue;
            // A handedness split DUPLICATES vertices, and the skin, dangly and animmesh per-vertex arrays
            // follow it inside Tangents -- the same extra-vertex path the parse takes.
            MdlPost.Tangents(m, n);
            if (m.Tangents.Length != 0) late++;
        }
        if (late > 0)
            _log?.Info($"MtrFeed: '{model.Name}' -- {late} mesh(es) got their tangent frame from a late .mtr read");
    }

    private static void Read(string name)
    {
        byte[]? data = null;
        try
        {
            data = TexRegistry.ReadResourceBytes(name, MtrResType, out string? why);
            if (data == null && why != null)
            {
                _unreadable++;
                if (_unreadable <= 8) _log?.Always($"MtrFeed: '{name}'.mtr exists but was NOT READ -- {why}; its meshes draw their bitmap as a texture");
            }
            if (data is { Length: 0 }) data = null;
        }
        catch (Exception e)
        {
            _log?.Info($"MtrFeed: '{name}' faulted -- {e.Message}");
        }
        int hint = 0;
        if (data != null)
        {
            MtrDef m = MtrDef.Parse(data, name);
            hint = m.RenderHint;
            _loaded++;
            if (m.ShaderVs.Length > 0 && m.ShaderFs.Length > 0)
            {
                // Each half's native stage FIRST: once the plain halves are known, whether either has one is too, and the
                // pair's stock test (ContentPrograms.Register, on another thread) can trust a missing native text.
                ReadNative(m.ShaderVs); ReadNative(m.ShaderFs);
                ReadShader(m.ShaderVs); ReadShader(m.ShaderFs);
            }
        }
        else _absent++;
        lock (Gate)
        {
            _hint[name] = hint;
            _bytes[name] = data;
        }
        // A recipe interned before this read took its coverage mode and its declared-water term from a
        // material that was not there yet; this is the edge that lets the next reclassify re-ask them.
        if (data != null) DrawRecipe.MaterialLanded();
    }

    private static void ReadNative(string half)
    {
        if (StageResources.Fits(half)) ReadShader(StageResources.Prefix + half);
    }

    /// <summary>GAME thread: the text of a content shader a material names, once per name.</summary>
    private static void ReadShader(string name)
    {
        lock (Gate) if (_shd.ContainsKey(name)) return;
        string? text = null;
        try
        {
            byte[]? b = TexRegistry.ReadResourceBytes(name, ShdResType, out _);
            if (b is { Length: > 0 }) text = System.Text.Encoding.UTF8.GetString(b);
        }
        catch (Exception e)
        {
            _log?.Info($"MtrFeed: '{name}'.shd faulted -- {e.Message}");
        }
        lock (Gate) _shd[name] = text;
    }

    /// <summary>A content shader's text as the feed read it beside its material, or null when it has not (or there is no
    /// such file). Never reads.</summary>
    internal static string? ShaderText(string name)
    {
        lock (Gate) return _shd.TryGetValue(name, out string? t) ? t : null;
    }

    /// <summary>The world edge: a new area's materials may come from other containers.</summary>
    internal static void AreaEdge()
    {
        // The area edge is a load screen: drain whatever the area's models asked for, so the NEXT area (and
        // anything that re-parses this one) finds the cache warm instead of one model behind. Bounded, so a
        // hostile name list cannot hold the edge.
        Pump(MaxQueued);
        lock (Gate)
        {
            _want.Clear();
            _queued.Clear();
        }
    }

    /// <summary>The module edge: the next module's materials come from its own haks and NWSync manifest, so every
    /// remembered answer (a stock <c>.mtr</c>, or "absent") goes.</summary>
    internal static void ModuleEdge()
    {
        lock (Gate)
        {
            _hint.Clear();
            _bytes.Clear();
            _shd.Clear();
            _want.Clear();
            _queued.Clear();
        }
    }

    /// <summary>A period clause: what the cache holds and what it could not answer in time.</summary>
    internal static string Stats()
    {
        if (!_armed) return "";
        lock (Gate)
            return $"[mtr] {_hint.Count} known, {_loaded} read, {_absent} absent, {_misses} asked early"
                   + (_dropped != 0 ? $", {_dropped} dropped" : "") + (_unreadable != 0 ? $", {_unreadable} UNREADABLE" : "");
    }
}
