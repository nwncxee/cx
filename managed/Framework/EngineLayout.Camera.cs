namespace CX.Framework;

/// <summary>The camera pull-in's rows of <see cref="EngineLayout"/>.</summary>
public static unsafe partial class EngineLayout
{
    /// <summary>CNWCModule -&gt; the camera pull-in's predicted yaw and pitch impulses (floats): <c>UpdateNormalCameraCollision</c>
    /// (Linux @0x7daf80, Windows rva 0x7d2940) adds +0x154 to yaw and +0x150 to pitch. <c>TurnCamera(deg, 1)</c> (@0x7d8960 /
    /// rva 0x7cfe60) and <c>TiltCamera(deg, 1)</c> both store into +0x150, nothing stores +0x154, only the constructor clears either.</summary>
    public static readonly int ModulePredictYawImpulse = Field<float>("CNWCModule", "predictYawImpulse");
    /// <inheritdoc cref="ModulePredictYawImpulse"/>
    public static readonly int ModulePredictPitchImpulse = Field<float>("CNWCModule", "predictPitchImpulse");
}
