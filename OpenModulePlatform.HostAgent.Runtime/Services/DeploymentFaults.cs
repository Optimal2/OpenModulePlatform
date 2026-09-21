using System.ComponentModel;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// The one list of deployment faults that are recorded against the deployment row instead
/// of aborting the whole HostAgent cycle.
/// </summary>
/// <remarks>
/// R5-D1, R7-D2, R8-P4-4, R8-P4-10 and R12-F1 each found one of five private copies of this
/// list a step behind the others. The copies stay as named entry points (the gate in
/// ExpectedDeploymentFailureFilterTests resolves them by name) but all delegate here, so
/// there is nothing left to drift. HostAgentEngine additionally takes DbException because
/// only it wraps the database work.
/// </remarks>
internal static class DeploymentFaults
{
    public static bool IsRecordable(Exception exception)
        => exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or Win32Exception
            or ManagementException
            or COMException
            || (exception is TargetInvocationException invocation
                && invocation.InnerException is not null
                && IsRecordable(invocation.InnerException));
}
