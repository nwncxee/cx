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

    /// <summary>Where <c>Gl.*</c> may be used: the binding and its program builder and probe, the stubs for the
    /// entry points the engine itself imports, CX's own windows and the NUI model view drawn on the engine's
    /// renderer in a session Vulkan refused, and the engine draw census.
    /// <c>tools/gates/gl-confined.sh</c> reads this list and refuses a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Graphics/Gl.cs", "/managed/Graphics/GlProgram.cs", "/managed/Graphics/GlProbe.cs",
        "/managed/Ui/TextRig.cs", "/managed/Ui/TextRig.Replay.cs",
        "/managed/Services/Nui/NuiModelEngine.cs", "/managed/Services/Nui/NuiModelEngine.Solve.cs",
        "/managed/Services/Nui/NuiModelView.Gl.cs",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "GL outside the files that keep it",
        "'{0}' issues GL outside the files listed in CX0003. Alloy renders on Vulkan: record the work on the seam (managed/Rhi/).",
        "CX.Backend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Alloy renders on Vulkan, so GL survives in a named set of files -- the binding, the stubs for the entry "
            + "points the engine imports, and CX's own windows and NUI model view drawn on the engine's renderer in a session "
            + "Vulkan refused. Gl's Init guard and a null check of an entry point are not GL work, and a GL enum number the "
            + "engine's own data speaks is EngineTokens.");

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
