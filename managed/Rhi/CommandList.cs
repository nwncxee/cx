using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Rhi;

/// <summary>A recorded command's opcode.</summary>
public enum Op : byte
{
    End, BeginRenderPass, EndRenderPass, SetPipeline, SetViewport, SetScissor, SetDepthBias, BindSet,
    BindIndexBuffer, Draw, DrawIndexed, DrawIndexedIndirect, Dispatch, CopyImage, BlitImage, UpdateBuffer, ZeroBuffer,
    PushConstants, BindVertexBuffer, DrawIndexedMulti, SetDepthState, SetRasterState, SetDepthRange, ReadImages,
    DrawIndirect, CopyImageToBuffer, ClearImage, GenerateMips, BeginLabel, EndLabel, SetShadingRate,
}

/// <summary>An index element's width.</summary>
public enum IndexType : byte { U16, U32 }

/// <summary>A pixel rectangle.</summary>
public readonly record struct Rect(int X, int Y, int W, int H);

/// <summary>One texel origin in one mip; <see cref="Z"/> is the layer of an array or cube, the slice of a 3D image.</summary>
public readonly record struct ImageRegion(int Mip, int X, int Y, int Z);

/// <summary><see cref="Op.BeginRenderPass"/>'s header; the attachments follow it.</summary>
public readonly record struct RenderPassCmd(int Colors, bool HasDepth, DepthAttachment Depth, ImageId ShadingRate);
/// <summary><see cref="Op.SetShadingRate"/>: the pass's rate image REPLACEs the 1x1 pipeline rate when <see cref="Coarse"/>.</summary>
public readonly record struct ShadingRateCmd(bool Coarse);
/// <summary><see cref="Op.SetDepthBias"/>'s constants.</summary>
public readonly record struct DepthBiasCmd(float Factor, float Units);
/// <summary><see cref="Op.SetDepthRange"/>: the window-depth range the viewport maps clip z into.</summary>
public readonly record struct DepthRangeCmd(float Min, float Max);
/// <summary><see cref="Op.BindSet"/>'s header; the four binding arrays follow it in field order.</summary>
public readonly record struct BindSetCmd(int Set, int Uniforms, int Storage, int Sampled, int StorageImages);
/// <summary><see cref="Op.BindIndexBuffer"/>.</summary>
public readonly record struct IndexBufferCmd(BufferId Buffer, IndexType Type, long Offset);
/// <summary><see cref="Op.Draw"/>.</summary>
public readonly record struct DrawCmd(int Verts, int Insts, int FirstVert, int FirstInst);
/// <summary><see cref="Op.DrawIndexed"/>.</summary>
public readonly record struct DrawIndexedCmd(int Indices, int Insts, int FirstIndex, int BaseVertex, int FirstInst);
/// <summary><see cref="Op.DrawIndexedIndirect"/>: up to <see cref="MaxDraws"/> five-word commands, the real count
/// read from <see cref="Count"/> when it is not null.</summary>
public readonly record struct IndirectCmd(BufferId Commands, long Offset, int MaxDraws, BufferId Count, long CountOffset);
/// <summary><see cref="Op.DrawIndexedMulti"/>'s header: the index counts, first indices and base vertices follow,
/// <see cref="Draws"/> of each, in that order. The shader's draw index counts them from 0.</summary>
public readonly record struct MultiDrawCmd(int Draws);
/// <summary><see cref="Op.PushConstants"/>'s header; the bytes follow it.</summary>
public readonly record struct PushConstantsCmd(int Offset, int Bytes);
/// <summary><see cref="Op.BindVertexBuffer"/>: the buffer behind the bound layout's one stream.</summary>
public readonly record struct VertexBufferCmd(BufferId Buffer, long Offset);
/// <summary><see cref="Op.Dispatch"/>'s group counts.</summary>
public readonly record struct DispatchCmd(int X, int Y, int Z);
/// <summary><see cref="Op.CopyImage"/>: a W x H x D block, texel for texel.</summary>
public readonly record struct CopyImageCmd(ImageId Src, ImageRegion From, ImageId Dst, ImageRegion To, int W, int H, int D);
/// <summary><see cref="Op.BlitImage"/>: a scaled copy of mip 0, layer 0; also resolves a multisampled source.</summary>
public readonly record struct BlitImageCmd(ImageId Src, Rect From, ImageId Dst, Rect To, Filter Filter);
/// <summary><see cref="Op.UpdateBuffer"/>'s header; the bytes follow it.</summary>
public readonly record struct UpdateBufferCmd(BufferId Buffer, long Offset, int Bytes);
/// <summary><see cref="Op.ZeroBuffer"/>: <see cref="Bytes"/> zero bytes from <see cref="Offset"/>, 0 meaning to the end.</summary>
public readonly record struct ZeroBufferCmd(BufferId Buffer, long Offset, long Bytes);
/// <summary><see cref="Op.CopyImageToBuffer"/>: a <see cref="W"/> x <see cref="H"/> block of one mip and layer into a
/// buffer at <see cref="Offset"/>, tightly packed (block rows for a compressed format).</summary>
public readonly record struct CopyToBufferCmd(ImageId Src, ImageRegion From, int W, int H, BufferId Dst, long Offset);
/// <summary><see cref="Op.BeginLabel"/>'s header; the UTF-8 name follows it, <see cref="Bytes"/> long with its NUL.</summary>
public readonly record struct LabelCmd(int Bytes);
/// <summary><see cref="Op.ReadImages"/>'s header; the ids follow it. <see cref="Access"/> says which read the
/// hand-over is for: a sampled texture, or a storage image, which wants a layout of its own.</summary>
public readonly record struct ReadImagesCmd(int Images, Access Access);

