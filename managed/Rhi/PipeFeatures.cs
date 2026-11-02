namespace CX.Rhi;

/// <summary>The label prefixes the backend's pipeline tally tells a feature by. Each of these passes takes its labels
/// from here, so a renamed label still counts; <c>tools/gates/pipe-features.sh</c> refuses one spelled by hand.</summary>
public static class PipeFeatures
{
    public const string Grass = "vk grass ", Particles = "vk particles ", Probe = "vk probe ";

    /// <summary>A CX window program's label; its pipelines are labelled <see cref="CxWindow"/> and the rest.</summary>
    public const string CxWindowProgram = "CX window ";

    public const string CxWindow = "vk " + CxWindowProgram;
}
