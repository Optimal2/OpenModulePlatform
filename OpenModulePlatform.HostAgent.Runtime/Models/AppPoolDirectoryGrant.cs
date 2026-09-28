namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// One filesystem right HostAgent ensures for an IIS application pool during a web app
/// deployment. <see cref="AccountNames" /> are tried in order and the first one icacls accepts
/// receives <see cref="Permission" /> with object and container inheritance. A
/// <see cref="Required" /> grant fails the deployment; any other grant only logs a warning.
/// </summary>
internal sealed record AppPoolDirectoryGrant(
    string Path,
    string AppPoolName,
    IReadOnlyList<string> AccountNames,
    string Permission,
    bool Required);
