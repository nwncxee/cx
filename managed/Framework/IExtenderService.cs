namespace CX.Framework;

/// <summary>
/// Implement this and the host discovers, constructs, and initializes it
/// automatically. The only contract a contributor needs.
/// </summary>
public interface IExtenderService
{
    /// <summary>Wire hooks and state here. Runs once, before hooks are committed.</summary>
    void Initialize(ExtenderLog log, HookService hooks);

    /// <summary>Lower initializes earlier; ties go by type name. Below 0 only for a service whose verdict the
    /// others arm on at their own Initialize.</summary>
    int InitOrder => 0;
}
