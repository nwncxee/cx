using System.Collections.Immutable;
using System.Linq;
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

    /// <summary>File's members that read a binary file whole, put one in place, or open one to write, whatever their
    /// arguments. Text (a dump, a log) is no cache.</summary>
    private static readonly string[] FileCalls =
    {
        "ReadAllBytes", "ReadAllBytesAsync", "WriteAllBytes", "WriteAllBytesAsync", "AppendAllBytes", "AppendAllBytesAsync",
        "Create", "OpenWrite", "Copy", "Move", "Replace",
    };

    /// <summary>FileInfo's members that put a file in place or open one to write, whatever their arguments.</summary>
    private static readonly string[] InfoCalls = { "Create", "OpenWrite", "CopyTo", "MoveTo", "Replace", "AppendText", "CreateText" };

    /// <summary>File's members that open a file as their FileAccess argument says: a write unless it is Read.</summary>
    private static readonly string[] Openers = { "Open", "OpenHandle" };



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
        var op = (IInvocationOperation)c.Operation;
        IMethodSymbol m = op.TargetMethod;
        string? type = m.ContainingType?.ToDisplayString();
        if (type == "System.IO.RandomAccess" && m.Name.StartsWith("Write")) Report(c, op.Syntax);
        bool writes = type switch
        {
            "System.IO.File" => FileCalls.Contains(m.Name) || (Openers.Contains(m.Name) && Writes(op.Arguments)),
            "System.IO.FileInfo" => InfoCalls.Contains(m.Name) || (m.Name == "Open" && Writes(op.Arguments)),
            _ => false,
        };
        if (writes) Report(c, op.Syntax);
    }

    /// <summary>A FileStream opened to write.</summary>
    private static void OnNew(OperationAnalysisContext c)
    {
        var op = (IObjectCreationOperation)c.Operation;
        if (op.Type?.ToDisplayString() == "System.IO.FileStream" && Writes(op.Arguments)) Report(c, op.Syntax);
    }

    /// <summary>Whether an argument list may open a file to write: any FileAccess but the constant Read, and no FileAccess
    /// at all, which .NET opens read-write.</summary>
    private static bool Writes(ImmutableArray<IArgumentOperation> args)
    {
        foreach (IArgumentOperation a in args)
            if (a.Parameter?.Type.ToDisplayString() == "System.IO.FileAccess")
                return !(a.Value.ConstantValue.Value is int access && access == (int)System.IO.FileAccess.Read);
        return true;
    }

    private static void Report(OperationAnalysisContext c, SyntaxNode node)
    {
        string path = node.SyntaxTree.FilePath.Replace('\\', '/');
        if (!Scope.Any(path.Contains) || path.Contains("SelfCheck") || path.Contains("ApprovedExamples") || Allowed.Any(path.Contains)) return;
        c.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), node.ToString()));
    }
}
