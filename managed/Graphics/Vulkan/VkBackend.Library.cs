using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using CX.Framework;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// Graphics pipeline libraries: each pipeline's four parts are built once per stage, vertex input and target set and
/// linked on the frame unoptimised, which costs a millisecond or two; a worker thread then links the same libraries
/// optimised, and the result takes over at the head of the next graphics submit, which is where the backend already
/// learns what finished. A device without the extension builds whole pipelines on the frame.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>One optimised link to make: the shared pipeline it replaces, a copy of the desc, and the four libraries.</summary>
    private sealed class LinkJob(PipeObj obj, PipelineDesc desc, bool rate, bool dyn, VkPipeline[] libs, string what, int gen)
    {
        public readonly PipeObj Obj = obj;
        /// <summary>The specialization generation the libraries were built at; a result from an older one is dropped.</summary>
        public readonly int Gen = gen;
        public readonly PipelineDesc Desc = desc;
        public readonly bool Rate = rate, Dyn = dyn;
        public readonly VkPipeline[] Libs = libs;
        public readonly string What = what;
        public VkPipeline Result;
    }

    /// <summary>The libraries by part key, the keys each stage's libraries sit under, the libraries a destroyed stage
    /// left until no link can still read them, and the worker's two queues.</summary>
    private sealed class LibraryState
    {
        public readonly Dictionary<string, VkPipeline> Libs = new();
        public readonly Dictionary<uint, List<string>> ByShader = new();
        public readonly List<VkPipeline> Grave = new();
        public readonly ConcurrentQueue<LinkJob> Jobs = new(), Done = new();
        public readonly AutoResetEvent Wake = new(false);
        /// <summary>Jobs queued and not yet swapped in, counted on the frame thread.</summary>
        public int InFlight;
        public Thread? Worker;
        public volatile bool Stop;
    }

    private readonly LibraryState _lib = new();

    /// <summary>The device offers graphics pipeline libraries with fast linking; and whether the backend uses them, which
    /// a refused library turns off for the session.</summary>
    private readonly bool _canLibraries;
    private bool _librariesOn = true;

    /// <summary>First-use pipelines are linked from libraries, the optimised one built on the worker.</summary>
    internal bool Libraries { get => _canLibraries && _librariesOn; set => _librariesOn = value; }

    /// <summary>The open line's words for the pipeline path the device took.</summary>
    internal static string PipelinePathLine(bool libraries)
        => libraries ? "first-use pipelines are linked from libraries" : "pipelines are built whole";

    /// <summary><paramref name="d"/>'s pipeline linked unoptimised from its four libraries, each built at its first ask.</summary>
    private VkPipeline Linked(in PipelineDesc d, bool rate, bool dyn, string what)
    {
        VkPipeline* libs = stackalloc VkPipeline[4];
        if (!LibrariesOf(d, rate, dyn, what, libs)) return default;
        return Build(d, rate, dyn, Part.None, libs, 4, what);
    }

    /// <summary>The four libraries <paramref name="d"/> links from, into <paramref name="libs"/>; false (logged) when one
    /// was refused.</summary>
    private bool LibrariesOf(in PipelineDesc d, bool rate, bool dyn, string what, VkPipeline* libs)
    {
        (string vi, string pr, string fs, string fo) = PartKeys(d, rate, dyn);
        libs[0] = Library(d, rate, dyn, Part.VertexInput, vi, what, default);
        libs[1] = Library(d, rate, dyn, Part.PreRaster, pr, what, d.Vs, d.Gs);
        libs[2] = Library(d, rate, dyn, Part.Fragment, fs, what, d.Fs);
        libs[3] = Library(d, rate, dyn, Part.Output, fo, what, default);
        return libs[0].IsNotNull && libs[1].IsNotNull && libs[2].IsNotNull && libs[3].IsNotNull;
    }

    /// <summary>The <paramref name="part"/> library under <paramref name="key"/>, built at its first ask and filed
    /// under the stages it holds, which take it with them when they go.</summary>
    private VkPipeline Library(in PipelineDesc d, bool rate, bool dyn, Part part, string key, string what, ShaderId a, ShaderId b = default)
    {
        LibraryState l = _lib;
        if (l.Libs.TryGetValue(key, out VkPipeline lib)) return lib;
        lib = Build(d, rate, dyn, part, null, 0, $"{what} ({part} library)");
        if (lib.IsNull) return lib;
        l.Libs[key] = lib;
        foreach (ShaderId s in (ReadOnlySpan<ShaderId>)[a, b])
        {
            if (s.IsNull) continue;
            if (!l.ByShader.TryGetValue(s.Value, out List<string>? keys)) l.ByShader[s.Value] = keys = new List<string>();
            keys.Add(key);
        }
        return lib;
    }

    /// <summary>Hand the worker <paramref name="obj"/>'s optimised link, starting the worker at the first.</summary>
    private void QueueOptimised(PipeObj obj, in PipelineDesc d, bool rate, bool dyn, string what)
    {
        LibraryState l = _lib;
        VkPipeline* libs = stackalloc VkPipeline[4];
        if (!LibrariesOf(d, rate, dyn, what, libs)) return;
        l.Jobs.Enqueue(new LinkJob(obj, d, rate, dyn, [libs[0], libs[1], libs[2], libs[3]], what, _specGen));
        l.InFlight++;
        if (l.Worker == null)
        {
            l.Worker = new Thread(LinkWorker) { IsBackground = true, Name = "cx-pipelines" };
            l.Worker.Start();
        }
        l.Wake.Set();
    }

    /// <summary>The worker: every queued job's optimised link, onto the done queue, until the backend goes.</summary>
    private void LinkWorker()
    {
        Affinity.ClearThis();   // created by the (possibly pinned) main thread; the links must not share its core
        LibraryState l = _lib;
        while (!l.Stop)
        {
            while (!l.Stop && l.Jobs.TryDequeue(out LinkJob? job))
            {
                fixed (VkPipeline* libs = job.Libs) job.Result = Build(job.Desc, job.Rate, job.Dyn, Part.Whole, libs, 4, job.What);
                l.Done.Enqueue(job);
            }
            l.Wake.WaitOne();
        }
    }

    /// <summary>Every optimised pipeline the worker finished takes over from its linked one, which goes once no submit
    /// in flight can reach it; with no link left in flight, the libraries destroyed stages left go too. At the head of
    /// every graphics submit.</summary>
    internal void SwapOptimised()
    {
        if (!OnThread("SwapOptimised")) return;
        LibraryState l = _lib;
        while (l.Done.TryDequeue(out LinkJob? job))
        {
            l.InFlight--;
            if (job.Result.IsNull) continue;
            PipeObj o = job.Obj;
            VkPipeline old = o.Handle, fresh = job.Result;
            // a link made before the view turned over carries the specialization of the pipeline Respecialize replaced
            if (o.Dead || job.Gen != _specGen) { Later(() => _vkd.vkDestroyPipeline(fresh)); continue; }
            o.Handle = fresh;
            o.Optimised = true;
            _pipeCacheDirty = true;
            Name(VkObjectType.Pipeline, fresh.Handle, job.What + " (optimised)");
            Later(() => _vkd.vkDestroyPipeline(old));
        }
        if (l.InFlight == 0 && l.Grave.Count > 0)
        {
            VkPipeline[] dead = l.Grave.ToArray();
            l.Grave.Clear();
            Later(() => { foreach (VkPipeline p in dead) _vkd.vkDestroyPipeline(p); });
        }
    }

    /// <summary>The worker's optimised pipeline is the one <paramref name="p"/> draws with.</summary>
    internal bool Optimised(PipelineId p) => _pipelines.TryGet(p.Value, out VkPipe v) && v.Obj.Optimised;

    /// <summary>The libraries holding stage <paramref name="s"/>, to the grave: a link in flight may still read them.</summary>
    private void DropLibraries(ShaderId s)
    {
        LibraryState l = _lib;
        if (!l.ByShader.Remove(s.Value, out List<string>? keys)) return;
        foreach (string k in keys)
            if (l.Libs.Remove(k, out VkPipeline lib)) l.Grave.Add(lib);
    }

    /// <summary>Stop the worker and free every library, after the device is idle.</summary>
    private void DisposeLibraries()
    {
        LibraryState l = _lib;
        l.Stop = true;
        l.Wake.Set();
        l.Worker?.Join();
        while (l.Done.TryDequeue(out LinkJob? job))
            if (job.Result.IsNotNull) _vkd.vkDestroyPipeline(job.Result);
        foreach (VkPipeline p in l.Libs.Values) _vkd.vkDestroyPipeline(p);
        foreach (VkPipeline p in l.Grave) _vkd.vkDestroyPipeline(p);
        l.Libs.Clear();
        l.Grave.Clear();
        l.Wake.Dispose();
    }
}
