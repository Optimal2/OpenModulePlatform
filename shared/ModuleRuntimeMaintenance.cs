using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace OpenModulePlatform.ModuleDefinitions;

/// <summary>
/// Parses and validates the optional <c>runtimeMaintenance</c> section of a module definition:
/// versioned SQL steps a module declares for platform events that touch rows the module's own
/// tables reference (a host, an artifact or an app instance being removed). The platform runs the
/// declared steps generically; it never knows a module's tables. See docs/MODULE_DEFINITIONS.md,
/// "Runtime maintenance steps".
/// </summary>
/// <remarks>
/// Source-linked into HostAgent, Portal and Bootstrapper next to
/// <see cref="ModuleDefinitionSqlOwnership"/>, so every import gate and both executors apply one
/// contract. Step SQL is checked against an allow-list grammar over the parsed T-SQL; anything the
/// grammar does not name is rejected.
/// </remarks>
internal static class ModuleRuntimeMaintenance
{
    internal const string RuleId = "OMP-MODULE-RUNTIME-MAINTENANCE";
    internal const string SectionName = "runtimeMaintenance";

    internal const string HostRemoved = "host-removed";
    internal const string ArtifactRemoved = "artifact-removed";
    internal const string AppInstanceRemoved = "app-instance-removed";
    internal const string AppInstanceBlockingCount = "app-instance-blocking-count";

    internal const string IdempotentExecution = "idempotent";
    internal const string ReadOnlyExecution = "read-only";

    /// <summary>The one parameter the platform binds for an event, and the execution it expects.</summary>
    internal sealed record EventContract(string Name, string ParameterName, string ParameterSqlType, string Execution);

    internal static readonly IReadOnlyDictionary<string, EventContract> Events =
        new Dictionary<string, EventContract>(StringComparer.Ordinal)
        {
            [HostRemoved] = new(HostRemoved, "@HostId", "uniqueidentifier", IdempotentExecution),
            [ArtifactRemoved] = new(ArtifactRemoved, "@ArtifactId", "int", IdempotentExecution),
            [AppInstanceRemoved] = new(AppInstanceRemoved, "@AppInstanceId", "uniqueidentifier", IdempotentExecution),
            [AppInstanceBlockingCount] = new(AppInstanceBlockingCount, "@AppInstanceId", "uniqueidentifier", ReadOnlyExecution),
        };

    internal sealed record Step(string ModuleKey, string SchemaName, string Key, string Event, int Order, string Sql);

    private static readonly HashSet<string> SectionProperties = new(StringComparer.Ordinal) { "steps", "description" };

    private static readonly HashSet<string> StepProperties = new(StringComparer.Ordinal)
    {
        "key", "event", "order", "execution", "description", "path", "source",
        "inlineSql", "contentEncoding", "content", "sha256",
    };

    // Schemas of the modules the platform ships. A module key that would derive one of them
    // (moduleKey "portal" derives omp_portal) is refused, so no definition can claim them.
    private static readonly HashSet<string> PlatformModuleSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "omp_core", "omp_portal", "omp_auth", "omp_content", "omp_iframe",
    };

    private static readonly Regex ModuleKeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The only schema a module's runtime maintenance steps may write: <c>omp_&lt;moduleKey&gt;</c>.
    /// The platform derives it from the module key; <c>module.schemaName</c> must equal it and
    /// cannot widen it. The executor also requires omp.Modules to register this schema for the
    /// module, and for no other module.
    /// </summary>
    internal static string AllowedSchemaFor(string moduleKey) => "omp_" + moduleKey;

    internal const string ModuleKeyCaseRuleId = "OMP-MODULE-KEY-CASE";

    /// <summary>
    /// T-SQL that throws when <paramref name="moduleKeyExpression"/> differs only in letter case
    /// from a module key already in omp.Modules or omp.ModuleDefinitionDocuments. Two such keys
    /// derive the same physical schema (SQL Server schema names follow the database collation),
    /// and under a case-insensitive collation the registration MERGE would update the other
    /// module's row. Every import and registration path runs it before it writes.
    /// </summary>
    /// <param name="moduleKeyExpression">A T-SQL variable or parameter holding the incoming key.</param>
    /// <param name="excludeModuleIdExpression">When editing an existing module row, its id, so the row does not collide with itself.</param>
    internal static string ModuleKeyCaseGuardSql(string moduleKeyExpression, string? excludeModuleIdExpression = null)
    {
        var excludeSelf = excludeModuleIdExpression is null ? string.Empty : $" AND existing.ModuleId <> {excludeModuleIdExpression}";
        // Each table is probed only when it exists, so a database that predates omp.Modules still
        // imports; deferred name resolution never compiles the skipped statement.
        return $@"
DECLARE @OmpModuleKeyCaseConflict bit = 0;
IF OBJECT_ID(N'omp.Modules', N'U') IS NOT NULL
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM omp.Modules existing
        WHERE UPPER(existing.ModuleKey) = UPPER({moduleKeyExpression})
          AND CONVERT(varbinary(400), existing.ModuleKey) <> CONVERT(varbinary(400), {moduleKeyExpression}){excludeSelf}
    )
        SET @OmpModuleKeyCaseConflict = 1;
