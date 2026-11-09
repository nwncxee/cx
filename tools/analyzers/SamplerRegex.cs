using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0008: a regex that reads a GLSL sampler or image declaration outside the one reader. A text match
/// misses the shapes GLSL allows -- a comment, doubled spaces, a second declarator, an array type -- and answers
/// "not declared" about a stage that declares it.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SamplerRegex : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0008";

    /// <summary>The reader every other caller asks instead: it lexes the stage, so a declaration is a declaration
    /// however it is written.</summary>
    private const string Home = "/managed/Graphics/Vulkan/VkGlsl";

    /// <summary>Who REWRITES such a declaration rather than reading it, which no reader can do for them: the
    /// bindless handle rewrite and the content translation's two declaration passes.
    /// <c>tools/gates/sampler-regex.sh</c> reads this list and refuses a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Alloy/AlloyRenderer.Handles.cs",
        "/managed/Alloy/Core/ContentStage.Text.cs",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "a sampler declaration read by regex outside the one reader",
        "this pattern reads a GLSL sampler or image declaration as text. Ask VkGlsl.Opaques, which lexes the stage: a regex misses a comment, doubled spaces, a second declarator and an array type, and its answer is a silent 'not declared'.",
        "CX.Alloy",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Four readers of sampler declarations drifted apart, each blind to a different shape a content "
            + "override may use, and a wrong answer is a stage rebuilt or a unit left unbound with nothing in the log. "
            + "VkGlsl.Opaques lexes the stage once and answers with each declaration's name, binding and bind kind. The "
            + "files that REWRITE a declaration, which no reader does for them, are listed in CX0008.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        // a [GeneratedRegex] method counts as generated code, so the rule must analyze generated code to see one
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnCall, OperationKind.Invocation, OperationKind.ObjectCreation);
        context.RegisterSyntaxNodeAction(OnAttribute, SyntaxKind.Attribute);
    }

    /// <summary>The same pattern handed to the source generator rather than to the constructor.</summary>
    private static void OnAttribute(SyntaxNodeAnalysisContext c)
    {
        var at = (AttributeSyntax)c.Node;
        if (!at.Name.ToString().Contains("GeneratedRegex")) return;
        AttributeArgumentSyntax? first = at.ArgumentList?.Arguments.Count > 0 ? at.ArgumentList.Arguments[0] : null;
        if (first is null || !ReadsOpaqueDecl(first.Expression.ToString())) return;
        if (!Permitted(c.Node.SyntaxTree.FilePath)) c.ReportDiagnostic(Diagnostic.Create(Rule, first.GetLocation()));
    }

    private static void OnCall(OperationAnalysisContext c)
    {
        INamedTypeSymbol? owner = c.Operation switch
        {
            IInvocationOperation i => i.TargetMethod.ContainingType,
            IObjectCreationOperation o => o.Type as INamedTypeSymbol,
            _ => null,
        };
        if (!IsRegex(owner)) return;
        ImmutableArray<IArgumentOperation> args = c.Operation switch
        {
            IInvocationOperation i => i.Arguments,
            IObjectCreationOperation o => o.Arguments,
            _ => ImmutableArray<IArgumentOperation>.Empty,
        };
        foreach (IArgumentOperation a in args)
        {
            if (a.Parameter?.Name != "pattern") continue;
            if (ReadsOpaqueDecl(a.Value.Syntax.ToString()) && !Permitted(a.Syntax.SyntaxTree.FilePath))
                c.ReportDiagnostic(Diagnostic.Create(Rule, a.Value.Syntax.GetLocation()));
        }
    }

    /// <summary>Is this the framework's own <c>Regex</c>, rather than some other type with a <c>pattern</c>
    /// parameter?</summary>
    private static bool IsRegex(INamedTypeSymbol? t)
        => t?.Name == "Regex" && t.ContainingNamespace?.ToDisplayString() == "System.Text.RegularExpressions";

    /// <summary>Does this pattern text go looking for a sampler or image DECLARATION -- a storage or binding word
    /// beside an opaque type? A pattern that names one without the other reads something else.</summary>
    private static bool ReadsOpaqueDecl(string text)
        => (text.Contains("uniform") || text.Contains("binding"))
        && (text.Contains("sampler") || text.Contains("image"));

    private static bool Permitted(string file)
    {
        string path = (file ?? "").Replace('\\', '/');
        if (path.Contains(Home)) return true;
        foreach (string a in Allowed) if (path.Contains(a)) return true;
        return false;
    }
}
