// File: OpenModulePlatform.WorkerManager.WindowsService/Models/HostRestartCheck.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

/// <summary>
/// The restart predicate for worker-host upgrades: does the WorkerProcessHost build a
/// running worker was launched with differ from the build the catalogue currently wants?
/// </summary>
/// <remarks>
/// Host identity is deliberately NOT part of <see cref="DesiredWorkerInstance"/>: the host
/// executable is resolved per start and frozen on the process as a witness (R12-F2), so the
/// only truthful comparison is witness-versus-currently-resolved — a definition stamp would
/// drift from the running process exactly when it matters. Before this predicate existed,
/// nothing compared the two at all: a host upgrade left every healthy worker running the old
/// executable forever, and the deployment diagnostics reported a Pending drift
/// ("Worker processes run worker host build X but Y is desired") that nothing ever cleared.
///
/// The null semantics carry the safety guarantees:
/// a configured WorkerManager:WorkerProcessPath yields no artifact identity at all
/// (a null witness, permanently), and a transient resolve miss yields no desired identity
/// for one cycle. Unknown must never read as "changed" — recycling a healthy worker over a
/// bookkeeping gap is the failure mode R6-F2 exists to prevent.
///
/// Artifact IDs are compared, not version strings: the deployment diagnostics compare
/// ReportedHostArtifactId against the desired ArtifactId, and a re-uploaded artifact can
/// carry the same version string under a new id. Version strings are log text, not identity.
/// </remarks>
public static class HostRestartCheck
{
    public static bool RequiresHostRestart(int? startedHostArtifactId, int? desiredHostArtifactId)
    {
        return startedHostArtifactId.HasValue
            && desiredHostArtifactId.HasValue
            && startedHostArtifactId.Value != desiredHostArtifactId.Value;
    }
}
