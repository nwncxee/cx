using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0011: a buffer grown outside HeldBuffer, or two buffers held to one size. A method that takes a
/// <c>ref BufferId</c> and creates a buffer is a grow helper, and HeldBuffer is the one; a HeldBuffer call whose size
/// argument another buffer shares sizes a buffer made late to what the other was given.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HeldBufferOnly : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0011";

    /// <summary>The one class whose methods may take a buffer by reference and create it.</summary>
    private const string Owner = "HeldBuffer";

    private static readonly DiagnosticDescriptor Helper = new(
        Id,
        "Buffer grown outside HeldBuffer",
        "'{0}' takes a BufferId by reference and creates a buffer. Grow a pass's buffer with HeldBuffer.Grow, Zeroed or Doubled.",
        "CX.Buffers",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A grow helper keyed on a flag or on a capacity several buffers share remakes a late buffer at the "
            + "size another was given, and the GPU then reads past its end. HeldBuffer keeps each buffer's own size.");

    private static readonly DiagnosticDescriptor Shared = new(
        Id,
        "Buffers held to one size",
        "'{0}' is the size of {1} and of {2}. Give each buffer its own size field (an array of sizes for an array of buffers).",
        "CX.Buffers",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A size field shared by two buffers says the second is as long as the first, which is untrue once "
            + "one of them is made or remade alone.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Helper, Shared);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockStartAction(OnBlock);
    }

    private static void OnBlock(OperationBlockStartAnalysisContext start)
    {
        if (start.OwningSymbol is not IMethodSymbol method) return;
        bool helper = method.ContainingType?.Name != Owner
            && method.Parameters.Any(p => p.RefKind == RefKind.Ref && p.Type.Name == "BufferId");
        var held = new List<(string Cap, string Id, IInvocationOperation Call)>();
        start.RegisterOperationAction(c =>
        {
            var op = (IInvocationOperation)c.Operation;
            IMethodSymbol m = op.TargetMethod;
            if (helper && m.Name == "CreateBuffer" && m.ContainingType?.Name == "VkBackend")
                c.ReportDiagnostic(Diagnostic.Create(Helper, op.Syntax.GetLocation(), method.Name));
            if (m.ContainingType?.Name != Owner) return;
            IArgumentOperation? id = Arg(op, "id"), cap = Arg(op, "cap");
            if (id == null || cap == null) return;
            if (id.Value is IArrayElementReferenceOperation idAt && !SameIndex(idAt, cap.Value))
                c.ReportDiagnostic(Diagnostic.Create(Shared, cap.Syntax.GetLocation(), Text(cap.Value), Text(id.Value),
                    "every other element of " + Text(idAt.ArrayReference)));
            lock (held) held.Add((Text(cap.Value), Text(id.Value), op));
        }, OperationKind.Invocation);
        start.RegisterOperationBlockEndAction(c =>
        {
            foreach (var group in held.GroupBy(h => h.Cap))
            {
                var ids = group.Select(h => h.Id).Distinct().ToList();
                if (ids.Count < 2) continue;
                foreach (var h in group.Where(h => h.Id != ids[0]))
                    c.ReportDiagnostic(Diagnostic.Create(Shared, h.Call.Syntax.GetLocation(), group.Key, ids[0], h.Id));
            }
        });
    }

    /// <summary>The argument bound to the parameter named <paramref name="name"/>.</summary>
    private static IArgumentOperation? Arg(IInvocationOperation op, string name)
        => op.Arguments.FirstOrDefault(a => a.Parameter?.Name == name);

    /// <summary>Whether the size is an element of an array indexed exactly as the buffer is.</summary>
    private static bool SameIndex(IArrayElementReferenceOperation id, IOperation cap)
        => cap is IArrayElementReferenceOperation c && c.Indices.Length == id.Indices.Length
            && c.Indices.Zip(id.Indices, (a, b) => Text(a) == Text(b)).All(x => x);

    /// <summary>An operand's source with its spacing removed, so two spellings of one place compare equal.</summary>
    private static string Text(IOperation o) => string.Concat(o.Syntax.ToString().Where(ch => !char.IsWhiteSpace(ch)));
}
