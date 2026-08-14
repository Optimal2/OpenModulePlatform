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

        // One failing mirror must not take the others with it, and above all must not take
        // down the rest of the cycle. Mirroring runs before host jobs and telemetry, so a
        // source path on a temporarily unreachable UNC share used to stop job processing
        // and resource collection on every single tick -- a file copy nobody was waiting
        // for silently disabling the parts of the agent that report what is going on
        // (R7-D10).
        foreach (var mirror in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourcePath = Path.GetFullPath(mirror.SourcePath.Trim());
            var targetPath = Path.GetFullPath(mirror.TargetPath.Trim());

            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    _logger.LogWarning(
                        "Configured file mirror source path was not found; the mirror was skipped this cycle. SourcePath={SourcePath}, TargetPath={TargetPath}",
                        sourcePath,
                        targetPath);
                    continue;
                }

                ArtifactDirectoryMirror.MirrorDirectory(
                    sourcePath,
                    targetPath,
                    mirror.ExcludedEntries,
                    cancellationToken,
                    mirror.DeleteStaleTargetEntries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning(
                    ex,
                    "Configured file mirror failed and was skipped this cycle. SourcePath={SourcePath}, TargetPath={TargetPath}",
                    sourcePath,
                    targetPath);
                continue;
            }

            _logger.LogInformation(
                "Mirrored configured files. SourcePath={SourcePath}, TargetPath={TargetPath}",
                sourcePath,
                targetPath);
        }

        return Task.CompletedTask;
    }
}
