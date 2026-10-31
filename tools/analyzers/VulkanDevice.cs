using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CX.Analyzers;

/// <summary>CX0005: a Vulkan or VMA entry point outside the backend. The device is the backend's, so a feature
/// records on the seam instead of reaching it; VMA's virtual blocks touch no device and keep books where listed.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class VulkanDevice : DiagnosticAnalyzer
{
    /// <summary>The rule's id, the one a build log names.</summary>
    public const string Id = "CX0005";

    private const string Ns = "Vortice.Vulkan";
    private const string Home = "/managed/Graphics/";

    /// <summary>VMA's virtual blocks: a TLSF free list over plain numbers, no allocator, no device, no memory.
    /// They are the one part of the binding a book-keeper outside the backend may name.</summary>
    private static readonly ImmutableHashSet<string> VirtualBlock = ImmutableHashSet.Create(
        "vmaCreateVirtualBlock", "vmaDestroyVirtualBlock", "vmaClearVirtualBlock", "vmaIsVirtualBlockEmpty",
        "vmaVirtualAllocate", "vmaVirtualFree", "vmaGetVirtualAllocationInfo", "vmaSetVirtualAllocationUserData",
        "vmaGetVirtualBlockStatistics", "vmaCalculateVirtualBlockStatistics");

    /// <summary>Who keeps books on virtual blocks outside the backend: the mesh arena, which places a mesh in a
    /// block of element counts. <c>tools/gates/vma-confined.sh</c> reads this list and refuses a name no file answers to.</summary>
    private static readonly string[] Allowed =
    {
        "/managed/Alloy/Db/MeshArena.Blocks.cs",
    };

    private static readonly DiagnosticDescriptor Rule = new(
        Id,
        "Vulkan or VMA outside the backend",
        "'{0}' is a Vulkan or VMA entry point outside managed/Graphics/. The device is the backend's: record the work on the seam (managed/Rhi/). Only VMA's virtual blocks, in the files CX0005 lists, keep books outside it.",
        "CX.Backend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every allocation, image, buffer, pipeline and command that reaches the device goes through the backend, "
            + "which owns the device's lifetime and the timelines its deaths wait on; a call made elsewhere is invisible to "
            + "both. VMA's virtual-block family is the exception: it allocates places in a number line and never touches the "
            + "device, so a book-keeper such as the mesh arena may call it. Vulkan's enums, structs and handle types are data, "
            + "not calls, and are free anywhere.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(OnMember, OperationKind.Invocation, OperationKind.MethodReference,
                                        OperationKind.FieldReference, OperationKind.PropertyReference);
    }

    private static void OnMember(OperationAnalysisContext c)
    {
        ISymbol? s = c.Operation switch
        {
            IInvocationOperation i => i.TargetMethod,
            IMethodReferenceOperation m => m.Method,
            IFieldReferenceOperation f => f.Field,
            IPropertyReferenceOperation p => p.Property,
            _ => null,
        };
        if (s is null || !IsEntryPoint(s.Name) || s.ContainingNamespace?.ToDisplayString() != Ns) return;

        string path = c.Operation.Syntax.SyntaxTree.FilePath.Replace('\\', '/');
        if (path.Contains(Home)) return;
        if (VirtualBlock.Contains(s.Name))
            foreach (string a in Allowed) if (path.Contains(a)) return;

        c.ReportDiagnostic(Diagnostic.Create(Rule, c.Operation.Syntax.GetLocation(), s.Name));
    }

    /// <summary>The C naming the binding keeps: <c>vkDoThing</c> or <c>vmaDoThing</c>. Enum members, structs and
    /// handle types do not spell themselves that way, so data stays free of the rule.</summary>
    private static bool IsEntryPoint(string name) =>
        (name.Length > 2 && name[0] == 'v' && name[1] == 'k' && char.IsUpper(name[2]))
        || (name.Length > 3 && name[0] == 'v' && name[1] == 'm' && name[2] == 'a' && char.IsUpper(name[3]));
}
