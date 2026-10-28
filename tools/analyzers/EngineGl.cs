using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0003: GL issued outside the files that keep it. Alloy renders on Vulkan, so GL has a named set of
/// homes and cannot drift back into a pass.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EngineGl : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0003";

    private const string GlType = "CX.Graphics.Gl";

    /// <summary>Where <c>Gl.*</c> may be used: the bindings, CX's own UI windows on the vanilla renderer, the
    /// engine's model views, the engine texture path's stood-down arm, and the uniform shim with its callers.
    /// <c>tools/gates/gl-confined.sh</c> reads this list and refuses a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Graphics/Gl.cs", "/managed/Graphics/GlProgram.cs", "/managed/Graphics/GlContextService.cs",
        "/managed/Graphics/RenderTarget.cs", "/managed/Graphics/ProgramBinary.cs", "/managed/Graphics/GlProbe.cs",
        "/managed/Graphics/GlTexture.cs", "/managed/Graphics/GpuGraveyard.cs",
        "/managed/Graphics/NullGl.cs", "/managed/Graphics/NullGl.Stubs.cs",
        "/managed/Ui/TextRig.cs", "/managed/Ui/TextRig.Replay.cs", "/managed/Ui/UiHostService.cs",
        "/managed/Ui/KnobPanelService.cs", "/managed/Alloy/Pass/TextAtlas.cs",
        "/managed/Services/Nui/NuiModelEngine.cs", "/managed/Services/Nui/NuiModelEngine.Solve.cs",
        "/managed/Services/Nui/NuiModelView.Gl.cs", "/managed/Services/Perf/DrawStatsService.cs",
        "/managed/Alloy/DdsReader.Formats.cs", "/managed/Alloy/Db/TexRegistry.Load.cs",
        "/managed/Alloy/OwnedTextures.cs", "/managed/Alloy/OwnedTextures.Upload.cs",
        "/managed/Graphics/Vulkan/VkUniformShim.cs",
        "/managed/Alloy/AlloyRenderer.Uniforms.cs", "/managed/Alloy/Programs.cs",
        "/managed/Alloy/Pass/GrassField.cs", "/managed/Alloy/Pass/FogVolume.cs", "/managed/Alloy/Pass/SceneFog.cs",
        "/managed/Alloy/Pass/StageEnv.cs", "/managed/Alloy/Pass/GuiContent.cs",
        "/managed/Alloy/Pass/Weather.Surface.cs", "/managed/Alloy/Pass/Weather.Map.cs",
        "/managed/Alloy/Pass/Weather.Shelter.cs", "/managed/Alloy/Pass/Weather.Esm.cs",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "GL outside the files that keep it",
        "'{0}' issues GL outside the files listed in CX0003. Alloy renders on Vulkan: record the work on the seam (managed/Rhi/).",
        "CX.Backend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Alloy renders on Vulkan, so GL survives in a named set of files -- the bindings, CX's own UI windows on the "
            + "vanilla renderer, the engine's model views and texture path, and the uniform shim. Gl's constants, its Init guard "
            + "and a null check of an entry point are not GL work.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnField, OperationKind.FieldReference);
        context.RegisterOperationAction(OnMethod, OperationKind.Invocation, OperationKind.MethodReference);
    }

    /// <summary>An entry point read: called, or captured. A comparison with null only asks whether the loader ran.</summary>
    private static void OnField(OperationAnalysisContext c)
    {
        var op = (IFieldReferenceOperation)c.Operation;
        if (op.Field.IsConst || op.Field.ContainingType?.ToDisplayString() != GlType) return;
        IOperation? up = op.Parent;
        while (up is IConversionOperation { IsImplicit: true }) up = up.Parent;
        if (up is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }) return;
        Report(c.ReportDiagnostic, op.Syntax);
    }

    private static void OnMethod(OperationAnalysisContext c)
    {
        IMethodSymbol m = c.Operation is IInvocationOperation i ? i.TargetMethod : ((IMethodReferenceOperation)c.Operation).Method;
        if (m.Name != "Init" && m.ContainingType?.ToDisplayString() == GlType) Report(c.ReportDiagnostic, c.Operation.Syntax);
    }

    private static void Report(Action<Diagnostic> report, SyntaxNode node)
    {
        string path = node.SyntaxTree.FilePath.Replace('\\', '/');
        foreach (string a in Allowed) if (path.Contains(a)) return;
        report(Diagnostic.Create(Rule, node.GetLocation(), node.ToString()));
    }
}
