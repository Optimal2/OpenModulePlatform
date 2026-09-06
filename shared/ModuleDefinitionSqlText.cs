using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace OpenModulePlatform.ModuleDefinitions;

// Source-linked with the ownership parser so CLI and runtime text handling stay aligned.
internal static class ModuleDefinitionSqlText
{
    internal static string RemovePortableDatabaseHeader(string sql) => Regex.Replace(sql,
        @"(?im)^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
        match => new string(match.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray()));

    internal static string NormalizeBatchSeparators(string sql)
    {
        // ScriptDom recognizes GO but not sqlcmd's repeat count. Tokenize first so
        // only batch separators are normalized, never strings or comments.
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var tokens = parser.GetTokenStream(reader, out _);
        var normalized = sql.ToCharArray();
        TSqlParserToken? previous = null;
        foreach (var token in tokens.Where(static token =>
                     token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)))
        {
            if (previous?.TokenType == TSqlTokenType.Go && token.TokenType == TSqlTokenType.Integer && previous.Line == token.Line)
                Array.Fill(normalized, ' ', token.Offset, token.Text.Length);
            previous = token;
        }
        return new string(normalized);
    }
}
