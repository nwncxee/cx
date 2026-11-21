namespace CX.Framework;

/// <summary>When a self-check suite runs: before the services initialize, or after every one has.</summary>
public enum SuitePhase
{
    /// <summary>Ahead of every service's <c>Initialize</c>, once the shared accessors are attached.</summary>
    BeforeServices,
    /// <summary>After every service's <c>Initialize</c>, before the frame walk is sealed.</summary>
    AfterServices,
}

/// <summary>Marks a static method taking one <see cref="ExtenderLog"/> as a self-check suite: the host finds it
/// and runs it in its phase, so a new suite needs no registration line.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SelfCheckSuiteAttribute(SuitePhase phase) : Attribute
{
    /// <summary>The phase the suite runs in.</summary>
    public SuitePhase Phase { get; } = phase;
}
