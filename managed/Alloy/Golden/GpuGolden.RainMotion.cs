namespace CX.Alloy;

/// <summary>The rain motion golden's seam onto the frame's own choice of when rain records its motion.</summary>
internal static partial class GpuGolden
{
    static partial void RainMotionWanted(bool fsr, bool taa, ref int wanted) => wanted = Weather.MotionWanted(fsr, taa) ? 1 : 0;
}
