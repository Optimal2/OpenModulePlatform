namespace OpenModulePlatform.Web.Shared.Runtime;

/// <summary>Supplies the runtime advisory to the portal top bar.</summary>
/// <remarks>The abstraction keeps version selection injectable without changing consumer applications.</remarks>
public interface IOmpRuntimeAdvisorySource
{
    OmpRuntimeAdvisoryResult GetAdvisory();
}

/// <summary>Reads the advisory for the current process.</summary>
public sealed class OmpRuntimeAdvisorySource : IOmpRuntimeAdvisorySource
{
    public static OmpRuntimeAdvisorySource Instance { get; } = new();

    public OmpRuntimeAdvisoryResult GetAdvisory()
        => OmpRuntimeAdvisory.EvaluateCurrentProcess();
}