/// <summary>CPU work waiting on a list's submit: <see cref="Fn"/> gets <see cref="Arg"/> and whether the GPU ran the
/// submit (false: it was refused, or the device went away first).</summary>
public readonly record struct WhenDoneCall(Action<int, bool> Fn, int Arg);

/// <summary>One pass's recorded commands: an 8-byte opcode word and a fixed record per command, spans copied in
/// after it, in one reused byte arena. Recording allocates nothing once the arena reaches the pass's high water;
/// a growth logs. With <see cref="Immediate"/> set, each command is submitted as it is recorded -- the A/B for a
/// migrated pass.</summary>
public sealed class CommandList
{
    /// <summary>Every record starts on this boundary, so a record read in place is aligned.</summary>
    private const int Align = 8;

    private byte[] _buf;
    private int _len, _sent;
    private readonly string _name;
    private readonly byte[] _nameZ;
    private readonly ExtenderLog? _log;
    private readonly List<WhenDoneCall> _whenDone = new();

    /// <summary>Submit every command as it is recorded, instead of when the pass submits the list.</summary>
    public IDevice? Immediate;

    /// <summary>Recorded for the async compute queue: dispatches, copies and buffer writes only, over images made
    /// <see cref="ImageUse.Async"/> and bound by slot (no bindless texture). A device with no such queue runs it on the
    /// graphics queue, in submit order.</summary>
    public bool Async { get; init; }

    /// <summary>An immediate submit was refused since the last <see cref="Reset"/>; later commands are not
    /// submitted, and the pass sits out.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Times the arena grew; each is one allocation and one log line.</summary>
    public int Growths { get; private set; }

    /// <summary>The list's name, as a log line says it.</summary>
    public string Name => _name;

    /// <summary>The list's name, UTF-8 and NUL-terminated: what a capture tool shows its submit under.</summary>
    public ReadOnlySpan<byte> NameUtf8 => _nameZ;

    /// <summary>Bytes recorded since the last <see cref="Reset"/>.</summary>
    public int Length => _len;

    public CommandList(string name, int bytes = 4096, ExtenderLog? log = null)
    {
        _name = name;
        _nameZ = Encoding.UTF8.GetBytes(name + "\0");
        _log = log;
        _buf = new byte[Math.Max(Align, bytes)];
    }

    /// <summary>Every command <paramref name="other"/> recorded since its reset, copied in after this list's own: a
    /// pass whose draws were recorded aside while the constants they read were gathered for the upload ahead of it.</summary>
    public void Append(CommandList other)
    {
        other.TakeWhenDone(_whenDone);
        if (other._len == 0) return;
        other._buf.AsSpan(0, other._len).CopyTo(Reserve(other._len));
        Sent();
    }

    /// <summary>Forget every recorded command; the arena stays. Work no submit took is told it never ran.</summary>
    public void Reset()
    {
        _len = 0; _sent = 0; Faulted = false;
        int n = _whenDone.Count;
        for (int i = 0; i < n; i++) _whenDone[i].Fn(_whenDone[i].Arg, false);
        _whenDone.RemoveRange(0, n);
    }

