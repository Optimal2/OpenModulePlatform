using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// How far the artifact catalog is from being able to run with
/// <c>HostAgent:RequireArtifactHash</c> enabled.
/// </summary>
/// <remarks>
/// R12-F12. The flag defaults to false and carries no value at all in the running
/// configuration, and the only signal about the artifacts that keep it off was a
/// per-artifact log line in <see cref="ArtifactProvisioner" /> written only while THAT
/// artifact is being provisioned. An artifact this host never provisions -- a superseded
/// version, an app deployed on another host -- was therefore completely invisible, so
/// nobody could know how far the installation was from being able to turn the flag on,
/// and a number nobody can see cannot be driven to zero.
///
/// Measured on LINUS-LAPTOP 2026-08-16: 29 of 372 enabled artifacts carry no Sha256, and
/// <b>none</b> of the 29 is referenced by an enabled app instance, worker instance or host
/// artifact requirement. The board assumed enabling the flag would refuse to provision the
/// 29; the measurement says it would refuse nothing, because all 29 are superseded rows.
/// That second number is the one that decides whether the flag can be flipped, which is
/// why it is counted separately instead of being inferred from the first.
///
/// The reference test is deliberately coarser than
/// <see cref="OmpHostArtifactRepository.GetDesiredArtifactsAsync" />: it asks whether the
/// artifact is wired to anything anywhere, not whether it is desired on this host. That
/// makes it a superset of every host's desired set, so a zero here is a safe answer for
/// every host, and it avoids a second copy of the desired-set resolution that would drift
/// away from the first (metod 4.6).
/// </remarks>
public sealed record ArtifactContentHashGap(
    int EnabledArtifactCount,
    int MissingHashCount,
    int MissingHashStillReferencedCount,
    IReadOnlyList<string> MissingHashSamples);

public sealed partial class OmpHostArtifactRepository
{
    private const int ArtifactContentHashGapSampleCount = 10;

    /// <summary>
    /// Counts the enabled artifacts that carry no content hash, and how many of those are
    /// still referenced by something that could ask for them to be provisioned.
    /// </summary>
    public async Task<ArtifactContentHashGap> GetArtifactContentHashGapAsync(CancellationToken ct)
    {
        const string sql = @"
SELECT
    (
        SELECT COUNT(1)
        FROM omp.Artifacts
        WHERE IsEnabled = 1
    ) AS EnabledArtifactCount,
    (
        SELECT COUNT(1)
        FROM omp.Artifacts
        WHERE IsEnabled = 1
          AND (Sha256 IS NULL OR LTRIM(RTRIM(Sha256)) = N'')
    ) AS MissingHashCount,
    (
        SELECT COUNT(1)
        FROM omp.Artifacts ar
        WHERE ar.IsEnabled = 1
          AND (ar.Sha256 IS NULL OR LTRIM(RTRIM(ar.Sha256)) = N'')
          AND
          (
              EXISTS (SELECT 1 FROM omp.AppInstances ai WHERE ai.ArtifactId = ar.ArtifactId AND ai.IsEnabled = 1)
              OR EXISTS (SELECT 1 FROM omp.WorkerInstances wi WHERE wi.ArtifactId = ar.ArtifactId AND wi.IsEnabled = 1)
              OR EXISTS (SELECT 1 FROM omp.HostArtifactRequirements hr WHERE hr.ArtifactId = ar.ArtifactId AND hr.IsEnabled = 1)
          )
    ) AS MissingHashStillReferencedCount;

SELECT TOP (@sampleCount)
    CONCAT(app.AppKey, N' ', ar.Version, N' (', ar.PackageType, N'/', ISNULL(ar.TargetName, N'-'), N')') AS Descriptor
FROM omp.Artifacts ar
INNER JOIN omp.Apps app ON app.AppId = ar.AppId
WHERE ar.IsEnabled = 1
  AND (ar.Sha256 IS NULL OR LTRIM(RTRIM(ar.Sha256)) = N'')
ORDER BY app.AppKey, ar.Version, ar.ArtifactId;";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@sampleCount", System.Data.SqlDbType.Int).Value = ArtifactContentHashGapSampleCount;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var enabledCount = 0;
        var missingCount = 0;
        var referencedCount = 0;
        if (await reader.ReadAsync(ct))
        {
            enabledCount = reader.GetInt32(0);
            missingCount = reader.GetInt32(1);
            referencedCount = reader.GetInt32(2);
        }

        var samples = new List<string>(ArtifactContentHashGapSampleCount);
        if (await reader.NextResultAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                {
                    samples.Add(reader.GetString(0));
                }
            }
        }

        return new ArtifactContentHashGap(enabledCount, missingCount, referencedCount, samples);
    }
}
