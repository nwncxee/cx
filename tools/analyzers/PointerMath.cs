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
        description: "A pointer plus an offset, (nint)p + n, or &p[i], is a field nobody named. Declare it as a row and read it through its view, "
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
        context.RegisterOperationAction(OnAsPointer, OperationKind.Invocation);
        context.RegisterSyntaxNodeAction(OnReinterpretIndex, SyntaxKind.ElementAccessExpression);
    }

    /// <summary><c>((float*)&amp;v)[i]</c> reads a struct as an array of its lanes: the lane is a field nobody named.</summary>
    private static void OnReinterpretIndex(SyntaxNodeAnalysisContext c)
    {
        var at = (ElementAccessExpressionSyntax)c.Node;
        SyntaxNode e = at.Expression;
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        if (e is not CastExpressionSyntax cast) return;
        SyntaxNode o = cast.Expression;
        while (o is ParenthesizedExpressionSyntax p) o = p.Expression;
        if (o is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression }) Report(c.ReportDiagnostic, at);
    }

    /// <summary><c>&amp;p[i]</c> is <c>p + i</c>; on an array it is the same move once the array is pinned.</summary>
    private static void OnAddressOf(SyntaxNodeAnalysisContext c)
    {
        var node = (PrefixUnaryExpressionSyntax)c.Node;
        if (node.Operand is ElementAccessExpressionSyntax at && Moved(c.SemanticModel, at, c.CancellationToken))
            Report(c.ReportDiagnostic, node);
    }

    /// <summary><c>Unsafe.AsPointer(ref p[i])</c> is <c>&amp;p[i]</c>. The address of a view's field, or of a bounds-checked
    /// span element, is not a move.</summary>
    private static void OnAsPointer(OperationAnalysisContext c)
    {
        var op = (IInvocationOperation)c.Operation;
        if (op.TargetMethod.Name != "AsPointer" || op.TargetMethod.ContainingType.ToDisplayString() != "System.Runtime.CompilerServices.Unsafe"
            || op.Arguments.Length != 1 || op.SemanticModel == null) return;
        if (Strip(op.Arguments[0].Syntax) is ElementAccessExpressionSyntax at && Moved(op.SemanticModel, at, c.CancellationToken))
            Report(c.ReportDiagnostic, op.Syntax);
    }

    /// <summary>The expression an argument passes, without <c>ref</c> or parentheses.</summary>
    private static SyntaxNode Strip(SyntaxNode n)
    {
        if (n is ArgumentSyntax a) n = a.Expression;
        while (n is ParenthesizedExpressionSyntax p) n = p.Expression;
        if (n is RefExpressionSyntax r) n = r.Expression;
        while (n is ParenthesizedExpressionSyntax p) n = p.Expression;
        return n;
    }

    /// <summary>True when <paramref name="at"/> indexes a pointer, or an array past element 0.</summary>
    private static bool Moved(SemanticModel model, ElementAccessExpressionSyntax at, System.Threading.CancellationToken ct)
    {
        ITypeSymbol? t = model.GetTypeInfo(at.Expression, ct).Type;
        if (t?.TypeKind == TypeKind.Pointer) return true;
        return t?.TypeKind == TypeKind.Array
            && !(at.ArgumentList.Arguments.Count == 1 && at.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.ValueText: "0" });
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
        if (FromPointer(ptr) && !FromPointer(by)) return true;
        ITypeSymbol? p = ptr.Type, n = Unconverted(by).Type;
        if (p == null || n == null) return false;
        if (p.TypeKind == TypeKind.Pointer) return n.TypeKind != TypeKind.Pointer;
        return IsAddress(Unconverted(ptr).Type) && !IsAddress(n) && n.TypeKind != TypeKind.Pointer;
    }

    /// <summary>True when <paramref name="o"/> is a pointer cast to an integer: <c>(nint)p</c>, <c>(long)p</c>.</summary>
    private static bool FromPointer(IOperation o) =>
        Unconverted(o) is IConversionOperation { IsImplicit: false } conv && conv.Operand.Type?.TypeKind == TypeKind.Pointer
        && conv.Type?.TypeKind != TypeKind.Pointer;

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
