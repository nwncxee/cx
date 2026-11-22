using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Residency goldens (approved-spec): on a device capped through VMA's heap limit, textures and meshes evict and
/// retry instead of landing in system RAM, long-lived images spill and move back, and refusals say so once.
/// Oracles: hand-written caps, sizes in MiB, the words cx.log must hold, and the heap each allocation sits on.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The texture examples' cap, alloy.vramcap's value in their Given.</summary>
    private const int TextureCapMiB = 3072;

    /// <summary>The spill examples' cap: small enough that a few hundred MiB of filler reaches it.</summary>
    private const int SpillCapMiB = 512;

    /// <summary>One idle texture: 2048x2048 RGBA8 with no mips, 16 MiB by hand.</summary>
    private const int IdleSide = 2048;
    private const long IdleBytes = 16 * MiB;

    /// <summary>A texture that cannot fit: 4096x4096 RGBA8 with no mips, 64 MiB by hand.</summary>
    private const int BigSide = 4096;

    /// <summary>The frames between two move-back tries, by hand: the frame line's period.</summary>
    private const int Period = 300;

    /// <summary>Vertices a mesh grow places: past 2^18, so the static mirror doubles to 2^19 vertices of 48 bytes,
    /// 24 MiB by hand.</summary>
    private const int GrowVerts = 300_000;
    private const int GrowMiB = 24;
    private const int StaticFloats = 12;

    private static readonly ushort[] ResidencyTri = [0, 1, 2];

    /// <summary>Every residency golden, each group on a capped device of its own, made and disposed here.</summary>
    private static void ResidencyApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        UnifiedHeapNothingSpills(vk, log);
        vk.DeviceLocalBytes(out _, out _, out long heap, out _);
        if (heap < (TextureCapMiB + 1024) * MiB)
        {
            new Checker(log, "GpuGolden.Residency.SelfCheck").Skip(
                $"the texture examples cap a {TextureCapMiB}MiB heap and this device's is {heap / MiB}MiB");
            return;
        }
        TextureExamples(check, log);
        SpillExamples(check, log);
    }

    /// <summary>A capped device whose log lands in <paramref name="heard"/>; null (failed) when it is refused.</summary>
    private static VkBackend? Capped(Checker check, List<string> heard, int capMib, string why)
    {
        VkBackend? dev = VkBackend.TryCreate(new ExtenderLog { Tap = heard.Add }, capMib: capMib);
        check.Expect(dev != null, $"{why}: a device capped at {capMib}MiB was refused");
        return dev;
    }

    /// <summary>One harness frame: a submit, the frame's end, and the move-back check every frame runs.</summary>
    private static void ResidencyFrame(VkBackend vk, ExtenderLog log)
    {
        vk.Submit(new CommandList("golden residency frame", 1 << 8, log));
        vk.EndAliasFrame();
        vk.MoveBackCheck();
    }

    /// <summary>A buffer that takes the capped heap's room but <paramref name="leave"/> bytes; null when none is left.</summary>
    private static BufferId Filler(VkBackend vk, int capMib, long leave, string label)
    {
        long room = capMib * MiB - vk.DeviceBlockBytes() - leave;
        return room <= 0 ? default : vk.CreateBuffer(new BufferDesc(room, BufferUse.Vertex, MemoryKind.Device, label));
    }

    private static readonly SamplerDesc ResidencySampler = new(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp);

    /// <summary>An owned texture through the mirror's own path, as an area load makes one.</summary>
    private static ImageId OwnedTexture(VkBackend vk, uint gl, int side, string label)
        => VkTextures.Create(vk, gl, Format.Rgba8, side, side, 1, false, ResidencySampler, label);

    private static int Count(List<string> lines, params string[] words)
    {
        int n = 0;
        foreach (string l in lines)
        {
            bool all = true;
            foreach (string w in words) all &= l.Contains(w, StringComparison.Ordinal);
            if (all) n++;
        }
        return n;
    }

    private static int Evicted(List<uint> names)
    {
        int n = 0;
        foreach (uint gl in names) if (!VkTextures.Resident(gl)) n++;
        return n;
    }

    /// <summary>A static mesh of <paramref name="nv"/> equal vertices under content key <paramref name="key"/>.</summary>
    private static int ResidencyMesh(ulong key, int nv)
        => MeshArena.AcquireRaw(key, MeshArena.Fmt.Static, nv, new float[nv * StaticFloats], ResidencyTri, 3,
                                null, 0, null, 0, Vector3.Zero, Vector3.One);

    /// <summary>E1, E2, E10, E11 and E3/E4 on a device capped at alloy.vramcap's 3072.</summary>
    private static void TextureExamples(Checker check, ExtenderLog log)
    {
        var heard = new List<string>();
        VkBackend? dev = Capped(check, heard, TextureCapMiB, "E1");
        if (dev == null) return;
        PriorityOn(dev, heard, log);
        PriorityAbsent(dev, heard, log);
        ExtenderLog? knobs = KnobGroup.Log;
        ExtenderLog? arenaLog = MeshArena.Log;
        var ear = new ExtenderLog { Tap = heard.Add };
        KnobGroup.Log = ear;
        MeshArena.Log = ear;
        VkTextures.DeviceForCheck(dev);
        var names = new List<uint>();
        BufferId rb = dev.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden residency read"));
        try
        {
            IdleEvicted(dev, check, log, names);
            foreach (uint gl in names) VkTextures.Forget(gl);
            names.Clear();
            Settle(dev, log, rb);
            BufferId fill = Filler(dev, TextureCapMiB, 8 * MiB, "golden residency texture filler");
            check.Expect(!fill.IsNull, "E2: the filler that leaves 8MiB of room was refused");
            uint refused = RefusedOnce(dev, check, heard);
            EachNamedOnce(dev, check, heard);
            dev.Destroy(fill);
            Settle(dev, log, rb);
            OwnImageBack(dev, check, log, refused);
        }
        finally
        {
            foreach (uint gl in names) VkTextures.Forget(gl);
            VkTextures.DeviceForCheck(null);
            MeshArena.VkForgetForCheck();
            MeshArena.ResetForCheck();
            KnobGroup.Log = knobs;
            MeshArena.Log = arenaLog;
            dev.Destroy(rb);
            dev.Dispose();
        }
    }

    // Given a driver with VK_EXT_memory_priority
    // When the device is created
    // Then cx.log says priorities are on: render targets, shadows and the mesh arena high, textures low
    /// <summary>E3: the creation lines of a device whose driver offers the extension. Oracle: the words, by hand.</summary>
    private static void PriorityOn(VkBackend dev, List<string> heard, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.PriorityOn.SelfCheck");
        if (!dev.Offers("VK_EXT_memory_priority\0"u8))
        {
            check.Skip("this host's driver does not offer VK_EXT_memory_priority");
            return;
        }
        int on = Count(heard, "VK_EXT_memory_priority", "render targets", "shadows", "mesh arena", "high", "textures", "low");
        check.Expect(on == 1, $"E3: {on} creation lines say priorities are on for render targets, shadows and the mesh arena high and textures low, not 1");
    }

    // Given a driver without it
    // When the device is created
    // Then cx.log says once that it is absent, and nothing else changes
    /// <summary>E4: the creation lines of a device whose driver lacks the extension. Oracle: the words, by hand,
    /// and a target that still clears to a hand-written colour.</summary>
    private static void PriorityAbsent(VkBackend dev, List<string> heard, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.PriorityAbsent.SelfCheck");
        if (dev.Offers("VK_EXT_memory_priority\0"u8))
        {
            check.Skip("this host's driver offers VK_EXT_memory_priority, so the device that lacks it is not here");
            return;
        }
        int absent = Count(heard, "VK_EXT_memory_priority", "absent");
        check.Expect(absent == 1, $"E4: {absent} creation lines say VK_EXT_memory_priority is absent, not 1");
        BufferId rb = dev.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden residency absent read"));
        ImageId t = Target(dev, 4, "golden residency absent target", null);
        byte[]? got = ClearRead(dev, log, t, new Vector4(0, 1, 0, 1), rb);
        check.Expect(got != null && Solid(got, 0, 255, 0, 255), "E4: a target on the device without priorities does not clear to green");
        dev.Destroy(t);
        dev.Destroy(rb);
    }

    // Given alloy.vramcap 3072 and a session whose textures would pass it
    // When a texture or mesh allocation does not fit the budget
    // Then it is not placed in system RAM: idle textures are evicted and the allocation is tried again
    /// <summary>E1: idle 16 MiB textures fill the 3072 MiB cap, then one more texture and a 24 MiB mesh grow each
    /// land in device memory by evicting idle ones. Oracle: the cap and sizes by hand, and each allocation's heap.</summary>
    private static void IdleEvicted(VkBackend dev, Checker check, ExtenderLog log, List<uint> names)
    {
        bool arena = MeshArena.ResetForCheck();
        check.Expect(arena, "E1: the arena holds live meshes, so the grow half cannot run");
        int small = arena ? ResidencyMesh(6391, 1000) : 0;
        if (arena) MeshArena.VkPumpForCheck(dev);
        long cap = TextureCapMiB * MiB;
        for (int i = 0; i < TextureCapMiB / 16 + 2; i++)
        {
            dev.DeviceLocalBytes(out _, out _, out _, out long held);
            if (held + IdleBytes > cap) break;
            uint gl = VkTextures.NewName();
            names.Add(gl);
            if (OwnedTexture(dev, gl, IdleSide, $"golden residency idle {i}").IsNull) break;
        }
        check.Expect(Evicted(names) == 0, $"E1: {Evicted(names)} idle textures were evicted before the cap was reached");
        uint late = VkTextures.NewName();
        ImageId one = OwnedTexture(dev, late, IdleSide, "golden residency one more");
        names.Add(late);
        check.Expect(!one.IsNull, "E1: the texture past the cap was refused, not given room by an eviction");
        check.Expect(one.IsNull || !dev.InSystemRam(one), "E1: the texture past the cap was placed in system RAM");
        int afterTexture = Evicted(names);
        check.Expect(afterTexture >= 1, "E1: the texture past the cap fitted without an idle texture being evicted");
        if (!arena) return;
        int big = ResidencyMesh(6392, GrowVerts);
        MeshArena.VkPumpForCheck(dev);
        BufferId vbo = MeshArena.VkVbo(MeshArena.Fmt.Static);
        check.Expect(!vbo.IsNull, $"E1: the {GrowMiB}MiB mesh grow past the cap was refused, not given room by an eviction");
        check.Expect(vbo.IsNull || !dev.InSystemRam(vbo), $"E1: the {GrowMiB}MiB mesh grow was placed in system RAM");
        check.Expect(Evicted(names) > afterTexture, "E1: the mesh grow past the cap fitted without an idle texture being evicted");
        MeshArena.Release(small);
        MeshArena.Release(big);
        MeshArena.FrameEdgeForCheck();
    }

    // Given that retry still does not fit
    // When the allocation is refused
    // Then the texture draws its fallback for now, and cx.log names the texture and its size once
    /// <summary>E2: a 64 MiB texture with 8 MiB of room and nothing idle to evict. Oracle: the null index the
    /// fallback draws with, and the name and "64 MiB" by hand.</summary>
    private static uint RefusedOnce(VkBackend dev, Checker check, List<string> heard)
    {
        uint gl = VkTextures.NewName();
        ImageId img = OwnedTexture(dev, gl, BigSide, "golden residency refused");
        check.Expect(img.IsNull, "E2: a 64MiB texture was made with 8MiB of room and nothing idle to evict");
        check.Expect(VkTextures.Index((int)gl) == 0, "E2: the refused texture does not draw with the fallback's null index");
        int named = Count(heard, "VkTextures", "golden residency refused", "64 MiB");
        check.Expect(named == 1, $"E2: cx.log names the refused texture with its 64 MiB {named} times, not once");
        return gl;
    }

    // Given more than eight textures are refused
    // When each is refused
    // Then each is named once with its MiB, and a repeat refusal adds no line
    /// <summary>E10: ten 64 MiB textures refused, then the first refused again. Oracle: ten names and "64 MiB" by hand.</summary>
    private static void EachNamedOnce(VkBackend dev, Checker check, List<string> heard)
    {
        const int Refusals = 10;
        var gls = new uint[Refusals];
        for (int i = 0; i < Refusals; i++)
        {
            gls[i] = VkTextures.NewName();
            OwnedTexture(dev, gls[i], BigSide, $"golden residency many {(char)('a' + i)}");
        }
        for (int i = 0; i < Refusals; i++)
        {
            int named = Count(heard, "VkTextures", $"golden residency many {(char)('a' + i)}", "64 MiB");
            check.Expect(named == 1, $"E10: refusal {i + 1} of {Refusals} is named with its 64 MiB {named} times, not once");
        }
        int lines = heard.Count;
        OwnedTexture(dev, gls[0], BigSide, "golden residency many a");
        check.Expect(heard.Count == lines, $"E10: the repeat refusal of the first texture added {heard.Count - lines} lines, not 0");
        foreach (uint gl in gls) VkTextures.Forget(gl);
    }

    // Given a texture drew its fallback, and the budget later has room
    // When it is next drawn
    // Then it draws its own image
    /// <summary>E11: E2's refused texture once its filler is gone, asked for by the draw's index lookup after a
    /// period. Oracle: the null index before, a non-null one after.</summary>
    private static void OwnImageBack(VkBackend dev, Checker check, ExtenderLog log, uint gl)
    {
        check.Expect(VkTextures.Index((int)gl) == 0, "E11: precondition: the texture never drew its fallback");
        for (int f = 0; f <= Period; f++) ResidencyFrame(dev, log);
        check.Expect(VkTextures.Index((int)gl) != 0, "E11: with room back, the texture still draws the fallback, not its own image");
        VkTextures.Forget(gl);
    }

    /// <summary>E13, E5 to E9 on a device capped at 512 MiB.</summary>
    private static void SpillExamples(Checker check, ExtenderLog log)
    {
        var heard = new List<string>();
        VkBackend? dev = Capped(check, heard, SpillCapMiB, "E5");
        if (dev == null) return;
        ExtenderLog? casterLog = CasterPass.VkLogForCheck(new ExtenderLog { Tap = heard.Add });
        ExtenderLog? knobs = KnobGroup.Log;
        ExtenderLog? arenaLog = MeshArena.Log;
        var ear = new ExtenderLog { Tap = heard.Add };
        KnobGroup.Log = ear;
        MeshArena.Log = ear;
        VkTextures.DeviceForCheck(dev);
        BufferId rb = dev.CreateBuffer(new BufferDesc(64, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden spill read"));
        try
        {
            Settle(dev, log, rb);
            MoveBackAllocatesNothing(dev, check, log);
            Spilled s = SpillAndDraw(dev, check, log, rb);
            BackInDevice(dev, check, log, rb, s);
            MoveBackRefused(dev, check, log, rb);
            NeitherHeap(dev, check, log, rb, heard);
            GrowRefused(dev, check, log, rb, heard);
        }
        finally
        {
            CasterPass.VkResetForCheck();
            CasterPass.VkLogForCheck(casterLog);
            VkTextures.DeviceForCheck(null);
            MeshArena.VkForgetForCheck();
            MeshArena.ResetForCheck();
            KnobGroup.Log = knobs;
            MeshArena.Log = arenaLog;
            dev.Destroy(rb);
            dev.Dispose();
        }
    }

    // Given nothing is in system RAM
    // When frames run
    // Then the move-back check allocates nothing
    /// <summary>E13: two periods of frames with nothing spilled. Oracle: zero managed bytes allocated by the check,
    /// and one check per frame counted, so a check that never ran cannot pass.</summary>
    private static void MoveBackAllocatesNothing(VkBackend dev, Checker check, ExtenderLog log)
    {
        long checks = dev.MoveBackChecks, bytes = 0;
        for (int f = 0; f < 2 * Period; f++)
        {
            dev.Submit(new CommandList("golden residency quiet", 1 << 8, log));
            dev.EndAliasFrame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            dev.MoveBackCheck();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        check.Expect(dev.MoveBackChecks - checks == 2 * Period, $"E13: {dev.MoveBackChecks - checks} move-back checks ran over {2 * Period} frames, not one a frame");
        check.Expect(bytes == 0, $"E13: the move-back check allocated {bytes} bytes over {2 * Period} frames with nothing in system RAM");
    }

    /// <summary>What E5 spilled, for E6 to watch move back.</summary>
    private sealed record Spilled(BufferId Fill, ImageId Target, ImageId Alias, ImageId Shadow, uint Idle);

    /// <summary>The shadow array the spill examples ask the caster bake for: 1024 square, 4 layers, 16 MiB by hand.</summary>
    private const int ShadowSide = 1024, ShadowLayers = 4;

    // Given a render target, shadow array, alias group, lightmap pool or TAA history that does not fit after idle textures are evicted
    // When it is allocated
    // Then it lands in system RAM and keeps drawing
    /// <summary>E5: an idle texture, then filler to the cap, then a target, an alias member and the caster's sun
    /// array. Oracle: the heap each sits on, and a hand-written clear colour read back.</summary>
    private static Spilled SpillAndDraw(VkBackend dev, Checker check, ExtenderLog log, BufferId rb)
    {
        uint idle = VkTextures.NewName();
        OwnedTexture(dev, idle, IdleSide, "golden spill idle");
        BufferId fill = Filler(dev, SpillCapMiB, 0, "golden spill filler");
        check.Expect(!fill.IsNull, "E5: the filler that takes the cap's room was refused");
        ImageId target = Target(dev, 4, "golden spill target", null);
        ImageId alias = Target(dev, 4, "golden spill alias", "golden spill lane");
        ImageId shadow = CasterPass.VkSunArrayForCheck(dev, ShadowSide, ShadowLayers);
        check.Expect(!VkTextures.Resident(idle), "E5: the idle texture was not evicted before the long-lived images spilled");
        check.Expect(!target.IsNull && dev.InSystemRam(target), "E5: the render target past the cap is not in system RAM");
        check.Expect(!alias.IsNull && dev.InSystemRam(alias), "E5: the alias group past the cap is not in system RAM");
        check.Expect(!shadow.IsNull && dev.InSystemRam(shadow), "E5: the shadow array past the cap is not in system RAM");
        check.Expect(!CasterPass.BakeStoodDown, "E5: the caster bake stood down over a shadow array that spilled");
        byte[]? t = target.IsNull ? null : ClearRead(dev, log, target, new Vector4(1, 0, 0, 1), rb);
        check.Expect(t != null && Solid(t, 255, 0, 0, 255), "E5: the spilled render target does not draw red");
        byte[]? a = alias.IsNull ? null : ClearRead(dev, log, alias, new Vector4(0, 0, 1, 1), rb);
        check.Expect(a != null && Solid(a, 0, 0, 255, 255), "E5: the spilled alias member does not draw blue");
        return new Spilled(fill, target, alias, shadow, idle);
    }

    // Given an image in system RAM, and use falls under 80% of the budget
    // When the next period starts
    // Then it is back in device memory: an alias group at its frame boundary, a shadow array remade and rebaked with no empty frame, TAA history started over, the lightmap pool's pages relanded from the disk cache
    /// <summary>E6: E5's filler goes, a period and one frame run with the caster asking for its array every frame.
    /// Oracle: the heap each sits on, a hand-written clear colour, and no frame without a shadow array.</summary>
    private static void BackInDevice(VkBackend dev, Checker check, ExtenderLog log, BufferId rb, Spilled s)
    {
        bool wasOut = !s.Target.IsNull && dev.InSystemRam(s.Target) && !s.Alias.IsNull && dev.InSystemRam(s.Alias)
                      && !s.Shadow.IsNull && dev.InSystemRam(s.Shadow);
        check.Expect(wasOut, "E6: precondition: E5 left no image in system RAM to move back");
        dev.Destroy(s.Fill);
        int empty = 0;
        ImageId shadow = s.Shadow;
        for (int f = 0; f <= Period; f++)
        {
            ResidencyFrame(dev, log);
            shadow = CasterPass.VkSunArrayForCheck(dev, ShadowSide, ShadowLayers);
            if (shadow.IsNull || CasterPass.VkCascadeMap.IsNull) empty++;
        }
        check.Expect(!s.Target.IsNull && !dev.InSystemRam(s.Target), "E6: the render target is still in system RAM a period after room returned");
        check.Expect(!s.Alias.IsNull && !dev.InSystemRam(s.Alias), "E6: the alias group is still in system RAM a period after room returned");
        check.Expect(!shadow.IsNull && !dev.InSystemRam(shadow), "E6: the shadow array is still in system RAM a period after room returned");
        check.Expect(empty == 0, $"E6: {empty} frames had no shadow array while it moved back");
        byte[]? a = s.Alias.IsNull ? null : ClearRead(dev, log, s.Alias, new Vector4(0, 1, 0, 1), rb);
        check.Expect(a != null && Solid(a, 0, 255, 0, 255), "E6: the alias member moved back does not draw green");
        dev.Destroy(s.Target);
        dev.Destroy(s.Alias);
        VkTextures.Forget(s.Idle);
        CasterPass.VkResetForCheck();
        Settle(dev, log, rb);
    }

    /// <summary>The move-back-refused example's shadow array: 4096 square, 2 layers of D32F, 128 MiB by hand.</summary>
    private const int MoveSide = 4096, MoveLayers = 2;

    /// <summary>What the move-back-refused example holds after room returns: 400 MiB, under 80% of 512 (409.6) yet
    /// leaving 112 MiB, less than the array's 128.</summary>
    private const int HeldAfterMiB = 400;

    // Given that move back is refused
    // When it is tried
    // Then the old copy keeps drawing from system RAM, no pass stands down, and the next try waits a period
    /// <summary>E7: a 128 MiB shadow array spills, then use falls to 400 of 512 MiB, leaving too little room for it.
    /// Oracle: the sizes by hand, the heap it sits on, and at most one try in each of two periods.</summary>
    private static void MoveBackRefused(VkBackend dev, Checker check, ExtenderLog log, BufferId rb)
    {
        BufferId fill = Filler(dev, SpillCapMiB, 0, "golden move filler");
        ImageId shadow = CasterPass.VkSunArrayForCheck(dev, MoveSide, MoveLayers);
        check.Expect(!shadow.IsNull && dev.InSystemRam(shadow), "E7: precondition: the 128MiB shadow array did not spill to system RAM");
        dev.Destroy(fill);
        Settle(dev, log, rb);
        BufferId held = Filler(dev, SpillCapMiB, (SpillCapMiB - HeldAfterMiB) * MiB, "golden move held");
        int tries = dev.MoveBackTries;
        bool same = true;
        for (int f = 0; f < 2 * Period; f++)
        {
            ResidencyFrame(dev, log);
            same &= CasterPass.VkSunArrayForCheck(dev, MoveSide, MoveLayers) == shadow;
        }
        int tried = dev.MoveBackTries - tries;
        check.Expect(tried >= 1 && tried <= 2, $"E7: {tried} move-back tries over two periods, not one a period");
        check.Expect(same && !shadow.IsNull && dev.InSystemRam(shadow), "E7: the shadow array is no longer the old copy in system RAM after a refused move back");
        check.Expect(!CasterPass.BakeStoodDown, "E7: the caster bake stood down over a refused move back");
        dev.Destroy(held);
        CasterPass.VkResetForCheck();
        Settle(dev, log, rb);
    }

    /// <summary>An array no heap takes: 16384 square, 256 layers of D32F, 256 GiB by hand.</summary>
    private const int HugeSide = 16384, HugeLayers = 256;

    // Given neither VRAM nor system RAM takes it
    // When it is allocated
    // Then that pass stands down for now, says so once, and is tried again at the next move back
    /// <summary>E8: the caster asks for a 256 GiB array every frame for two periods. Oracle: one stand-down line
    /// that is not for the session, and the device's own refusal count rising only at a period.</summary>
    private static void NeitherHeap(VkBackend dev, Checker check, ExtenderLog log, BufferId rb, List<string> heard)
    {
        int faults = dev.Faults;
        CasterPass.VkSunArrayForCheck(dev, HugeSide, HugeLayers);
        check.Expect(CasterPass.BakeStoodDown, "E8: the caster bake did not stand down when no heap took its array");
        int first = dev.Faults - faults;
        for (int f = 0; f < Period / 2; f++)
        {
            ResidencyFrame(dev, log);
            CasterPass.VkSunArrayForCheck(dev, HugeSide, HugeLayers);
        }
        check.Expect(dev.Faults - faults == first, $"E8: the stood-down bake retried {dev.Faults - faults - first} times inside one period");
        for (int f = 0; f <= Period; f++)
        {
            ResidencyFrame(dev, log);
            CasterPass.VkSunArrayForCheck(dev, HugeSide, HugeLayers);
        }
        check.Expect(dev.Faults - faults > first, "E8: the stood-down bake was not tried again at the next move back");
        int said = Count(heard, "CasterPass", "REFUSED");
        check.Expect(said == 1, $"E8: the stand-down was said {said} times, not once");
        check.Expect(Count(heard, "CasterPass", "this session") == 0, "E8: the stand-down says it is for the session, not for now");
        CasterPass.VkResetForCheck();
        Settle(dev, log, rb);
    }

    // Given a mesh arena grow that does not fit after eviction
    // When it is tried
    // Then the meshes already uploaded keep drawing, and cx.log names the grow and its MiB once
    /// <summary>E9: a mesh uploads, filler leaves 8 MiB with nothing idle to evict, then a 24 MiB grow over two
    /// pumps. Oracle: the vertex buffer the first mesh drew from, kept, and "24 MiB" by hand.</summary>
    private static void GrowRefused(VkBackend dev, Checker check, ExtenderLog log, BufferId rb, List<string> heard)
    {
        bool reset = MeshArena.ResetForCheck();
        check.Expect(reset, "E9: the arena holds live meshes");
        if (!reset) return;
        MeshArena.VkForgetForCheck();
        int small = ResidencyMesh(6393, 1000);
        MeshArena.VkPumpForCheck(dev);
        BufferId before = MeshArena.VkVbo(MeshArena.Fmt.Static);
        check.Expect(!before.IsNull, "E9: precondition: the first mesh did not upload");
        BufferId fill = Filler(dev, SpillCapMiB, 8 * MiB, "golden grow filler");
        int big = ResidencyMesh(6394, GrowVerts);
        MeshArena.VkPumpForCheck(dev);
        ResidencyFrame(dev, log);
        MeshArena.VkPumpForCheck(dev);
        BufferId after = MeshArena.VkVbo(MeshArena.Fmt.Static);
        check.Expect(!after.IsNull && after == before, "E9: the refused grow did not leave the uploaded meshes on their own vertex buffer");
        int named = Count(heard, "MeshArena", "grow", $"{GrowMiB} MiB");
        check.Expect(named == 1, $"E9: cx.log names the {GrowMiB} MiB grow {named} times, not once");
        dev.Destroy(fill);
        MeshArena.Release(small);
        MeshArena.Release(big);
        MeshArena.FrameEdgeForCheck();
        MeshArena.VkForgetForCheck();
        Settle(dev, log, rb);
    }

    // Given a device whose only heap is host-visible (an integrated GPU)
    // When it runs
    // Then nothing is refused or reported as in system RAM
    /// <summary>E12: on a device whose every device-local type is host-visible, idle textures, a target and a mesh
    /// grow. Oracle: no refusal counted and no allocation on a heap that is not device-local.</summary>
    private static void UnifiedHeapNothingSpills(VkBackend vk, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.UnifiedHeap.SelfCheck");
        if (!vk.UnifiedMemory)
        {
            check.Skip("this host's device has device-local memory the CPU cannot map: no integrated GPU here");
            return;
        }
        var heard = new List<string>();
        VkBackend? dev = Capped(check, heard, SpillCapMiB, "E12");
        if (dev == null) return;
        VkTextures.DeviceForCheck(dev);
        try
        {
            int faults = dev.Faults;
            uint gl = VkTextures.NewName();
            ImageId tex = OwnedTexture(dev, gl, IdleSide, "golden unified texture");
            ImageId target = Target(dev, 4, "golden unified target", null);
            check.Expect(dev.Faults == faults, $"E12: {dev.Faults - faults} refusals on a device whose only heap is host-visible");
            check.Expect(!tex.IsNull && !dev.InSystemRam(tex), "E12: a texture is reported as in system RAM on an integrated GPU");
            check.Expect(!target.IsNull && !dev.InSystemRam(target), "E12: a render target is reported as in system RAM on an integrated GPU");
            VkTextures.Forget(gl);
            dev.Destroy(target);
        }
        finally
        {
            VkTextures.DeviceForCheck(null);
            dev.Dispose();
        }
    }
}
