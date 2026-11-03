using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The shipped Diffuse wrap (approved-spec, E9): a player with no <c>light.wrapndl</c> line follows the
/// knob's default. Oracle: the stock terminator, which takes no wrap, so 0 written here.
/// </summary>
internal static class WrapDefaultApprovedExamples
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "WrapDefaultApprovedExamples.SelfCheck");

        // Given a player with no Diffuse wrap line in cx.cfg
        // When the settings panel opens
        // Then Diffuse wrap reads 0, and a lit sphere's shadow line falls where the stock client's does
        check.Expect(Lighting.WrapNdl.Default == 0f, $"CX-448 E9: Diffuse wrap ships at {Lighting.WrapNdl.Default}, the stock terminator takes 0");
        log.Info(check.Ok ? "WrapDefaultApprovedExamples.SelfCheck: passed" : "WrapDefaultApprovedExamples.SelfCheck: FAILED");
    }
}
