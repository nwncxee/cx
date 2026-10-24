using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0003: GL issued outside <c>managed/Graphics/</c> and the backend (<c>managed/Alloy/</c>), so engine-context GL
/// cannot drift back. The carve-out is the GL UI rig both render modes keep.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EngineGl : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0003";

    private const string GlType = "CX.Graphics.Gl";

    /// <summary>Where <c>Gl.*</c> may be used: the loader, the backend, and the rig that replays into whichever context is
    /// current (vanilla mode and the GUI-flush refusal), with the vanilla-only model view.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Graphics/", "/managed/Alloy/",
        "/managed/Ui/TextRig.cs", "/managed/Ui/TextRig.Replay.cs",
        "/managed/Services/Nui/NuiModelEngine.cs", "/managed/Services/Nui/NuiModelEngine.Solve.cs",
        "/managed/Services/Nui/NuiModelView.Gl.cs",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "GL outside Graphics and the backend",
        "'{0}' issues GL outside managed/Graphics/ and managed/Alloy/. Put it behind a Graphics helper or in the backend.",
        "CX.Backend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Only the loader (managed/Graphics/) and the backend (managed/Alloy/) issue GL, so a second backend replaces "
            + "one place. Gl's constants, its Init guard and a null check of an entry point are not GL work.");

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
