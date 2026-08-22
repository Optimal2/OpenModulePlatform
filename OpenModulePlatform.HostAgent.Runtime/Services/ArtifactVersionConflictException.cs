namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// An artifact identity (app, package type, target, version) already exists with
/// DIFFERENT content. This is the real "forgot to bump the version" failure, kept
/// as a distinct type so the universal package import can classify it structurally
/// instead of matching on message text. Inherits <see cref="InvalidOperationException"/>
/// so existing expected-failure filters keep covering it.
/// </summary>
public sealed class ArtifactVersionConflictException : InvalidOperationException
{
    public ArtifactVersionConflictException(
        string message,
        string? existingSha256,
        string incomingSha256)
        : base(message)
    {
        ExistingSha256 = existingSha256;
        IncomingSha256 = incomingSha256;
    }

    public string? ExistingSha256 { get; }

    public string IncomingSha256 { get; }
}
