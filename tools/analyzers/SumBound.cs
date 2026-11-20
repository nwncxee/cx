using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0010: in the backend, an integer sum, or a local declared as one, on either side of an ordering comparison
/// other than a sign test, such as <c>offset + n &gt; size</c>. A large enough offset wraps the sum back below the size
/// and the range passes; <c>Bounds.Inside</c> and <c>Bounds.InWords</c> subtract instead.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SumBound : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0010";

    /// <summary>The folders whose ranges arrive from callers and reach the device or a mapped allocation unchecked.</summary>
    private static readonly string[] Scope = { "/managed/Graphics/", "/managed/Rhi/" };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Range tested by a sum",
        "'{0}' tests a range by a sum, which wraps. Use Bounds.Inside(at, n, size), or Bounds.InWords for a Vulkan buffer range.",
        "CX.Bounds",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An offset near the largest value of its type wraps 'offset + n' back below the size, so the check "
            + "passes and the device reads or writes past the buffer. Bounds subtracts from the size instead, which cannot wrap.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnCompare, OperationKind.Binary);
    }

    private static void OnCompare(OperationAnalysisContext c)
    {
        var op = (IBinaryOperation)c.Operation;
        if (op.OperatorKind is not (BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual)) return;
        if (op.ConstantValue.HasValue || Zero(op.LeftOperand) || Zero(op.RightOperand)) return;
        if (!(Summed(op.LeftOperand) || Summed(op.RightOperand))) return;
        string path = op.Syntax.SyntaxTree.FilePath.Replace('\\', '/');
        if (!Scope.Any(path.Contains) || path.Contains("SelfCheck")) return;
        c.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), op.Syntax.ToString()));
    }

    /// <summary>A constant zero: a sum tested against it is a sign test, which has no size to stay inside.</summary>
    private static bool Zero(IOperation o) => o.ConstantValue is { HasValue: true, Value: 0 or 0L or 0U or 0UL };

    /// <summary>A sum, or a local whose declaration holds one (<c>long end = at + n; if (end &gt; size)</c>).</summary>
    private static bool Summed(IOperation o)
    {
        if (IsSum(o)) return true;
        while (o is IConversionOperation conv) o = conv.Operand;
        if (o is not ILocalReferenceOperation { Local: var local } || o.SemanticModel is not { } model) return false;
        foreach (SyntaxReference r in local.DeclaringSyntaxReferences)
            if (r.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } init }
                && model.GetOperation(init) is { } value && IsSum(value)) return true;
        return false;
    }

    /// <summary>An integer addition the compiler does not fold, under any conversion the comparison put on it.</summary>
    private static bool IsSum(IOperation o)
    {
        while (o is IConversionOperation conv) o = conv.Operand;
        while (o is IParenthesizedOperation p) o = p.Operand;
        return o is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } add && !add.ConstantValue.HasValue
            && add.Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
                or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
    }
}
