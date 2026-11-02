namespace CX.Alloy;

/// <summary>The late rain and rain motion goldens' seams onto the frame's own choice of where rain draws.</summary>
internal static partial class GpuGolden
{
    static partial void RainDrawsWhere(bool fsr, bool taa, ref int where) => where = (int)Weather.DrawsWhere(fsr, taa);

    static partial void SplashDrawsWhere(bool fsr, bool taa, ref int where) => where = (int)Weather.DrawsWhere(fsr, taa);

    static partial void RainMotionWhen(bool fsr, bool taa, bool late, ref int wanted) => wanted = Weather.MotionWanted(fsr, taa, late) ? 1 : 0;
}
