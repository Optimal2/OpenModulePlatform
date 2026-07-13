// File: OpenModulePlatform.HostAgent.Runtime/Services/ArtifactRetentionProtectedReferences.cs
using System.Text;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Discovers foreign keys that reference <c>omp.Artifacts(ArtifactId)</c> from tables the
/// retention SQL does not already handle (typically module-owned schemas such as
/// <c>omp_ibs_packager</c>), and renders them as extra protected-reference clauses.
/// </summary>
/// <remarks>
/// Without this, a module table pinning an artifact makes the retention DELETE fail on the
/// foreign key and the whole cleanup transaction rolls back. Modules can therefore protect
/// artifacts simply by referencing them with a foreign key; no core registration is needed.
/// The Portal maintenance preview duplicates this logic in
/// <c>OpenModulePlatform.Portal/Services/OmpAdminRepository.Maintenance.cs</c> so the preview
/// and the HostAgent job always agree on what is deletable.
/// </remarks>
internal static class ArtifactRetentionProtectedReferences
{
    /// <summary>
    /// Marker inside the retention SQL templates that is replaced with the generated clauses.
    /// </summary>
    public const string SqlMarker = "/*EXTERNAL_ARTIFACT_REFERENCES*/";

    /// <summary>
    /// Finds referencing schema/table/column triplets for every foreign key that points at
    /// <c>omp.Artifacts(ArtifactId)</c>, excluding the core tables the retention batch already
    /// handles itself (they either protect conditionally or are deleted by the cleanup).
    /// </summary>
    public const string DiscoverySql = @"
SELECT s.name AS SchemaName,
       t.name AS TableName,
       c.name AS ColumnName
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.tables t ON t.object_id = fk.parent_object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
INNER JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE rs.name = N'omp'
  AND rt.name = N'Artifacts'
  AND rc.name = N'ArtifactId'
  AND NOT
  (
      s.name = N'omp'
      AND t.name IN
      (
          N'AppInstances',
          N'WorkerInstances',
          N'InstanceTemplateAppInstances',
          N'HostArtifactRequirements',
          N'HostAgentDesiredStates',
          N'HostAppDeploymentStates',
          N'HostAgentRuntimeStates',
          N'HostArtifactStates',
          N'ArtifactConfigurationFiles'
      )
  )
ORDER BY s.name, t.name, c.name;";

    /// <summary>
    /// Renders UNION ALL clauses matching the protected-reference subquery shape. Returns an
    /// empty string when no external references exist so the templates work unchanged.
    /// </summary>
    public static string BuildProtectionClauses(
        IReadOnlyList<(string SchemaName, string TableName, string ColumnName)> references)
    {
        if (references.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var reference in references)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("            UNION ALL");
            builder.AppendLine();
            builder.AppendLine("            SELECT 1");
            builder.AppendLine($"            FROM {QuoteIdentifier(reference.SchemaName)}.{QuoteIdentifier(reference.TableName)} extref");
            builder.Append($"            WHERE extref.{QuoteIdentifier(reference.ColumnName)} = ar.ArtifactId");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Brackets an identifier from the system catalog so it is always safe to embed in SQL.
    /// </summary>
    public static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";
}
