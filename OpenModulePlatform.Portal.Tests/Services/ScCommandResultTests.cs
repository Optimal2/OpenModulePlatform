using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ScCommandResultTests
{
    [Fact]
    public void IsServiceNotFound_ReturnsTrue_ForExitCode1060()
    {
        var result = new ScCommandResult(1060, string.Empty, string.Empty);

        Assert.True(result.IsServiceNotFound());
        Assert.False(result.IsServiceMarkedForDeletion());
        Assert.False(result.IsServiceControlTemporarilyUnavailable());
    }

    [Fact]
    public void IsServiceNotFound_ReturnsTrue_ForKnownTextFallback()
    {
        var result = new ScCommandResult(1, "[SC] OpenService FAILED 1060:", "The specified service does not exist as an installed service.");

        Assert.True(result.IsServiceNotFound());
    }

    [Fact]
    public void IsServiceMarkedForDeletion_ReturnsTrue_ForExitCode1072()
    {
        var result = new ScCommandResult(1072, string.Empty, string.Empty);

        Assert.True(result.IsServiceMarkedForDeletion());
        Assert.False(result.IsServiceNotFound());
    }

    [Fact]
    public void IsServiceMarkedForDeletion_ReturnsTrue_ForKnownTextFallback()
    {
        var result = new ScCommandResult(1, "[SC] DeleteService FAILED 1072:", "The specified service has been marked for deletion.");

        Assert.True(result.IsServiceMarkedForDeletion());
    }

    [Fact]
    public void IsServiceControlTemporarilyUnavailable_ReturnsTrue_ForExitCode1061()
    {
        var result = new ScCommandResult(1061, string.Empty, string.Empty);

        Assert.True(result.IsServiceControlTemporarilyUnavailable());
        Assert.False(result.IsServiceNotFound());
    }

    [Fact]
    public void IsServiceControlTemporarilyUnavailable_ReturnsTrue_ForKnownTextFallback()
    {
        var result = new ScCommandResult(1, "[SC] ControlService FAILED 1061:", "The service cannot accept control messages at this time.");

        Assert.True(result.IsServiceControlTemporarilyUnavailable());
    }

    [Fact]
    public void KnownPredicates_ReturnFalse_ForUnexpectedAccessDeniedResult()
    {
        var result = new ScCommandResult(5, string.Empty, "Access is denied.");

        Assert.False(result.IsServiceNotFound());
        Assert.False(result.IsServiceMarkedForDeletion());
        Assert.False(result.IsServiceControlTemporarilyUnavailable());
    }
}
