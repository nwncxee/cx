using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0007: a push-constant block staged as a span of primitives. The block is one layout the shader
/// names member by member, so the bytes that fill it come from a struct whose members carry those names, never
/// from a positional list of ints or floats the next edit can reorder in silence.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PushBlock : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0007";

    private static readonly ImmutableHashSet<SpecialType> Primitives = ImmutableHashSet.Create(
        SpecialType.System_Boolean, SpecialType.System_Char, SpecialType.System_SByte, SpecialType.System_Byte,
        SpecialType.System_Int16, SpecialType.System_UInt16, SpecialType.System_Int32, SpecialType.System_UInt32,
        SpecialType.System_Int64, SpecialType.System_UInt64, SpecialType.System_Single, SpecialType.System_Double,
        SpecialType.System_IntPtr, SpecialType.System_UIntPtr);

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "A push block is a named struct",
        "this push stages the block as a span of {0}. Declare the layout as a struct and push "
            + "MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in x)), so each member is named where it is written.",
        "CX.Rhi",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A push block written as a positional list of primitives says nothing about which member each "
            + "word fills: a shader that gains, drops or reorders a member leaves the list compiling and wrong. One "
            + "layout, one declaration.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnCall, OperationKind.Invocation);
    }

    private static void OnCall(OperationAnalysisContext c)
    {
        var call = (IInvocationOperation)c.Operation;
        if (call.TargetMethod.Name != "PushConstants"
            || call.TargetMethod.ContainingType?.ToDisplayString() != "CX.Rhi.CommandList") return;
        IArgumentOperation? data = call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "data");
        if (data == null) return;
        string? what = Staged(Bare(data.Value));
        if (what != null) c.ReportDiagnostic(Diagnostic.Create(Rule, c.Operation.Syntax.GetLocation(), what));
    }

    /// <summary>The operation with every conversion and parenthesis around it taken off.</summary>
    private static IOperation Bare(IOperation op)
    {
        while (op is IConversionOperation conv) op = conv.Operand;
        return op;
    }

    /// <summary>The <c>MemoryMarshal</c> calls that only reinterpret: whatever they are handed is the layout, so this
    /// rule reads through them to the span at the call site.</summary>
    private static readonly ImmutableHashSet<string> Reinterprets = ImmutableHashSet.Create("AsBytes", "Cast");

    /// <summary>What the push stages the block as, when that is a span of primitives; null when it is a struct
    /// (or anything this rule cannot read, which it leaves alone).</summary>
    private static string? Staged(IOperation data)
    {
        if (Literal(data) is { } lit) return lit;
        if (data is IInvocationOperation inv && Reinterprets.Contains(inv.TargetMethod.Name)
            && inv.TargetMethod.ContainingType?.ToDisplayString() == "System.Runtime.InteropServices.MemoryMarshal")
        {
            IOperation arg = Bare(inv.Arguments[0].Value);
            return Literal(arg) ?? Element(arg.Type);
        }
        return null;
    }

    private static readonly ImmutableHashSet<OperationKind> Lists = ImmutableHashSet.Create(
        OperationKind.CollectionExpression, OperationKind.ArrayCreation);

    /// <summary>A list of words written at the call site -- a collection expression, an array or a stackalloc --
    /// named by its element type; null when the data is not one. Roslyn models a stackalloc as no operation kind at
    /// all, so that shape is recognised by its syntax.</summary>
    private static string? Literal(IOperation data)
        => Lists.Contains(data.Kind) ? Element(data.Type) ?? "literals"
         : data.Syntax is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax
             ? Element(data.Type)
             : null;

    /// <summary>The primitive element name of a span, array or pointer type; null when its element is a struct,
    /// or when the type is not one this rule reads.</summary>
    private static string? Element(ITypeSymbol? type)
    {
        ITypeSymbol? element = type switch
        {
            IArrayTypeSymbol a => a.ElementType,
            IPointerTypeSymbol p => p.PointedAtType,
            INamedTypeSymbol n when n.OriginalDefinition.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>"
                => n.TypeArguments[0],
            _ => null,
        };
        return element != null && Primitives.Contains(element.SpecialType) ? element.ToDisplayString() : null;
    }
}
