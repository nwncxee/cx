using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0009: a cache file read whole or written outside CacheFile. Every cache file carries a checked header and
/// is written through a temporary name, so a file read or written around it can hand a damaged cache to the renderer.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CacheIo : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0009";

    /// <summary>The folders whose files are caches when they read a file whole or write one.</summary>
    private static readonly string[] Scope = { "/managed/Alloy/", "/managed/Graphics/" };

    /// <summary>Where a file may be read whole or written in that scope: CacheFile itself, and the frame snapshot, which
    /// is a diagnostic the player sends, not a cache. <c>tools/gates/cache-io-confined.sh</c> reads this list and refuses
    /// a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Graphics/CacheFile.cs",
        "/managed/Alloy/Pass/FrameTap.Vk.cs",
    };

    /// <summary>File's members that read a binary file whole or open one to write. Text (a dump, a log) is no cache.</summary>
    private static readonly string[] FileCalls = { "ReadAllBytes", "WriteAllBytes", "Create", "OpenWrite" };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Cache file I/O outside CacheFile",
        "'{0}' reads or writes a file outside CacheFile. A cache file goes through CacheFile.Read and CacheFile.Write.",
        "CX.Cache",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "CacheFile checks each cache file's length and hash on read and writes it through a temporary name, so "
            + "a damaged or half-written file is a miss and never geometry. A read or write around it skips both. Self-checks "
            + "plant files by hand and are exempt.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnCall, OperationKind.Invocation);
        context.RegisterOperationAction(OnNew, OperationKind.ObjectCreation);
    }

    private static void OnCall(OperationAnalysisContext c)
    {
        IMethodSymbol m = ((IInvocationOperation)c.Operation).TargetMethod;
        if (m.ContainingType?.ToDisplayString() != "System.IO.File") return;
        foreach (string name in FileCalls) if (m.Name == name) { Report(c, c.Operation.Syntax); return; }
    }

    /// <summary>A FileStream opened to write: any FileAccess argument but Read.</summary>
    private static void OnNew(OperationAnalysisContext c)
    {
        var op = (IObjectCreationOperation)c.Operation;
        if (op.Type?.ToDisplayString() != "System.IO.FileStream") return;
        foreach (IArgumentOperation a in op.Arguments)
            if (a.Parameter?.Type.ToDisplayString() == "System.IO.FileAccess"
                && !(a.Value.ConstantValue.HasValue && a.Value.ConstantValue.Value is int access && access == 1))
            { Report(c, op.Syntax); return; }
    }

    private static void Report(OperationAnalysisContext c, SyntaxNode node)
    {
        string path = node.SyntaxTree.FilePath.Replace('\\', '/');
        bool inScope = false;
        foreach (string s in Scope) inScope |= path.Contains(s);
        if (!inScope || path.Contains("SelfCheck") || path.Contains("ApprovedExamples")) return;
        foreach (string a in Allowed) if (path.Contains(a)) return;
        c.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), node.ToString()));
    }
}