END;
IF OBJECT_ID(N'omp.ModuleDefinitionDocuments', N'U') IS NOT NULL
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM omp.ModuleDefinitionDocuments existing
        WHERE UPPER(existing.ModuleKey) = UPPER({moduleKeyExpression})
          AND CONVERT(varbinary(400), existing.ModuleKey) <> CONVERT(varbinary(400), {moduleKeyExpression})
    )
        SET @OmpModuleKeyCaseConflict = 1;
END;
IF @OmpModuleKeyCaseConflict = 1
BEGIN
    DECLARE @OmpModuleKeyCaseMessage nvarchar(2048) = CONCAT(
        N'{ModuleKeyCaseRuleId}: Module key ''', {moduleKeyExpression},
        N''' differs only in letter case from a module key that is already registered or imported; both would derive the same schema. Use the existing spelling.');
    THROW 53240, @OmpModuleKeyCaseMessage, 1;
END;
";
    }

    internal static void ValidateDocument(string definitionJson) => _ = ReadSteps(definitionJson);

    /// <summary>
    /// Returns the validated steps of one definition document, ordered by <c>order</c> then
    /// <c>key</c>. A document without the section returns an empty list; an invalid section throws
    /// <see cref="InvalidOperationException"/> naming the module, the step and the rule.
    /// </summary>
    internal static IReadOnlyList<Step> ReadSteps(string definitionJson)
    {
        var module = "<unresolved module>";
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The module definition must be a JSON object.");
            // A repeated property is read differently by System.Text.Json (last wins) and by SQL
            // JSON_VALUE (first wins), so the properties this contract binds must be unique.
            RejectDuplicateProperties(root, "The module definition", "moduleKey", "module", SectionName);
            if (Text(root, "moduleKey") is { Length: > 0 } key) module = key;
            if (!root.TryGetProperty(SectionName, out var section) || section.ValueKind == JsonValueKind.Null) return [];
            if (section.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"'{SectionName}' must be an object.");
            RejectUnknownProperties(section, SectionProperties, SectionName);
            RejectDuplicateProperties(section, SectionName);
            if (module == "<unresolved module>") throw new InvalidOperationException("A definition with runtime maintenance steps must declare moduleKey.");
            if (!ModuleKeyPattern.IsMatch(module))
                throw new InvalidOperationException("A definition with runtime maintenance steps must have a moduleKey of letters, digits and underscores that starts with a letter.");

            // The writable schema is derived by the platform, never taken from the document.
            var schemaName = AllowedSchemaFor(module);
            if (PlatformModuleSchemas.Contains(schemaName))
                throw new InvalidOperationException($"Module key '{module}' derives the platform schema '{schemaName}'; runtime maintenance steps cannot write platform schemas.");
            if (!root.TryGetProperty("module", out var moduleNode) || moduleNode.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"A definition with runtime maintenance steps must declare module.schemaName '{schemaName}'.");
            RejectDuplicateProperties(moduleNode, "module", "schemaName");
            var declaredSchema = Text(moduleNode, "schemaName");
            if (!string.Equals(declaredSchema, schemaName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"module.schemaName is '{declaredSchema}', but the platform derives '{schemaName}' from module key '{module}'; runtime maintenance steps may only write the derived schema.");

            if (!section.TryGetProperty("steps", out var stepsNode) || stepsNode.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"'{SectionName}.steps' must be an array.");

            var steps = new List<Step>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stepNode in stepsNode.EnumerateArray())
            {
                if (stepNode.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Each runtime maintenance step must be an object.");
                var stepKey = Text(stepNode, "key");
                if (string.IsNullOrWhiteSpace(stepKey)) throw new InvalidOperationException("A runtime maintenance step is missing key.");
                RejectUnknownProperties(stepNode, StepProperties, $"step '{stepKey}'");
                RejectDuplicateProperties(stepNode, $"step '{stepKey}'");
                if (!keys.Add(stepKey)) throw new InvalidOperationException($"Step key '{stepKey}' is declared more than once.");

                var eventName = Text(stepNode, "event");
                if (eventName is null || !Events.TryGetValue(eventName, out var contract))
                    throw new InvalidOperationException($"Step '{stepKey}' has unknown event '{eventName}'. Known events: {string.Join(", ", Events.Keys)}.");

                var execution = Text(stepNode, "execution");
                if (!string.Equals(execution, contract.Execution, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Step '{stepKey}' must declare execution '{contract.Execution}' for event '{eventName}'.");

                var order = 0;
                if (stepNode.TryGetProperty("order", out var orderNode) && orderNode.ValueKind != JsonValueKind.Null
                    && (orderNode.ValueKind != JsonValueKind.Number || !orderNode.TryGetInt32(out order)))
                    throw new InvalidOperationException($"Step '{stepKey}' has a non-integer order.");

                var sql = ModuleDefinitionSqlOwnership.Decode(Text(stepNode, "inlineSql"), Text(stepNode, "content"), Text(stepNode, "contentEncoding"));
                if (ValidateStepSql(sql, schemaName, contract) is { } error)
                    throw new InvalidOperationException($"Step '{stepKey}' ({eventName}): {error}");

                steps.Add(new Step(module, schemaName, stepKey, eventName, order, sql));
            }

            return steps
                .OrderBy(static step => step.Order)
                .ThenBy(static step => step.Key, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{RuleId}: Module '{module}' runtime maintenance was blocked: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns the first reason the step SQL is not one of the allowed forms for
    /// <paramref name="contract"/>, or null.
    /// </summary>
    /// <remarks>
    /// An allow-list over the parsed T-SQL, not a deny-list: every statement, clause and expression
    /// must match one of the forms in docs/MODULE_DEFINITIONS.md, "Runtime maintenance step
    /// grammar", and anything the grammar does not name is refused. A second pass refuses any
    /// syntax node whose type is outside the grammar's node set, so a clause the structural pass
    /// forgot to inspect cannot carry SQL through.
    /// </remarks>
    internal static string? ValidateStepSql(string sql, string moduleSchema, EventContract contract)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count != 0)
            return $"SQL cannot be parsed (line {errors[0].Line}, column {errors[0].Column}).";
        if (fragment is not TSqlScript { Batches.Count: 1 } script)
            return "SQL must be exactly one batch; GO separators are not allowed.";

        // A parameter name in a comment reads like a binding to a reviewer but binds nothing.
        foreach (var token in script.ScriptTokenStream)
        {
            if (token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment
                && token.Text.Contains('@', StringComparison.Ordinal))
                return $"Comments must not contain '@' (line {token.Line}, column {token.Column}).";
        }

        if (ModuleDefinitionSqlOwnership.Validate(sql) is { } ownership) return ownership;

        if (new StepGrammar(moduleSchema, contract).Validate(script.Batches[0]) is { } error) return error;

        var nodes = new GrammarNodeCheck();
        script.Accept(nodes);
        return nodes.Error;
    }

    private static void RejectUnknownProperties(JsonElement element, HashSet<string> allowed, string owner)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{owner} has unknown property '{property.Name}'.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string owner, params string[] only)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (only.Length != 0 && !only.Contains(property.Name, StringComparer.Ordinal)) continue;
            if (!seen.Add(property.Name))
                throw new InvalidOperationException($"{owner} declares property '{property.Name}' more than once.");
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    /// <summary>
    /// The step grammar (docs/MODULE_DEFINITIONS.md, "Runtime maintenance step grammar"):
    /// <code>
    /// step      := guard+
    /// guard     := IF OBJECT_ID(N'schema.table', N'U') IS NOT NULL body      -- no ELSE
    /// body      := statement | BEGIN statement* END
    /// statement := guard | delete | update | count
    /// delete    := DELETE [FROM] schema.table WHERE where
    /// update    := UPDATE schema.table SET column = value [, column = value]* WHERE where
    /// count     := SELECT COUNT(*) AS BlockingCount [, N'text' AS Description]
    ///              FROM schema.table WHERE where                              -- read-only event only
    /// where     := conjunct [AND conjunct]*, at least one binding conjunct
    /// binding   := column = @Param | column IN (SELECT a.column FROM schema.table2 a WHERE inner)
    /// inner     := like where, columns qualified by a, no further subquery
    /// filter    := operand {= | &lt;&gt; | &lt; | &gt; | &lt;= | &gt;=} operand | column IS [NOT] NULL | column IN (constant, ...)
    /// operand   := column | constant
    /// value     := constant
    /// constant  := number | -number | N'text' | 'text' | NULL | GETUTCDATE() | SYSUTCDATETIME()
    /// </code>
    /// schema is the module schema; every table must be named by an enclosing guard.
    /// </summary>
    private sealed class StepGrammar(string moduleSchema, EventContract contract)
    {
        private readonly List<string> guards = [];
        private string targetName = string.Empty;
        private int writes;
        private int selects;
        private string? error;

        private string Param => contract.ParameterName;

        internal string? Validate(TSqlBatch batch)
        {
            foreach (var statement in batch.Statements) Statement(statement);
            if (error is not null) return error;
            if (contract.Execution == ReadOnlyExecution)
            {
                return selects == 1
                    ? null
                    : $"Event '{contract.Name}' is read-only; the step must be exactly one SELECT COUNT(*) AS BlockingCount (found {selects}).";
            }

            return writes == 0 ? "The step must contain at least one DELETE or UPDATE." : null;
        }

        private bool Fail(TSqlFragment node, string message)
        {
            error ??= $"{message} (line {node.StartLine}, column {node.StartColumn}).";
            return false;
        }

        private static bool Is<T>(TSqlFragment? node) => node is not null && node.GetType() == typeof(T);

        private void Statement(TSqlStatement statement)
        {
            if (error is not null) return;
            if (Is<IfStatement>(statement))
            {
                Guard((IfStatement)statement);
                return;
            }

            if (guards.Count == 0)
            {
                Fail(statement, $"{statement.GetType().Name} is not allowed at the top level; a step is IF OBJECT_ID(N'{moduleSchema}.<table>', N'U') IS NOT NULL around DELETE, UPDATE or SELECT COUNT(*) AS BlockingCount");
                return;
            }

            if (Is<BeginEndBlockStatement>(statement))
            {
                foreach (var inner in ((BeginEndBlockStatement)statement).StatementList.Statements) Statement(inner);
            }
            else if (Is<DeleteStatement>(statement))
            {
                Delete((DeleteStatement)statement);
            }
            else if (Is<UpdateStatement>(statement))
            {
                Update((UpdateStatement)statement);
            }
            else if (Is<SelectStatement>(statement))
            {
                Select((SelectStatement)statement);
            }
            else
            {
                Fail(statement, $"Statement {statement.GetType().Name} is not allowed in a runtime maintenance step; the allowed statements are IF OBJECT_ID guards, BEGIN/END, DELETE, UPDATE and SELECT COUNT(*) AS BlockingCount");
            }
        }

        private void Guard(IfStatement node)
        {
            if (node.ElseStatement is not null)
            {
                Fail(node.ElseStatement, "IF ... ELSE is not allowed; a guard has no ELSE branch");
                return;
            }

            if (GuardedTable(node.Predicate) is not { } table)
            {
                Fail(node.Predicate, $"The IF condition must be exactly OBJECT_ID(N'{moduleSchema}.<table>', N'U') IS NOT NULL");
                return;
            }

            guards.Add(table);
            Statement(node.ThenStatement);
            guards.RemoveAt(guards.Count - 1);
        }

        private string? GuardedTable(BooleanExpression predicate)
        {
            if (predicate is not BooleanIsNullExpression { IsNot: true, Expression: FunctionCall call } || !Is<BooleanIsNullExpression>(predicate)) return null;
            if (!IsPlainCall(call, "OBJECT_ID") || call.Parameters.Count != 2) return null;
            if (call.Parameters[0] is not StringLiteral name || call.Parameters[1] is not StringLiteral kind) return null;
            if (!kind.Value.Equals("U", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = name.Value.Split('.');
            if (parts.Length != 2) return null;
            var schema = Unquote(parts[0]);
            var table = Unquote(parts[1]);
            if (table.Length == 0 || !schema.Equals(moduleSchema, StringComparison.OrdinalIgnoreCase)) return null;
            return $"{schema}.{table}";
        }

        private static string Unquote(string part)
        {
            var value = part.Trim();
            if (value.Length >= 2 && ((value[0] == '[' && value[^1] == ']') || (value[0] == '"' && value[^1] == '"')))
                value = value[1..^1];
            return value;
        }

        private void Delete(DeleteStatement statement)
        {
            writes++;
            if (!Writable(statement) || !StatementExtras(statement, statement.WithCtesAndXmlNamespaces, statement.OptimizerHints)) return;
            var spec = statement.DeleteSpecification;
            if (!WriteSpecification(spec, spec.FromClause)) return;
            Where(spec.WhereClause, spec);
        }

        private void Update(UpdateStatement statement)
        {
            writes++;
            if (!Writable(statement) || !StatementExtras(statement, statement.WithCtesAndXmlNamespaces, statement.OptimizerHints)) return;
            var spec = statement.UpdateSpecification;
            if (!WriteSpecification(spec, spec.FromClause)) return;
            foreach (var clause in spec.SetClauses)
            {
                if (!Is<AssignmentSetClause>(clause)
                    || clause is not AssignmentSetClause { Variable: null, Column: { } column, AssignmentKind: AssignmentKind.Equals } assignment
                    || !IsColumn(column, qualifier: null))
                {
                    Fail(clause, "SET must assign an unqualified column of the target table with '='; assigning variables is not allowed");
                    return;
                }

                if (!IsConstant(assignment.NewValue))
                {
                    Fail(assignment.NewValue, "A SET value must be a constant, NULL, GETUTCDATE() or SYSUTCDATETIME()");
                    return;
                }
            }

            Where(spec.WhereClause, spec);
        }

        private bool Writable(TSqlStatement statement)
            => contract.Execution != ReadOnlyExecution
                || Fail(statement, $"Event '{contract.Name}' is read-only; the step must not modify data");

        private bool StatementExtras(TSqlStatement statement, WithCtesAndXmlNamespaces? ctes, IList<OptimizerHint> hints)
        {
            if (ctes is not null) return Fail(statement, "Common table expressions (WITH) are not allowed");
            if (hints.Count != 0) return Fail(statement, "OPTION hints are not allowed");
            return true;
        }

        private bool WriteSpecification(UpdateDeleteSpecificationBase spec, FromClause? from)
        {
            if (spec.TopRowFilter is not null) return Fail(spec, "TOP is not allowed");
            if (spec.OutputClause is not null || spec.OutputIntoClause is not null) return Fail(spec, "OUTPUT is not allowed");
            if (from is not null) return Fail(from, $"A FROM clause or join on the write is not allowed; restrict the rows with WHERE column = {Param} or column IN (subquery)");
            if (!Is<NamedTableReference>(spec.Target))
                return Fail(spec.Target, $"The write target must be a table in the module schema '{moduleSchema}'");
            return OwnTable((NamedTableReference)spec.Target, aliasAllowed: false, isTarget: true);
        }

        private void Select(SelectStatement statement)
        {
            selects++;
            if (contract.Execution != ReadOnlyExecution)
            {
                Fail(statement, $"SELECT is only allowed in the read-only event '{AppInstanceBlockingCount}'; event '{contract.Name}' allows DELETE and UPDATE");
                return;
            }

            if (!StatementExtras(statement, statement.WithCtesAndXmlNamespaces, statement.OptimizerHints)) return;
            if (statement.Into is not null || statement.On is not null || statement.ComputeClauses.Count != 0)
            {
                Fail(statement, "SELECT INTO and COMPUTE are not allowed");
                return;
            }

            if (!Is<QuerySpecification>(statement.QueryExpression))
            {
                Fail(statement.QueryExpression, "The read-only step must be one SELECT COUNT(*) AS BlockingCount FROM <table> WHERE ...");
                return;
            }

            var spec = (QuerySpecification)statement.QueryExpression;
            if (!PlainQuery(spec) || !SingleTable(spec, aliasAllowed: false, isTarget: true, out _)) return;

            var elements = spec.SelectElements;
            if (elements.Count is < 1 or > 2
                || !IsNamed(elements[0], "BlockingCount", out var count) || !IsCountStar(count)
                || (elements.Count == 2 && !(IsNamed(elements[1], "Description", out var description) && Is<StringLiteral>(description))))
            {
                Fail(spec, "The read-only step must select exactly COUNT(*) AS BlockingCount, optionally followed by N'text' AS Description");
                return;
            }

            Where(spec.WhereClause, spec);
        }

        private bool PlainQuery(QuerySpecification spec)
        {
            if (spec.UniqueRowFilter != UniqueRowFilter.NotSpecified || spec.TopRowFilter is not null
                || spec.GroupByClause is not null || spec.HavingClause is not null || spec.OrderByClause is not null
                || spec.OffsetClause is not null || spec.ForClause is not null)
                return Fail(spec, "DISTINCT, TOP, GROUP BY, HAVING, ORDER BY, OFFSET and FOR are not allowed");
            return true;
        }

        private bool SingleTable(QuerySpecification spec, bool aliasAllowed, bool isTarget, out NamedTableReference table)
        {
            table = null!;
            if (spec.FromClause is not { TableReferences.Count: 1 } from || !Is<NamedTableReference>(from.TableReferences[0]))
                return Fail(spec, $"The query must read exactly one table of the module schema '{moduleSchema}'; joins are not allowed");
            table = (NamedTableReference)from.TableReferences[0];
            return OwnTable(table, aliasAllowed, isTarget);
        }

        private bool OwnTable(NamedTableReference table, bool aliasAllowed, bool isTarget)
        {
            var name = table.SchemaObject;
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
                return Fail(table, "Cross-database and linked-server references are not allowed");
            if (name.SchemaIdentifier is null)
                return Fail(table, $"Table '{name.BaseIdentifier.Value}' must be qualified with the module schema '{moduleSchema}'");
            var qualified = $"{name.SchemaIdentifier.Value}.{name.BaseIdentifier.Value}";
            if (!name.SchemaIdentifier.Value.Equals(moduleSchema, StringComparison.OrdinalIgnoreCase))
                return Fail(table, $"References {qualified}; runtime maintenance steps may only reference the module schema '{moduleSchema}'");
            if (table.TableHints.Count != 0 || table.TableSampleClause is not null || table.TemporalClause is not null)
                return Fail(table, "Table hints, TABLESAMPLE and FOR SYSTEM_TIME are not allowed");
            if (table.Alias is not null && !aliasAllowed)
                return Fail(table, "The written or counted table must not have an alias");
            if (!guards.Any(guard => guard.Equals(qualified, StringComparison.OrdinalIgnoreCase)))
                return Fail(table, $"{qualified} must be referenced inside IF OBJECT_ID(N'{qualified}', N'U') IS NOT NULL");
            if (isTarget) targetName = name.BaseIdentifier.Value;
            return true;
        }

        private void Where(WhereClause? where, TSqlFragment owner)
        {
            if (where is null)
            {
                Fail(owner, $"The statement must have a WHERE clause with column = {Param}");
                return;
            }

            if (where.Cursor is not null)
            {
                Fail(where, "WHERE CURRENT OF is not allowed");
                return;
            }

            Restricts(where.SearchCondition, qualifier: null);
        }

        // qualifier null: the outer WHERE, where the only table in scope is the target and columns
        // are unqualified. Otherwise the IN subquery, whose columns must name its own table, so a
        // column missing from the inner table cannot silently resolve to the outer one.
        private bool Restricts(BooleanExpression condition, string? qualifier)
        {
            var conjuncts = new List<BooleanExpression>();
            if (!Flatten(condition, conjuncts)) return false;
            var bound = false;
            foreach (var conjunct in conjuncts)
            {
                switch (Conjunct(conjunct, qualifier))
                {
                    case null:
                        return false;
                    case true:
                        bound = true;
                        break;
                }
            }

            return bound || Fail(condition, qualifier is null
                ? $"The WHERE clause must restrict the rows by the event parameter: column = {Param} or column IN (SELECT a.column FROM {moduleSchema}.<table> a WHERE a.column = {Param}), combined with AND"
                : $"The subquery WHERE clause must contain {qualifier}.column = {Param}, combined with AND");
        }

        private bool Flatten(BooleanExpression expression, List<BooleanExpression> into)
        {
            switch (expression)
            {
                case BooleanParenthesisExpression parenthesis when Is<BooleanParenthesisExpression>(parenthesis):
                    return Flatten(parenthesis.Expression, into);
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } conjunction when Is<BooleanBinaryExpression>(conjunction):
                    return Flatten(conjunction.FirstExpression, into) && Flatten(conjunction.SecondExpression, into);
                case BooleanBinaryExpression other:
                    return Fail(other, $"{other.BinaryExpressionType} is not allowed in a runtime maintenance WHERE clause; combine conditions with AND");
                default:
                    into.Add(expression);
                    return true;
            }
        }

        // true: binds the rows to the parameter; false: an allowed filter; null: refused.
        private bool? Conjunct(BooleanExpression condition, string? qualifier)
        {
            switch (condition)
            {
                case BooleanComparisonExpression comparison when Is<BooleanComparisonExpression>(comparison):
                    if (comparison.ComparisonType == BooleanComparisonType.Equals
                        && ((IsColumn(comparison.FirstExpression, qualifier) && IsParameter(comparison.SecondExpression))
                            || (IsColumn(comparison.SecondExpression, qualifier) && IsParameter(comparison.FirstExpression))))
                        return true;
                    if (comparison.ComparisonType is BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets
                            or BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.LessThan
                            or BooleanComparisonType.GreaterThan or BooleanComparisonType.LessThanOrEqualTo
                            or BooleanComparisonType.GreaterThanOrEqualTo
                        && IsOperand(comparison.FirstExpression, qualifier) && IsOperand(comparison.SecondExpression, qualifier))
                        return false;
                    break;
                case BooleanIsNullExpression isNull when Is<BooleanIsNullExpression>(isNull) && IsColumn(isNull.Expression, qualifier):
                    return false;
                case InPredicate { NotDefined: false } inPredicate when Is<InPredicate>(inPredicate) && IsColumn(inPredicate.Expression, qualifier):
                    if (inPredicate.Subquery is null)
                        return inPredicate.Values.Count > 0 && inPredicate.Values.All(IsConstant) ? false : Refuse(inPredicate, "IN (...) may list constants only");
                    if (qualifier is not null) return Refuse(inPredicate, "Only one level of IN subquery is allowed");
                    return Subquery(inPredicate.Subquery) ? true : null;
            }

            return Refuse(condition, $"Condition {condition.GetType().Name} is not allowed; a condition is column = {Param}, column IN (subquery), a comparison of the table's columns with constants, column IS [NOT] NULL or column IN (constants)");
        }

        private bool? Refuse(TSqlFragment node, string message)
        {
            Fail(node, message);
            return null;
        }

        private bool Subquery(ScalarSubquery subquery)
        {
            if (!Is<QuerySpecification>(subquery.QueryExpression) || subquery.Collation is not null)
                return Fail(subquery, $"The IN subquery must be SELECT a.column FROM {moduleSchema}.<table> a WHERE a.column = {Param}");
            var spec = (QuerySpecification)subquery.QueryExpression;
            if (!PlainQuery(spec) || !SingleTable(spec, aliasAllowed: true, isTarget: false, out var table)) return false;
            var exposed = table.Alias?.Value ?? table.SchemaObject.BaseIdentifier.Value;
            if (exposed.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                return Fail(table, "Give the subquery table an alias that differs from the written table's name");
            if (spec.SelectElements.Count != 1 || !IsNamed(spec.SelectElements[0], null, out var column) || !IsColumn(column, exposed))
                return Fail(spec, $"The IN subquery must select exactly one column qualified by '{exposed}'");
            if (spec.WhereClause is not { Cursor: null } where)
                return Fail(spec, $"The IN subquery must have a WHERE clause with {exposed}.column = {Param}");
            return Restricts(where.SearchCondition, exposed);
        }

        private static bool IsNamed(SelectElement element, string? alias, out ScalarExpression expression)
        {
            expression = null!;
            if (!Is<SelectScalarExpression>(element)) return false;
            var scalar = (SelectScalarExpression)element;
            expression = scalar.Expression;
            return alias is null
                ? scalar.ColumnName is null
                : scalar.ColumnName is { ValueExpression: null, Identifier: { } name } && name.Value.Equals(alias, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCountStar(ScalarExpression expression)
            => Is<FunctionCall>(expression)
                && expression is FunctionCall call
                && IsPlainCall(call, "COUNT")
                && call.Parameters.Count == 1
                && call.Parameters[0] is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard, MultiPartIdentifier: null };

        private static bool IsPlainCall(FunctionCall call, string name)
            => call.CallTarget is null && call.OverClause is null && call.WithinGroupClause is null && call.Collation is null
                && call.UniqueRowFilter == UniqueRowFilter.NotSpecified
                && call.FunctionName.Value.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static bool IsColumn(ScalarExpression? expression, string? qualifier)
        {
            if (!Is<ColumnReferenceExpression>(expression)) return false;
            var column = (ColumnReferenceExpression)expression!;
            if (column.ColumnType != ColumnType.Regular || column.Collation is not null || column.MultiPartIdentifier is null) return false;
            var identifiers = column.MultiPartIdentifier.Identifiers;
            return qualifier is null
                ? identifiers.Count == 1
                : identifiers.Count == 2 && identifiers[0].Value.Equals(qualifier, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsParameter(ScalarExpression? expression)
            => Is<VariableReference>(expression) && ((VariableReference)expression!).Name.Equals(Param, StringComparison.OrdinalIgnoreCase);

        private static bool IsOperand(ScalarExpression? expression, string? qualifier)
            => IsColumn(expression, qualifier) || IsConstant(expression);

        private static bool IsConstant(ScalarExpression? expression)
        {
            if (expression is null) return false;
            if (Is<IntegerLiteral>(expression) || Is<NumericLiteral>(expression) || Is<RealLiteral>(expression)
                || Is<StringLiteral>(expression) || Is<NullLiteral>(expression))
                return true;
            if (Is<UnaryExpression>(expression))
            {
                var unary = (UnaryExpression)expression;
                return unary.UnaryExpressionType == UnaryExpressionType.Negative
                    && (Is<IntegerLiteral>(unary.Expression) || Is<NumericLiteral>(unary.Expression) || Is<RealLiteral>(unary.Expression));
            }

            return Is<FunctionCall>(expression)
                && expression is FunctionCall call
                && call.Parameters.Count == 0
                && (IsPlainCall(call, "GETUTCDATE") || IsPlainCall(call, "SYSUTCDATETIME"));
        }
    }

    /// <summary>
    /// Second pass: every syntax node must be of a type the grammar uses, so a clause the
    /// structural pass did not inspect cannot carry SQL through.
    /// </summary>
    private sealed class GrammarNodeCheck : TSqlFragmentVisitor
    {
        private static readonly HashSet<Type> Allowed =
        [
            typeof(TSqlScript), typeof(TSqlBatch), typeof(IfStatement), typeof(BeginEndBlockStatement), typeof(StatementList),
            typeof(DeleteStatement), typeof(DeleteSpecification), typeof(UpdateStatement), typeof(UpdateSpecification),
            typeof(SelectStatement), typeof(QuerySpecification), typeof(SelectScalarExpression), typeof(IdentifierOrValueExpression),
            typeof(FromClause), typeof(WhereClause), typeof(NamedTableReference), typeof(SchemaObjectName), typeof(Identifier),
            typeof(MultiPartIdentifier), typeof(ColumnReferenceExpression), typeof(VariableReference), typeof(AssignmentSetClause),
            typeof(BooleanComparisonExpression), typeof(BooleanBinaryExpression), typeof(BooleanParenthesisExpression),
            typeof(BooleanIsNullExpression), typeof(InPredicate), typeof(ScalarSubquery), typeof(FunctionCall), typeof(UnaryExpression),
            typeof(IntegerLiteral), typeof(NumericLiteral), typeof(RealLiteral), typeof(StringLiteral), typeof(NullLiteral),
        ];

        internal string? Error { get; private set; }

        public override void Visit(TSqlFragment node)
        {
            if (Error is null && !Allowed.Contains(node.GetType()))
                Error = $"Syntax {node.GetType().Name} is not part of the runtime maintenance step grammar (line {node.StartLine}, column {node.StartColumn}).";
        }
    }
}
