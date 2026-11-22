using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Found tests on which textures an eviction picks and what a restore tells the records, on this device
/// uncapped: eviction is asked for directly. Oracle: each name's residency and the record epoch, by hand.</summary>
internal static partial class GpuGolden
{
    /// <summary>Side of each texture these tests evict; any size serves, since the eviction is asked for one texture's bytes.</summary>
    private const int EvictSide = 16;

    /// <summary>The found eviction tests, under a name of their own; a failure fails <paramref name="suite"/> too.</summary>
    private static void EvictFoundSelfCheck(VkBackend vk, Checker suite, ExtenderLog log)
    {
        var check = new Checker(log, "GpuGolden.EvictFound");
        VkTextures.DeviceForCheck(vk);
        uint held = VkTextures.NewName(), loose = VkTextures.NewName();
        int slot = -1;
        try
        {
            bool made = !OwnedTexture(vk, held, EvictSide, "golden evict held").IsNull
                      & !OwnedTexture(vk, loose, EvictSide, "golden evict loose").IsNull;
            check.Expect(made, "evict found: a 16x16 owned texture was refused");
            if (made)
            {
                slot = RecordHeldSpared(vk, check, log, held, loose);
                RestoreRestagesRecords(vk, check, log, loose);
            }
        }
        finally
        {
            AlloyRenderer.DropHoldFixture(slot);
            VkTextures.Forget(held);
            VkTextures.Forget(loose);
            VkTextures.DeviceForCheck(null);
        }
        if (!check.Ok) suite.Ok = false;
        log.Info(check.Ok ? "GpuGolden.EvictFound: passed" : "GpuGolden.EvictFound: FAILED");
    }

    // Given a record that holds texture T, staged once, then many frames with no restage, and a texture no record holds drawn since
    // When eviction runs for one texture's room
    // Then T stays on the device and the texture no record holds is evicted
    private static int RecordHeldSpared(VkBackend vk, Checker check, ExtenderLog log, uint held, uint loose)
    {
        ResidencyFrame(vk, log);
        VkTextures.Index((int)held);
        int slot = AlloyRenderer.HoldFixture(held);
        check.Expect(slot >= 0, "evict held: no record slot was free for the fixture");
        for (int f = 0; f < 3; f++) ResidencyFrame(vk, log);
        VkTextures.Index((int)loose);
        for (int f = 0; f < 3; f++) ResidencyFrame(vk, log);
        bool evicted = vk.Residents?.Evict(VkTextures.Bytes(loose)) == true;
        check.Expect(evicted, "evict held: nothing was evicted, though one texture no record holds is idle");
        check.Expect(VkTextures.Resident(held), "evict held: the texture a live record holds was evicted because the record was not restaged");
        check.Expect(!VkTextures.Resident(loose), "evict held: the idle texture no record holds stayed on the device");
        return slot;
    }

    // Given a parked texture a restaged record asked for, so the record holds the stand-in's index
    // When the frame's end seats it again
    // Then every record is staged again, and a frame that seats nothing stages none
    private static void RestoreRestagesRecords(VkBackend vk, Checker check, ExtenderLog log, uint parked)
    {
        check.Expect(VkTextures.Index((int)parked) == 0, "restore: precondition: the evicted texture did not draw the stand-in");
        int before = AlloyRenderer.RecordEpoch;
        ResidencyFrame(vk, log);
        check.Expect(VkTextures.Resident(parked), "restore: the wanted texture was not seated at the frame's end");
        int seated = AlloyRenderer.RecordEpoch;
        check.Expect(seated != before, "restore: the texture came back but no record is staged again, so each keeps the stand-in's index");
        ResidencyFrame(vk, log);
        check.Expect(AlloyRenderer.RecordEpoch == seated, "restore: a frame that seated nothing staged every record again");
    }
}
