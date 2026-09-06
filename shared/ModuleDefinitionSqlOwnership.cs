using System.Text;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace OpenModulePlatform.ModuleDefinitions;

// Source-linked into all three executors: parsing and payload rules cannot drift.
internal static class ModuleDefinitionSqlOwnership
{
    internal const string RuleId = "OMP-MODULE-SQL-CONFIG-OWNERSHIP";
    internal sealed record Violation(string Table, int Line, int Column, string Reason)
    {
        public string Message => $"{RuleId}: Module definition SQL must not write {Table}; {Reason} (line {Line}, column {Column}).";
    }

    internal static string Decode(string? inlineSql, string? content, string? encoding)
    {
        var normalizedEncoding = encoding?.Trim().ToLowerInvariant();
        if (normalizedEncoding is not (null or "" or "utf8" or "utf-8" or "base64-utf8"))
        {
            throw new InvalidOperationException($"{RuleId}: Unknown module SQL content encoding.");
        }

        string? decoded = content;
        try
        {
            if (content is not null && normalizedEncoding == "base64-utf8")
            {
                decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(content));
            }
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new InvalidOperationException($"{RuleId}: Invalid base64-utf8 module SQL payload.", ex);
        }

        if (!string.IsNullOrWhiteSpace(inlineSql) && !string.IsNullOrWhiteSpace(decoded)
            && !string.Equals(inlineSql, decoded, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{RuleId}: Conflicting module SQL payload fields.");
        }

        var sql = !string.IsNullOrWhiteSpace(inlineSql) ? inlineSql : decoded;
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException($"{RuleId}: Missing module SQL payload.");
        }

