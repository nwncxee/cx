using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0004: a queue submit, a fence or a semaphore wait outside the backend's timelines. Every graphics submit
/// signals the timeline every lifetime waits on, so a second path would hand out numbers nothing waits on.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueueSync : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0004";

    private const string Home = "/managed/Graphics/Vulkan/VkBackend.Sync.cs";

    private static readonly ImmutableHashSet<string> Calls = ImmutableHashSet.Create(
        "vkQueueSubmit", "vkQueueSubmit2", "vkQueueSubmit2KHR", "vkQueueWaitIdle",
        "vkWaitSemaphores", "vkWaitSemaphoresKHR", "vkGetSemaphoreCounterValue", "vkGetSemaphoreCounterValueKHR", "vkSignalSemaphore",
        "vkCreateFence", "vkWaitForFences", "vkResetFences", "vkGetFenceStatus");

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Queue sync outside the timeline",
        "'{0}' submits or waits outside VkBackend.Sync.cs. Go through QueueSubmit, WaitFor or PassedNow.",
        "CX.Backend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The backend's graphics queue has a timeline semaphore: a submit signals its number, and readbacks, deaths, "
            + "staging and descriptor pools wait on those numbers; the async compute queue's own timeline is waited on only by "
            + "a graphics submit's join. A submit, fence or wait made elsewhere is invisible to them.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnMethod, OperationKind.Invocation, OperationKind.MethodReference);
    }

    private static void OnMethod(OperationAnalysisContext c)
    {
        IMethodSymbol m = c.Operation is IInvocationOperation i ? i.TargetMethod : ((IMethodReferenceOperation)c.Operation).Method;
        if (!Calls.Contains(m.Name) || m.ContainingNamespace?.ToDisplayString() != "Vortice.Vulkan") return;
        if (c.Operation.Syntax.SyntaxTree.FilePath.Replace('\\', '/').Contains(Home)) return;
        c.ReportDiagnostic(Diagnostic.Create(Rule, c.Operation.Syntax.GetLocation(), m.Name));
    }
}
