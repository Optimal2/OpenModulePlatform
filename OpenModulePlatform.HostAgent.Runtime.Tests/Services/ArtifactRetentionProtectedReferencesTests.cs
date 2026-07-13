// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/ArtifactRetentionProtectedReferencesTests.cs
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ArtifactRetentionProtectedReferencesTests
{
    [Fact]
    public void BuildProtectionClauses_NoReferences_ReturnsEmptyString()
    {
        var result = ArtifactRetentionProtectedReferences.BuildProtectionClauses([]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildProtectionClauses_SingleReference_RendersUnionClause()
    {
        var result = ArtifactRetentionProtectedReferences.BuildProtectionClauses(
            [("omp_ibs_packager", "ChannelTypeVersions", "ArtifactId")]);

        Assert.Contains("UNION ALL", result);
        Assert.Contains("FROM [omp_ibs_packager].[ChannelTypeVersions] extref", result);
        Assert.Contains("WHERE extref.[ArtifactId] = ar.ArtifactId", result);
    }

    [Fact]
    public void BuildProtectionClauses_MultipleReferences_RendersOneClausePerReference()
    {
        var result = ArtifactRetentionProtectedReferences.BuildProtectionClauses(
        [
            ("omp_module_a", "PinnedArtifacts", "ArtifactId"),
            ("omp_module_b", "Releases", "SourceArtifactId")
        ]);

        Assert.Contains("FROM [omp_module_a].[PinnedArtifacts] extref", result);
        Assert.Contains("FROM [omp_module_b].[Releases] extref", result);
        Assert.Contains("WHERE extref.[SourceArtifactId] = ar.ArtifactId", result);
        Assert.Equal(2, CountOccurrences(result, "UNION ALL"));
    }

    [Fact]
    public void QuoteIdentifier_EscapesClosingBrackets()
    {
        Assert.Equal("[weird]]name]", ArtifactRetentionProtectedReferences.QuoteIdentifier("weird]name"));
        Assert.Equal("[Plain]", ArtifactRetentionProtectedReferences.QuoteIdentifier("Plain"));
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