    /// <summary>Run <paramref name="fn"/>(<paramref name="arg"/>, ran) on the device's thread once the GPU has finished
    /// the next submit that takes this list, so everything recorded before it. Pass a cached delegate: this allocates
    /// nothing once the list's queue reaches its high water.</summary>
    public void WhenDone(Action<int, bool> fn, int arg) => _whenDone.Add(new WhenDoneCall(fn, arg));

    /// <summary>The work <see cref="WhenDone"/> queued since the last call, moved into <paramref name="into"/> for the
    /// submit that takes the list.</summary>
    public void TakeWhenDone(List<WhenDoneCall> into)
    {
        into.AddRange(_whenDone);
        _whenDone.Clear();
    }

    /// <summary>The commands recorded since the last call, for a device to execute. Each command is handed out once.</summary>
    public CommandReader TakePending()
    {
        var r = new CommandReader(_buf.AsSpan(_sent, _len - _sent));
        _sent = _len;
        return r;
    }

    public void BeginRenderPass(in RenderPassDesc d)
    {
        Head(Op.BeginRenderPass);
        Put(new RenderPassCmd(d.Colors.Length, d.HasDepth, d.Depth, d.ShadingRate));
        Put(d.Colors);
        Sent();
    }

    public void EndRenderPass() { Head(Op.EndRenderPass); Sent(); }

    /// <summary>The draws after it shade at the pass's <see cref="RenderPassDesc.ShadingRate"/> image's rate when
    /// <paramref name="coarse"/>, else at one invocation per pixel. A no-op on a pass without one, or a device without it.</summary>
    public void SetShadingRate(bool coarse) { Head(Op.SetShadingRate); Put(new ShadingRateCmd(coarse)); Sent(); }
    public void SetPipeline(PipelineId p) { Head(Op.SetPipeline); Put(p); Sent(); }
    public void SetViewport(int x, int y, int w, int h) { Head(Op.SetViewport); Put(new Rect(x, y, w, h)); Sent(); }
    public void SetScissor(int x, int y, int w, int h) { Head(Op.SetScissor); Put(new Rect(x, y, w, h)); Sent(); }
    public void SetDepthBias(float factor, float units) { Head(Op.SetDepthBias); Put(new DepthBiasCmd(factor, units)); Sent(); }

    /// <summary>The window-depth range the viewport maps clip z into, for the draws after it: a collapsed range
    /// (<c>1, 1</c>) is how a dome at the camera tests only where nothing has been drawn. Every render pass
    /// starts at <c>0, 1</c>, so one that narrows the range narrows it for itself alone.</summary>
    public void SetDepthRange(float min, float max) { Head(Op.SetDepthRange); Put(new DepthRangeCmd(min, max)); Sent(); }

    /// <summary>Bind <paramref name="b"/> as descriptor set <paramref name="set"/>; the spans are copied.</summary>
    public void BindSet(int set, in BindSet b)
    {
        Head(Op.BindSet);
        Put(new BindSetCmd(set, b.Uniforms.Length, b.Storage.Length, b.Sampled.Length, b.StorageImages.Length));
        Put(b.Uniforms); Put(b.Storage); Put(b.Sampled); Put(b.StorageImages);
        Sent();
    }