        return sql;
    }

    internal static string? Validate(string sql) => Analyze(sql).FirstOrDefault()?.Message;

    internal static void ValidateDocument(string definitionJson)
    {
        var module = "<unresolved module>";
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            var root = document.RootElement;
            if (root.TryGetProperty("moduleKey", out var key) && key.ValueKind == JsonValueKind.String)
                module = key.GetString() ?? module;
            if (!root.TryGetProperty("sqlScripts", out var scripts)) return;
            if (scripts.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Invalid sqlScripts payload.");
            foreach (var script in scripts.EnumerateArray())
            {
                if (script.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid SQL script payload.");
                string? Text(string name) => script.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
                var scriptKey = Text("key");
                if (string.IsNullOrWhiteSpace(scriptKey)) throw new InvalidOperationException("Missing SQL script key.");
                var sql = Decode(Text("inlineSql"), Text("content"), Text("contentEncoding"));
                if (Validate(sql) is { } error) throw new InvalidOperationException($"Script '{scriptKey}': {error}");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{RuleId}: Module '{module}' SQL payload was blocked: {ex.Message}", ex);
        }
    }

    // Dynamic SQL payloads are re-parsed recursively (EXEC of a constant string that
    // itself EXECs another). Any legitimate payload is a handful of levels deep; a
    // payload nested deeper than this is treated as unresolvable rather than parsed
    // without bound, so a self-referencing or adversarial payload cannot exhaust the
    // stack of the importing process.
    private const int MaxDynamicSqlNestingDepth = 32;

    internal static IReadOnlyList<Violation> Analyze(string sql)
    {
        var violations = new ViolationList();
        Parse(sql, violations, 0);
        return violations.Items;
    }

    // Violations are reported in discovery order (Validate surfaces the first one) and
    // deduplicated, so the ordered list is paired with a set for O(1) membership instead
    // of a linear Contains per Add. Violation is a record: structural equality applies.
    private sealed class ViolationList
    {
        private readonly HashSet<Violation> seen = [];
        internal List<Violation> Items { get; } = [];
        internal void Add(Violation violation)
        {
            if (seen.Add(violation)) Items.Add(violation);
        }
    }

    private static void Parse(string sql, ViolationList violations, int depth, TSqlFragment? origin = null)
    {
        if (depth > MaxDynamicSqlNestingDepth || string.IsNullOrWhiteSpace(sql))
        {
            violations.Add(new("<unresolved table>", origin?.StartLine ?? 1, origin?.StartColumn ?? 1,
                "SQL payload cannot be resolved"));
            return;
        }

        // ScriptDom recognizes GO but not sqlcmd's repeat count. Tokenize first so
        // only batch separators are normalized, never strings or comments.
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var tokenReader = new StringReader(sql);
        var tokens = parser.GetTokenStream(tokenReader, out _);
        var normalized = sql.ToCharArray();
        TSqlParserToken? previous = null;
        foreach (var token in tokens.Where(static token =>
                     token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)))
        {
            if (previous?.TokenType == TSqlTokenType.Go && token.TokenType == TSqlTokenType.Integer && previous.Line == token.Line)
                Array.Fill(normalized, ' ', token.Offset, token.Text.Length);
            previous = token;
        }
        using var reader = new StringReader(new string(normalized));
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
        if (errors.Count != 0)
        {
            // Do not echo source excerpts: configuration payloads can contain sensitive values.
            violations.Add(new("<unresolved table>", origin?.StartLine ?? errors[0].Line,
                origin?.StartColumn ?? errors[0].Column, "SQL payload cannot be parsed"));
            return;
        }

        fragment.Accept(new OwnershipVisitor(violations, depth, origin));
    }

    private sealed class TableCollector : TSqlFragmentVisitor
    {
        internal List<NamedTableReference> Tables { get; } = [];
        public override void ExplicitVisit(NamedTableReference node) => Tables.Add(node);
    }

    private sealed class OwnershipVisitor(ViolationList violations, int depth, TSqlFragment? origin) : TSqlFragmentVisitor
    {
        private const string DynamicIdentifier = "__module_sql_identifier__";
        private VariableCollector variables = new();

        public override void ExplicitVisit(TSqlBatch node)
        {
            variables = new VariableCollector();
            node.Accept(variables);
            base.ExplicitVisit(node);
        }

        private sealed class VariableCollector : TSqlFragmentVisitor
        {
            internal Dictionary<string, List<ScalarExpression?>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
            private void Add(string name, ScalarExpression? value)
            {
                if (!Values.TryGetValue(name, out var values)) Values[name] = values = [];
                values.Add(value);
            }
            public override void ExplicitVisit(DeclareVariableElement node) => Add(node.VariableName.Value, node.Value);
            // Reassignment, output parameters and SELECT assignment make a declaration unknown.
            public override void ExplicitVisit(SetVariableStatement node) => Add(node.Variable.Name, null);
            public override void ExplicitVisit(SelectSetVariable node) => Add(node.Variable.Name, null);
            public override void ExplicitVisit(ExecuteParameter node)
            {
                if (node.IsOutput && node.ParameterValue is VariableReference variable) Add(variable.Name, null);
            }
        }

        private string? ConstantSql(ScalarExpression expression, HashSet<string> seen)
        {
            switch (expression)
            {
                case StringLiteral literal: return literal.Value;
                case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary:
                    var left = ConstantSql(binary.FirstExpression, new(seen, StringComparer.OrdinalIgnoreCase));
                    var right = ConstantSql(binary.SecondExpression, new(seen, StringComparer.OrdinalIgnoreCase));
                    return left is not null && right is not null ? left + right : null;
                case ParenthesisExpression parenthesis: return ConstantSql(parenthesis.Expression, seen);
                case VariableReference variable when seen.Add(variable.Name):
                    return variables.Values.TryGetValue(variable.Name, out var values) && values.Count == 1 && values[0] is { } value
                        ? ConstantSql(value, seen) : null;
                case FunctionCall function when function.CallTarget is null
                    && function.FunctionName.Value.Equals("QUOTENAME", StringComparison.OrdinalIgnoreCase)
                    && function.Parameters.Count == 1:
                    return "[" + DynamicIdentifier + "]";
                default: return null;
            }
        }

        private void CheckDynamic(string? sql, TSqlFragment node)
        {
            if (sql is null)
            {
                Add("<unresolved table>", node, "dynamic SQL payload cannot be resolved");
                return;
            }
            if (sql.Contains(DynamicIdentifier, StringComparison.Ordinal))
            {
                // An unknown identifier is permitted only as a quoted constraint name
                // in bounded DDL. Never infer a DML target from an unknown identifier.
                using var reader = new StringReader(sql);
                var fragment = new TSql170Parser(true).Parse(reader, out var errors);
                if (errors.Count != 0 || fragment is not TSqlScript { Batches.Count: 1 } script
                    || script.Batches[0].Statements.Count != 1
                    || script.Batches[0].Statements[0] is not AlterTableDropTableElementStatement drop
                    || drop.SchemaObjectName.Identifiers.Any(i => i.Value.Contains(DynamicIdentifier, StringComparison.Ordinal))
                    || drop.AlterTableDropTableElements.Any(e => e.TableElementType != TableElementType.Constraint))
                {
                    Add("<unresolved table>", node, "dynamic SQL payload cannot be resolved as bounded constraint maintenance");
                    return;
                }
            }
            Parse(sql, violations, depth + 1, origin ?? node);
        }
        private static readonly HashSet<string> OwnedTables = new(StringComparer.OrdinalIgnoreCase)
        {
            "ArtifactConfigurationFiles", "ConfigOverlayDocuments", "ConfigOverlayConfigurationFiles"
        };

        private void Add(string table, TSqlFragment node, string reason = "configuration continuity is owned by the platform")
        {
            var location = origin ?? node;
            violations.Add(new Violation(table, location.StartLine, location.StartColumn, reason));
        }

        private void CheckName(SchemaObjectName name, TSqlFragment location)
        {
            // HashSet.TryGetValue is deliberate, not a Dictionary habit: the set is
            // case-insensitive, and the out value is the platform's canonical spelling of
            // the table, so the violation names "omp.ConfigOverlayDocuments" however the
            // module SQL cased it - and casing variants deduplicate to one violation.
            if (OwnedTables.TryGetValue(name.BaseIdentifier.Value, out var canonicalTable)
                && (name.SchemaIdentifier is null || name.SchemaIdentifier.Value.Equals("omp", StringComparison.OrdinalIgnoreCase)))
            {
                Add("omp." + canonicalTable, location);
            }
        }

        private void CheckTarget(TableReference target, FromClause? from, WithCtesAndXmlNamespaces? with, TSqlFragment location)
        {
            if (target is VariableTableReference) return;
            if (target is not NamedTableReference named)
            {
                Add("<unresolved table>", location, "write target cannot be resolved");
                return;
            }

            var aliases = new TableCollector();
            from?.Accept(aliases);
            Resolve(named, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            void Resolve(NamedTableReference table, HashSet<string> seen)
            {
                var name = table.SchemaObject;
                CheckName(name, location);
                if (name.Identifiers.Count != 1 || !seen.Add(name.BaseIdentifier.Value)) return;
                var key = name.BaseIdentifier.Value;
                var cte = with?.CommonTableExpressions.FirstOrDefault(c => c.ExpressionName.Value.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (cte is not null)
                {
                    var sources = new TableCollector();
                    cte.QueryExpression.Accept(sources);
                    foreach (var source in sources.Tables) Resolve(source, seen);
                }
                foreach (var alias in aliases.Tables.Where(t => t.Alias?.Value.Equals(key, StringComparison.OrdinalIgnoreCase) == true))
                {
                    Resolve(alias, seen);
                }
            }
        }

        public override void ExplicitVisit(BulkInsertBase node)
        {
            CheckName(node.To, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BulkInsertStatement node) => ExplicitVisit((BulkInsertBase)node);
        public override void ExplicitVisit(InsertBulkStatement node) => ExplicitVisit((BulkInsertBase)node);

        public override void ExplicitVisit(TriggerStatementBody node)
        {
            if (node.TriggerObject.Name is { } target) CheckName(target, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node) => ExplicitVisit((TriggerStatementBody)node);
        public override void ExplicitVisit(AlterTriggerStatement node) => ExplicitVisit((TriggerStatementBody)node);
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => ExplicitVisit((TriggerStatementBody)node);

        public override void ExplicitVisit(EnableDisableTriggerStatement node)
        {
            if (node.TriggerObject.Name is { } target) CheckName(target, node);
            base.ExplicitVisit(node);
        }

        // CreateIndexStatement is deliberately not checked: core bootstrap creates
        // additive indexes through this same gate. Blocking CREATE INDEX (including
        // UNIQUE) requires moving bootstrap DDL to the compiled migration path first.
        public override void ExplicitVisit(AlterIndexStatement node)
        {
            CheckName(node.OnName, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropIndexStatement node)
        {
            foreach (var clause in node.DropIndexClauses)
            {
                switch (clause)
                {
                    case DropIndexClause modern:
                        CheckName(modern.Object, node);
                        break;
                    case BackwardsCompatibleDropIndexClause legacy:
                        // ChildObjectName exposes the table separately from the index.
                        CheckName(legacy.Index, node);
                        break;
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DbccStatement node)
        {
            var targetIndex = node.Command switch
            {
                DbccCommand.CleanTable => 1,
                DbccCommand.DBReindex or DbccCommand.CheckTable
                    or DbccCommand.CheckConstraints or DbccCommand.ShowStatistics => 0,
                // ScriptDom 180 also represents CHECKCONSTRAINTS as a free-form
                // command; its arguments still use the normal literal collection.
                DbccCommand.Free when string.Equals(node.DllName, "CHECKCONSTRAINTS", StringComparison.OrdinalIgnoreCase) => 0,
                _ => -1
            };
            // DBCC commands without a table target (such as CHECKDB and SHRINKFILE)
            // stay outside this object-ownership rule; it is not a general DBCC ban.
            if (targetIndex >= 0)
            {
                // As with sp_rename, only a literal object name is bounded. Even
                // initialized variables and numeric object IDs cannot resolve a table here.
                if (node.Literals.ElementAtOrDefault(targetIndex)?.Value is not StringLiteral literal)
                    CheckDynamic(null, node);
                else
                {
                    using var reader = new StringReader(literal.Value);
                    var name = new TSql170Parser(true).ParseSchemaObjectName(reader, out var errors);
                    if (errors.Count != 0 || name?.BaseIdentifier is null)
                        CheckDynamic(null, node);
                    else
                        CheckName(name, node);
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatisticsStatement node)
        {
            CheckName(node.SchemaObjectName, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateStatisticsStatement node)
        {
            CheckName(node.OnName, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableStatement node)
        {
            CheckName(node.SchemaObjectName, node);
            base.ExplicitVisit(node);
        }

        // ScriptDom dispatches ExplicitVisit to the concrete statement type, not
        // the abstract ALTER TABLE base. Route every family through one check.
        public override void ExplicitVisit(AlterTableAddTableElementStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableDropTableElementStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableAlterColumnStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableConstraintModificationStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableTriggerModificationStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableRebuildStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableSetStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableFileTableNamespaceStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableChangeTrackingModificationStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableAlterPartitionStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableAlterIndexStatement node) => ExplicitVisit((AlterTableStatement)node);
        public override void ExplicitVisit(AlterTableAddClusterByStatement node) => ExplicitVisit((AlterTableStatement)node);

        public override void ExplicitVisit(AlterTableSwitchStatement node)
        {
            // SWITCH modifies both tables, including a module-owned source
            // switched into a platform-owned destination.
            CheckName(node.TargetTable, node);
            ExplicitVisit((AlterTableStatement)node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            CheckTarget(node.InsertSpecification.Target, null, node.WithCtesAndXmlNamespaces, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            CheckTarget(node.UpdateSpecification.Target, node.UpdateSpecification.FromClause, node.WithCtesAndXmlNamespaces, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            CheckTarget(node.DeleteSpecification.Target, node.DeleteSpecification.FromClause, node.WithCtesAndXmlNamespaces, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            CheckTarget(node.MergeSpecification.Target, null, node.WithCtesAndXmlNamespaces, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(OutputIntoClause node)
        {
            CheckTarget(node.IntoTable, null, null, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is not null) CheckName(node.Into, node);
            base.ExplicitVisit(node);
        }

        private void CheckRename(ExecutableProcedureReference procedure, TSqlFragment location)
        {
            var argument = procedure.Parameters.FirstOrDefault(p => p.Variable?.Name.Equals("@objname", StringComparison.OrdinalIgnoreCase) == true)
                ?? procedure.Parameters.FirstOrDefault(p => p.Variable is null);
            // Object names passed to sp_rename are identifiers inside a literal,
            // not executable SQL. Variables remain unbounded even if initialized.
            if (argument?.ParameterValue is not StringLiteral literal)
            {
                CheckDynamic(null, location);
                return;
            }

            using var reader = new StringReader(literal.Value);
            var name = new TSql170Parser(true).ParseSchemaObjectName(reader, out var errors);
            if (errors.Count != 0 || name?.BaseIdentifier is null)
            {
                CheckDynamic(null, location);
                return;
            }

            CheckName(name, location);
            if (name.Identifiers.Count > 1)
            {
                // A column (or index) name adds one identifier after the table.
                // Let ScriptDom preserve quoted dots and escaped delimiters.
                name.Identifiers.RemoveAt(name.Identifiers.Count - 1);
                CheckName(name, location);
            }
        }

        public override void ExplicitVisit(ExecuteStatement node)
        {
            var executable = node.ExecuteSpecification.ExecutableEntity;
            if (executable is ExecutableStringList strings)
            {
                var parts = strings.Strings.Select(s => ConstantSql(s, new(StringComparer.OrdinalIgnoreCase))).ToList();
                CheckDynamic(parts.Any(p => p is null) ? null : string.Concat(parts), node);
            }
            else if (executable is ExecutableProcedureReference procedure)
            {
                var name = procedure.ProcedureReference.ProcedureReference?.Name;
                if (name is null)
                    Add("<unresolved table>", node, "procedure target cannot be resolved");
                else if (name.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                {
                    var statement = procedure.Parameters.FirstOrDefault(p => p.Variable?.Name.Equals("@stmt", StringComparison.OrdinalIgnoreCase) == true)
                        ?? procedure.Parameters.FirstOrDefault();
                    CheckDynamic(statement is null ? null : ConstantSql(statement.ParameterValue, new(StringComparer.OrdinalIgnoreCase)), node);
                }
                else if (name.BaseIdentifier.Value.Equals("sp_rename", StringComparison.OrdinalIgnoreCase))
                    CheckRename(procedure, node);
                else if (name.BaseIdentifier.Value.Equals("sp_updatestats", StringComparison.OrdinalIgnoreCase)
                    || name.BaseIdentifier.Value.Equals("sp_createstats", StringComparison.OrdinalIgnoreCase))
                    Add("<platform-owned tables>", node, "global statistics maintenance affects platform-owned tables");
            }
            else
                Add("<unresolved table>", node, "executable payload cannot be resolved");
        }
    }
}
