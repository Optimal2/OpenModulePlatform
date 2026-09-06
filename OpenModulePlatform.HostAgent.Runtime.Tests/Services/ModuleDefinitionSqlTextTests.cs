using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class ModuleDefinitionSqlTextTests
{
    [Theory]
    [InlineData("SELECT 1;\nGO 12 -- repeat\nSELECT 2;", "SELECT 1;\nGO    -- repeat\nSELECT 2;")]
    [InlineData("PRINT N'\nGO 12\n';", "PRINT N'\nGO 12\n';")]
    [InlineData("/*\nGO 12\n*/\r\nSELECT 1;", "/*\nGO 12\n*/\r\nSELECT 1;")]
    [InlineData("SELECT 1;\r\nGO 2\r\nSELECT 2;", "SELECT 1;\r\nGO  \r\nSELECT 2;")]
    public void NormalizeBatchSeparators_PreservesLiteralsCommentsAndPositions(string sql, string expected)
    {
        Assert.Equal(expected, ModuleDefinitionSqlText.NormalizeBatchSeparators(sql));
    }

    [Fact]
    public void PortableHeader_PreservesDiagnosticLineAndColumn()
    {
        const string sql = "USE [OpenModulePlatform];\r\nGO\r\n  UPDATE omp.ConfigOverlayDocuments SET Id = 1;";
        var portable = ModuleDefinitionSqlText.RemovePortableDatabaseHeader(sql);
        Assert.Equal(sql.Length, portable.Length);
        Assert.Equal(sql.IndexOf("UPDATE", StringComparison.Ordinal), portable.IndexOf("UPDATE", StringComparison.Ordinal));
        var violation = Assert.Single(ModuleDefinitionSqlOwnership.Analyze(portable));
        Assert.Equal(3, violation.Line);
        Assert.Equal("omp.ConfigOverlayDocuments", violation.Table);
    }
}