    /// <summary>The per-draw constant block, or <paramref name="offset"/> bytes into it, for every draw after it.
    /// Refused past <see cref="Rhi.PushConstants.Bytes"/>: larger per-draw data belongs in a buffer the draw indexes.</summary>
    public void PushConstants(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || (offset & 3) != 0 || (data.Length & 3) != 0 || data.Length > Rhi.PushConstants.Bytes - offset)
        {
            _log?.Always($"CommandList {_name}: REFUSED {data.Length} push-constant bytes at {offset}, past the "
                       + $"{Rhi.PushConstants.Bytes}-byte block or off a word -- the pass draws with stale constants");
            Faulted = true;
            return;
        }
        Head(Op.PushConstants);
        Put(new PushConstantsCmd(offset, data.Length));
        Put(data);
        Sent();
    }

    /// <summary>Depth test, write and compare for the draws after it, over the bound pipeline's own until the next
    /// <see cref="SetPipeline"/>: per-batch depth rules without a pipeline per rule.</summary>
    public void SetDepthState(DepthState d) { Head(Op.SetDepthState); Put(d); Sent(); }

    /// <summary>Culling, winding and the depth-bias switch for the draws after it, as <see cref="SetDepthState"/>.
    /// Depth clamp stays the pipeline's.</summary>
    public void SetRasterState(RasterState r) { Head(Op.SetRasterState); Put(r); Sent(); }

    /// <summary>The buffer behind the bound pipeline's vertex stream.</summary>
    public void BindVertexBuffer(BufferId b, long offset = 0) { Head(Op.BindVertexBuffer); Put(new VertexBufferCmd(b, offset)); Sent(); }

    public void BindIndexBuffer(BufferId b, IndexType t, long offset)
    { Head(Op.BindIndexBuffer); Put(new IndexBufferCmd(b, t, offset)); Sent(); }

    public void Draw(int verts, int insts = 1, int firstVert = 0, int firstInst = 0)
    { Head(Op.Draw); Put(new DrawCmd(verts, insts, firstVert, firstInst)); Sent(); }

    public void DrawIndexed(int indices, int insts = 1, int firstIndex = 0, int baseVertex = 0, int firstInst = 0)
    { Head(Op.DrawIndexed); Put(new DrawIndexedCmd(indices, insts, firstIndex, baseVertex, firstInst)); Sent(); }

    public void DrawIndexedIndirect(BufferId cmds, long offset, int maxDraws, BufferId count = default, long countOffset = 0)
    { Head(Op.DrawIndexedIndirect); Put(new IndirectCmd(cmds, offset, maxDraws, count, countOffset)); Sent(); }

    /// <summary>Up to <paramref name="maxDraws"/> non-indexed four-word commands (vertices, instances, first vertex, first
    /// instance) from <paramref name="cmds"/> at <paramref name="offset"/>, sixteen bytes apart.</summary>
    public void DrawIndirect(BufferId cmds, long offset, int maxDraws)
    { Head(Op.DrawIndirect); Put(new IndirectCmd(cmds, offset, maxDraws, default, 0)); Sent(); }

    /// <summary>One draw per element of the three parallel spans, in one command: the shape a batch of meshes out
    /// of one arena draws with. The spans are copied; a length mismatch is refused.</summary>
    public void DrawIndexedMulti(ReadOnlySpan<int> indices, ReadOnlySpan<int> firstIndices, ReadOnlySpan<int> baseVertices)
    {
        if (indices.Length != firstIndices.Length || indices.Length != baseVertices.Length)
        {
            _log?.Always($"CommandList {_name}: REFUSED a multi-draw of {indices.Length}/{firstIndices.Length}/"
                       + $"{baseVertices.Length} -- the three spans are one per draw");
            Faulted = true;
            return;
        }
        if (indices.IsEmpty) return;
        Head(Op.DrawIndexedMulti);
        Put(new MultiDrawCmd(indices.Length));
        Put(indices); Put(firstIndices); Put(baseVertices);
        Sent();
    }

    public void Dispatch(int x, int y = 1, int z = 1) { Head(Op.Dispatch); Put(new DispatchCmd(x, y, z)); Sent(); }

    /// <summary>Every image in <paramref name="images"/> is read as <paramref name="access"/> from here on: a pass
    /// that wrote one as a target hands it over outside any render pass, so a later bind of it needs no barrier
    /// where the backend cannot place one. A no-op where layouts are not a thing.</summary>
    public void ReadImages(ReadOnlySpan<ImageId> images, Access access = Access.SampledRead)
    {
        if (images.IsEmpty) return;
        Head(Op.ReadImages);
        Put(new ReadImagesCmd(images.Length, access));
        Put(images);
        Sent();
    }

    /// <summary>Open a region named <paramref name="utf8"/> that capture tools show the commands up to its
    /// <see cref="EndLabel"/> under. A device without debug labels skips both; one submit closes what it left open.</summary>
    public void BeginLabel(ReadOnlySpan<byte> utf8)
    {
        Head(Op.BeginLabel);
        Put(new LabelCmd(utf8.Length + 1));
        Span<byte> dst = Reserve(utf8.Length + 1);
        utf8.CopyTo(dst);
        dst[^1] = 0;
        Sent();
    }

    /// <summary>Close the innermost <see cref="BeginLabel"/>; one whose region began in an earlier submit is dropped.</summary>
    public void EndLabel() { Head(Op.EndLabel); Sent(); }

    /// <summary>Every mip and layer of <paramref name="i"/> set to zero, outside a pass; the image needs <c>CopyDst</c>.</summary>
    public void ClearImage(ImageId i) { Head(Op.ClearImage); Put(i); Sent(); }
    /// <summary><paramref name="i"/>'s mips past the base filled from it by linear blits, outside a pass; the image needs
    /// <c>CopySrc</c> and <c>CopyDst</c>.</summary>
    public void GenerateMips(ImageId i) { Head(Op.GenerateMips); Put(i); Sent(); }
    public void CopyImage(in CopyImageCmd c) { Head(Op.CopyImage); Put(c); Sent(); }
    public void BlitImage(in BlitImageCmd c) { Head(Op.BlitImage); Put(c); Sent(); }
    public void CopyImageToBuffer(in CopyToBufferCmd c) { Head(Op.CopyImageToBuffer); Put(c); Sent(); }

    /// <summary>Write <paramref name="src"/> into <paramref name="b"/> at <paramref name="offset"/>; the bytes are copied now.</summary>
    public void UpdateBuffer(BufferId b, long offset, ReadOnlySpan<byte> src)
    {
        Head(Op.UpdateBuffer);
        Put(new UpdateBufferCmd(b, offset, src.Length));
        Put(src);
        Sent();
    }

    /// <summary>Zero <paramref name="bytes"/> of <paramref name="b"/> from <paramref name="offset"/> on the GPU;
    /// 0 bytes zeroes to the end. Both are whole words.</summary>
    public void ZeroBuffer(BufferId b, long offset = 0, long bytes = 0)
    { Head(Op.ZeroBuffer); Put(new ZeroBufferCmd(b, offset, bytes)); Sent(); }

    private void Head(Op op) => Put((long)op);

    private void Put<T>(in T v) where T : unmanaged
    {
        Span<byte> dst = Reserve(Unsafe.SizeOf<T>());
        MemoryMarshal.Write(dst, in v);
    }

    private void Put<T>(ReadOnlySpan<T> v) where T : unmanaged
    {
        if (v.IsEmpty) return;
        MemoryMarshal.AsBytes(v).CopyTo(Reserve(v.Length * Unsafe.SizeOf<T>()));
    }

    private Span<byte> Reserve(int bytes)
    {
        int padded = (bytes + Align - 1) & ~(Align - 1);
        if (_len + padded > _buf.Length)
        {
            int size = Math.Max(_buf.Length * 2, _len + padded);
            Array.Resize(ref _buf, size);
            Growths++;
            _log?.Always($"CommandList {_name}: arena grew to {size} bytes -- one allocation, size the list for its high water");
        }
        Span<byte> s = _buf.AsSpan(_len, bytes);
        _len += padded;
        return s;
    }

    private void Sent()
    {
        if (Immediate != null && !Faulted) Faulted = !Immediate.Submit(this);
    }
}

