using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Models;

public sealed class ArtifactProvisioningResultTests
{
    [Fact]
    public void Succeeded_ReturnsIsSuccessTrue()
    {
        var result = ArtifactProvisioningResult.Succeeded("/path", "hash");

        Assert.True(result.IsSuccess);
        Assert.Equal(ArtifactProvisioningState.Succeeded, result.State);
        Assert.Equal("/path", result.LocalPath);
        Assert.Equal("hash", result.ContentHash);
    }

    [Fact]
    public void Failed_ReturnsIsSuccessFalse()
    {
        var result = ArtifactProvisioningResult.Failed(ArtifactProvisioningState.Failed, "/path", "message");

        Assert.False(result.IsSuccess);
        Assert.Equal("message", result.ErrorMessage);
    }

    [Fact]
    public void Failed_WithHashMismatchState_HasCorrectStateCode()
    {
        var result = ArtifactProvisioningResult.Failed(ArtifactProvisioningState.HashMismatch, "/path", "bad hash");

        Assert.Equal(ArtifactProvisioningState.HashMismatch, result.State);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Succeeded_WithNullHash_Succeeds()
    {
        var result = ArtifactProvisioningResult.Succeeded("/path", null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.ContentHash);
    }
}
