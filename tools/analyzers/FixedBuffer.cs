using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CX.Analyzers;

/// <summary>CX0002: a <c>fixed</c> buffer. It decays to a pointer, so a record built from one is read at hand-picked
/// indices; name the fields, or use an <c>[InlineArray]</c> of a named element type, which is bounds-checked.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FixedBuffer : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0002";

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Fixed-size buffer",
        "'{0}' is a fixed buffer. Name the fields, or use an [InlineArray] of a named element type (ADR 0031).",
        "CX.Layout",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A fixed buffer is a pointer with a length nobody checks, and a record made of one is read at offsets "
            + "written by hand. Named fields say what each place is; an [InlineArray] gives a Span that checks its bounds.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(OnField, SymbolKind.Field);
    }

    private static void OnField(SymbolAnalysisContext c)
    {
        var f = (IFieldSymbol)c.Symbol;
        if (f.IsFixedSizeBuffer) c.ReportDiagnostic(Diagnostic.Create(Rule, f.Locations[0], f.ContainingType.Name + "." + f.Name));
    }
}
