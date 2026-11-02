using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0006: GL's depth convention outside the files that keep it. The world's depth is reversed, so GL's test,
/// a depth test with no comparison named, and a clear to 1 are refused; the casters and the GUI name <c>GlDepth</c>.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DepthConvention : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0006";

    private const string CompareType = "CX.Rhi.CompareOp", GlDepthType = "CX.Alloy.GlDepth";
    private const string DepthStateType = "CX.Rhi.DepthState", DepthAttachmentType = "CX.Rhi.DepthAttachment";

    /// <summary>Where <c>GlDepth</c> may be named: the passes that draw into a depth that keeps GL's convention --
    /// casters, shadow cascades, the GUI -- and the golden that holds them to it.
    /// <c>tools/gates/depth-confined.sh</c> reads this list and refuses a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Alloy/Pass/CasterPass.Vk.cs",
        "/managed/Alloy/Pass/GuiPassVk.Issue.cs", "/managed/Alloy/Pass/GuiPassVk.Bind.cs",
        "/managed/Alloy/AlloyRenderer.VkGrass.cs", "/managed/Alloy/AlloyRenderer.VkColour.cs",
        "/managed/Alloy/Golden/GpuGolden.ReversedZApprovedExamples.SelfCheck.cs",
        "/managed/Alloy/Golden/GpuGolden.DeferredRig.cs",
    };

    /// <summary>The backend, which maps every comparison and records every clear without knowing whose depth it is.</summary>
    private static readonly string[] Backend = { "/managed/Rhi/", "/managed/Graphics/" };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "GL's depth convention outside the files that keep it",
        "{0}. The world's depth is reversed: use WorldDepth's comparisons and clear, or GlDepth's in a file CX0006 lists.",
        "CX.Alloy",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The world's depth is reversed (near 1, far 0): a world pass tests WorldDepth.Nearer or NearerOrEqual "
            + "and clears to WorldDepth.Clear. Casters, shadow cascades and the GUI keep GL's depth and name GlDepth, "
            + "which only the files in CX0006's list may. A depth test with no comparison named would get GL's by default.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnField, OperationKind.FieldReference);
        context.RegisterOperationAction(OnCreate, OperationKind.ObjectCreation);
    }

    /// <summary>GL's comparison named raw, or <c>GlDepth</c> named outside the files that keep GL depth.</summary>
    private static void OnField(OperationAnalysisContext c)
    {
        var op = (IFieldReferenceOperation)c.Operation;
        string? type = op.Field.ContainingType?.ToDisplayString();
        if (In(op.Syntax, Backend) || c.ContainingSymbol.ContainingType?.ToDisplayString() == GlDepthType) return;
        if (type == CompareType && op.Field.Name is "Less" or "LessEqual")
            Report(c, op.Syntax, $"'{op.Syntax}' is GL's depth test");
        else if (type == GlDepthType && !In(op.Syntax, Allowed))
            Report(c, op.Syntax, $"'{op.Syntax}' is GL's depth in a file CX0006 does not list");
    }

    /// <summary>A depth test switched on with no comparison named, or a depth target cleared to a literal 1.</summary>
    private static void OnCreate(OperationAnalysisContext c)
    {
        var op = (IObjectCreationOperation)c.Operation;
        string? type = op.Type?.ToDisplayString();
        if (In(op.Syntax, Backend)) return;
        if (type == DepthStateType)
        {
            IArgumentOperation? test = Arg(op, "Test"), compare = Arg(op, "Compare");
            bool off = test == null || test.Value.ConstantValue is { HasValue: true, Value: false };
            if (!off && (compare == null || compare.ArgumentKind == ArgumentKind.DefaultValue))
                Report(c, op.Syntax, $"'{op.Syntax}' tests depth and names no comparison");
        }
        else if (type == DepthAttachmentType && Arg(op, "Depth") is { } depth
                 && depth.Value.DescendantsAndSelf().OfType<ILiteralOperation>().Any(IsOne))
            Report(c, op.Syntax, $"'{op.Syntax}' clears depth to 1, GL's far plane");
    }

    private static IArgumentOperation? Arg(IObjectCreationOperation op, string name)
        => op.Arguments.FirstOrDefault(a => a.Parameter?.Name == name);

    private static bool IsOne(ILiteralOperation l) => l.ConstantValue is { HasValue: true, Value: 1f or 1 };

    private static bool In(SyntaxNode node, string[] paths)
    {
        string path = node.SyntaxTree.FilePath.Replace('\\', '/');
        return paths.Any(p => path.Contains(p));
    }

    private static void Report(OperationAnalysisContext c, SyntaxNode node, string what)
        => c.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), what));
}
