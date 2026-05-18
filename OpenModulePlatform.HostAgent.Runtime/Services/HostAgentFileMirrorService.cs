using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class HostAgentFileMirrorService
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly ILogger<HostAgentFileMirrorService> _logger;

    public HostAgentFileMirrorService(
        IOptionsMonitor<HostAgentSettings> settings,
        ILogger<HostAgentFileMirrorService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task MirrorConfiguredFilesAsync(CancellationToken cancellationToken)
    {
        var mirrors = _settings.CurrentValue.FileMirrors
            .Where(static mirror => mirror.IsEnabled)
            .ToArray();

        if (mirrors.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mirror.Validate();

            var sourcePath = Path.GetFullPath(mirror.SourcePath.Trim());
            var targetPath = Path.GetFullPath(mirror.TargetPath.Trim());
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException(
                    $"Configured file mirror source path was not found: '{sourcePath}'.");
            }

            ArtifactDirectoryMirror.MirrorDirectory(
                sourcePath,
                targetPath,
                mirror.ExcludedEntries,
                cancellationToken,
                mirror.DeleteStaleTargetEntries);

            _logger.LogInformation(
                "Mirrored configured files. SourcePath={SourcePath}, TargetPath={TargetPath}",
                sourcePath,
                targetPath);
        }

        return Task.CompletedTask;
    }
}
