namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> a content pair's stages read beyond the ones the Alloy stages already
/// do: the rest of <c>GLRender::m_shaderExposedRuntimeData</c>'s run, uploaded under the engine's own names.</summary>
public static unsafe partial class EngineLayout
{
    // CX-303: the rest of the run a content stage can name, paired the same way -- ConnectShaderData's slot by name, then
    // the displacement SetUniforms feeds it from: +0xaf8 "moduleMinutesPerHour" <- +0x24, +0xb10 "moduleTransitionTime"
    // <- +0x14 (float), +0xb18 "areaWeatherType" <- +0x34, +0xb40 "userinputMousePosition" <- +0x40/+0x44 (an ivec2),
    // +0xb44 "userinputMouseButtons" <- +0x48. Identical on Windows: the loads at 0x1400b873d, 0x1400b87c3, 0x1400b8811,
    // 0x1400b8863/886a and 0x1400b8880 in its SetUniforms (@0xb8380), off m_shaderExposedRuntimeData rva 0x128ea10.
    /// <summary><c>moduleMinutesPerHour</c>, the module's game-minutes per hour.</summary>
    public static readonly int RtModuleMinutesPerHour = Field<int>("ShaderExposedRuntimeData", "moduleMinutesPerHour");
    /// <summary><c>moduleTransitionTime</c> (float), the length of an hour transition.</summary>
    public static readonly int RtModuleTransitionTime = Field<float>("ShaderExposedRuntimeData", "moduleTransitionTime");
    /// <summary><c>areaWeatherType</c>, the area's weather kind.</summary>
    public static readonly int RtAreaWeatherType = Field<int>("ShaderExposedRuntimeData", "areaWeatherType");
    /// <summary><c>userinputMousePosition</c>: the mouse's screen pixel, x then y.</summary>
    public static readonly int RtMouseX = Field<int>("ShaderExposedRuntimeData", "mouseX");
    /// <inheritdoc cref="RtMouseX"/>
    public static readonly int RtMouseY = Field<int>("ShaderExposedRuntimeData", "mouseY");
    /// <summary><c>userinputMouseButtons</c>, the button mask.</summary>
    public static readonly int RtMouseButtons = Field<int>("ShaderExposedRuntimeData", "mouseButtons");
}
