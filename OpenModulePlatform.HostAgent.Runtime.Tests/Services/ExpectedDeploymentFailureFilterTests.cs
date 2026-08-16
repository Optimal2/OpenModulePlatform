using System.ComponentModel;
using System.Data.Common;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// One invariant, checked against every copy of it: the exception filters that stand
/// between a deployment fault and the death of the whole HostAgent cycle all recognise the
/// same baseline of faults.
/// </summary>
/// <remarks>
/// R12-F1. Five private copies of this list exist across three files, and every round has
/// found at least one of them a step behind the others: R7-D2 added TimeoutException and
/// Win32Exception to two of five, R8-P4-10 finished the recovery pair, R8-P4-4 added
/// ManagementException and COMException to the ServiceApp pair and to HostAgentEngine and
/// left both WebApp copies without them -- on the one deployment path that actually loads
/// Microsoft.Web.Administration through reflection, where COM faults are not hypothetical.
/// The right structural answer is one shared predicate, but these are five private statics
/// in production code this cluster is not free to re-plumb. A gate that compares them and
/// fails is the next best thing (metod section 4.6), and unlike a comment it cannot drift.
/// </remarks>
// ManagementException is a Windows-only type, as is every code path these filters guard.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ExpectedDeploymentFailureFilterTests
{
    public static TheoryData<string, string> Filters() => new()
    {
        { nameof(WebAppDeploymentService), "IsExpectedDeploymentFailure" },
        { nameof(WebAppDeploymentService), "IsExpectedRecoveryStartFailure" },
        { nameof(ServiceAppDeploymentService), "IsExpectedDeploymentFailure" },
        { nameof(ServiceAppDeploymentService), "IsExpectedRecoveryStartFailure" },
        { nameof(HostAgentEngine), "IsExpectedDeploymentFailure" }
    };

    /// <summary>
    /// Faults every one of these filters must record against the deployment row rather than
    /// let escape. COMException and ManagementException are the two R12-F1 is about; the
    /// rest are the baseline earlier rounds established.
    /// </summary>
    public static IEnumerable<Exception> RecordableFaults()
    {
        yield return new InvalidOperationException("boom");
        yield return new IOException("boom");
        yield return new UnauthorizedAccessException("boom");
        yield return new TimeoutException("boom");
        yield return new Win32Exception(5);
        yield return new ManagementException("boom");
        yield return new COMException("boom", unchecked((int)0x80070005));
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public void EveryFilter_RecordsTheSharedBaselineOfDeploymentFaults(string typeName, string methodName)
    {
        var filter = ResolveFilter(typeName, methodName);

        foreach (var fault in RecordableFaults())
        {
            Assert.True(
                filter(fault),
                $"{typeName}.{methodName} does not match {fault.GetType().FullName}; an unmatched fault ends the whole HostAgent cycle.");
        }
    }

    /// <summary>
    /// The filters stay filters. Without this the test above would still pass if somebody
    /// "fixed" a copy by matching Exception, which would swallow programming errors too.
    /// </summary>
    [Theory]
    [MemberData(nameof(Filters))]
    public void EveryFilter_StillRejectsAFaultThatIsNotADeploymentFault(string typeName, string methodName)
    {
        var filter = ResolveFilter(typeName, methodName);

        Assert.False(
            filter(new NotSupportedException("boom")),
            $"{typeName}.{methodName} matches NotSupportedException, so it is no longer a filter.");
    }

    /// <summary>
    /// Only HostAgentEngine's copy wraps the database work, so only it takes DbException --
    /// documented here so the gate above is not read as "make all five identical".
    /// </summary>
    [Fact]
    public void OnlyTheOutermostFilter_RecordsDatabaseFaults()
    {
        Assert.True(ResolveFilter(nameof(HostAgentEngine), "IsExpectedDeploymentFailure")(new TestDbException("boom")));
        Assert.False(ResolveFilter(nameof(WebAppDeploymentService), "IsExpectedDeploymentFailure")(new TestDbException("boom")));
    }

    private static Func<Exception, bool> ResolveFilter(string typeName, string methodName)
    {
        var type = typeof(HostAgentEngine).Assembly.GetType(
            $"OpenModulePlatform.HostAgent.Runtime.Services.{typeName}",
            throwOnError: true)!;
        var method = type.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(Exception)])
            ?? throw new InvalidOperationException($"{typeName}.{methodName}(Exception) was not found.");

        return exception => (bool)method.Invoke(null, [exception])!;
    }

    private sealed class TestDbException(string message) : DbException(message);
}
