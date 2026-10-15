using System.Collections.Immutable;
using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0001: a pointer moved by an offset outside <c>managed/Framework/</c>. A place in memory is a field on a view,
/// and the view's own body is the one place its offset is added.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PointerMath : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0001";

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Pointer plus an offset outside a view",
        "'{0}' moves a pointer by an offset. Read the place as a field on a view under Framework/ instead (ADR 0031).",
        "CX.Layout",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A pointer plus an offset, or &p[i], is a field nobody named. Declare it as a row and read it through its view, "
            + "or give the memory a struct, a view or a Span of its own; only managed/Framework/ adds an offset to a pointer.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnBinary, OperationKind.Binary);
        context.RegisterOperationAction(OnCompound, OperationKind.CompoundAssignment);
        context.RegisterOperationAction(OnStep, OperationKind.Increment, OperationKind.Decrement);
        context.RegisterSyntaxNodeAction(OnAddressOf, SyntaxKind.AddressOfExpression);
    }

    /// <summary><c>&amp;p[i]</c> is <c>p + i</c>; on an array it is the same move once the array is pinned.</summary>
    private static void OnAddressOf(SyntaxNodeAnalysisContext c)
    {
        var node = (PrefixUnaryExpressionSyntax)c.Node;
        if (node.Operand is not ElementAccessExpressionSyntax at) return;
        ITypeSymbol? t = c.SemanticModel.GetTypeInfo(at.Expression, c.CancellationToken).Type;
        if (t?.TypeKind is not (TypeKind.Pointer or TypeKind.Array)) return;
        if (t.TypeKind == TypeKind.Array && at.ArgumentList.Arguments.Count == 1 && at.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.ValueText: "0" }) return;
        Report(c.ReportDiagnostic, node);
    }

    private static void OnBinary(OperationAnalysisContext c)
    {
        var op = (IBinaryOperation)c.Operation;
        if (op.OperatorKind is not (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)) return;
        if (Moves(op.LeftOperand, op.RightOperand) || (op.OperatorKind == BinaryOperatorKind.Add && Moves(op.RightOperand, op.LeftOperand)))
            Report(c.ReportDiagnostic, op.Syntax);
    }

    private static void OnCompound(OperationAnalysisContext c)
    {
        var op = (ICompoundAssignmentOperation)c.Operation;
        if (op.OperatorKind is (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract) && Moves(op.Target, op.Value)) Report(c.ReportDiagnostic, op.Syntax);
    }

    private static void OnStep(OperationAnalysisContext c)
    {
        var op = (IIncrementOrDecrementOperation)c.Operation;
        if (op.Target.Type?.TypeKind == TypeKind.Pointer) Report(c.ReportDiagnostic, op.Syntax);
    }

    /// <summary>True when <paramref name="ptr"/> is an address and <paramref name="by"/> an integer offset, not a second address.</summary>
    private static bool Moves(IOperation ptr, IOperation by)
    {
        ITypeSymbol? p = ptr.Type, n = Unconverted(by).Type;
        if (p == null || n == null) return false;
        if (p.TypeKind == TypeKind.Pointer) return n.TypeKind != TypeKind.Pointer;
        return IsAddress(Unconverted(ptr).Type) && !IsAddress(n) && n.TypeKind != TypeKind.Pointer;
    }

    private static bool IsAddress(ITypeSymbol? t) => t?.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr;

    private static IOperation Unconverted(IOperation o)
    {
        while (o is IConversionOperation { IsImplicit: true } conv) o = conv.Operand;
        return o;
    }

    private static void Report(Action<Diagnostic> report, SyntaxNode node)
    {
        string path = node.SyntaxTree.FilePath.Replace('\\', '/');
        if (path.Contains("/managed/Framework/")) return;
        report(Diagnostic.Create(Rule, node.GetLocation(), node.ToString()));
    }
}
