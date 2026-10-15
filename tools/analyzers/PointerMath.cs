using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0001: a pointer moved by an offset outside <c>managed/Framework/</c>. A place in memory is a field on a view,
/// and the view's own body is the one place its offset is added.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PointerMath : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a pragma or a build log names.</summary>
    public const string Id = "CX0001";

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Pointer plus an offset outside a view",
        "'{0}' moves a pointer by an offset. Read the place as a field on a view under Framework/ instead (ADR 0031).",
        "CX.Layout",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A pointer plus an offset is a field nobody named. Declare it as a row and read it through its view, "
            + "or give the memory a struct or a view of its own; only managed/Framework/ adds an offset to a pointer.");

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
    }

    private static void OnBinary(OperationAnalysisContext c)
    {
        var op = (IBinaryOperation)c.Operation;
        if (op.OperatorKind is not (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract)) return;
        if (Moves(op.LeftOperand, op.RightOperand) || (op.OperatorKind == BinaryOperatorKind.Add && Moves(op.RightOperand, op.LeftOperand)))
            Report(c, op);
    }

    private static void OnCompound(OperationAnalysisContext c)
    {
        var op = (ICompoundAssignmentOperation)c.Operation;
        if (op.OperatorKind is (BinaryOperatorKind.Add or BinaryOperatorKind.Subtract) && Moves(op.Target, op.Value)) Report(c, op);
    }

    private static void OnStep(OperationAnalysisContext c)
    {
        var op = (IIncrementOrDecrementOperation)c.Operation;
        if (op.Target.Type?.TypeKind == TypeKind.Pointer) Report(c, op);
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

    private static void Report(OperationAnalysisContext c, IOperation op)
    {
        string path = op.Syntax.SyntaxTree.FilePath.Replace('\\', '/');
        if (path.Contains("/managed/Framework/")) return;
        c.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), op.Syntax.ToString()));
    }
}