/// <summary>Walks a recorded span in order: <see cref="Next"/> yields the opcode, then the device reads that
/// command's record and spans with <see cref="Read{T}"/> and <see cref="Span{T}"/>, exactly as recorded.</summary>
public ref struct CommandReader
{
    private const int Align = 8;
    private readonly ReadOnlySpan<byte> _b;
    private int _at;

    public CommandReader(ReadOnlySpan<byte> b) { _b = b; _at = 0; }

    /// <summary>The next opcode, or false at the end.</summary>
    public bool Next(out Op op)
    {
        if (_at >= _b.Length) { op = Op.End; return false; }
        op = (Op)Read<long>();
        return true;
    }

    public T Read<T>() where T : unmanaged
    {
        T v = MemoryMarshal.Read<T>(_b.Slice(_at, Unsafe.SizeOf<T>()));
        Skip(Unsafe.SizeOf<T>());
        return v;
    }

    /// <summary><paramref name="n"/> records in place, pointing into the arena.</summary>
    public ReadOnlySpan<T> Span<T>(int n) where T : unmanaged
    {
        if (n == 0) return default;
        int bytes = n * Unsafe.SizeOf<T>();
        ReadOnlySpan<T> s = MemoryMarshal.Cast<byte, T>(_b.Slice(_at, bytes));
        Skip(bytes);
        return s;
    }

    private void Skip(int bytes) => _at += (bytes + Align - 1) & ~(Align - 1);
}
