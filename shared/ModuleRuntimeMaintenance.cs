using System.Text.Json;
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
/// contract. Validation is fail-closed: anything the analyzer cannot prove safe is rejected.
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

    // Reads are allowed from the platform catalog (joins such as "the worker rows on this host")
    // and from the SQL Server catalog views. Everything else outside the module schema is refused.
    private static readonly HashSet<string> ReadOnlySchemas = new(StringComparer.OrdinalIgnoreCase) { "omp", "sys" };

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
            if (Text(root, "moduleKey") is { Length: > 0 } key) module = key;
            if (!root.TryGetProperty(SectionName, out var section) || section.ValueKind == JsonValueKind.Null) return [];
            if (section.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"'{SectionName}' must be an object.");
            RejectUnknownProperties(section, SectionProperties, SectionName);
            if (module == "<unresolved module>") throw new InvalidOperationException("A definition with runtime maintenance steps must declare moduleKey.");

            var schemaName = root.TryGetProperty("module", out var moduleNode) && moduleNode.ValueKind == JsonValueKind.Object
                ? Text(moduleNode, "schemaName")
                : null;
            if (string.IsNullOrWhiteSpace(schemaName))
                throw new InvalidOperationException("A definition with runtime maintenance steps must declare module.schemaName; steps may only write that schema.");
            if (ReadOnlySchemas.Contains(schemaName) || schemaName.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Schema '{schemaName}' is not a module-owned schema.");

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
    /// Returns the first reason the step SQL is unsafe for <paramref name="contract"/>, or null.
    /// </summary>
    internal static string? ValidateStepSql(string sql, string moduleSchema, EventContract contract)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count != 0)
            return $"SQL cannot be parsed (line {errors[0].Line}, column {errors[0].Column}).";
        if (fragment is not TSqlScript { Batches.Count: 1 } script)
            return "SQL must be exactly one batch; GO separators are not allowed.";

        if (ModuleDefinitionSqlOwnership.Validate(sql) is { } ownership) return ownership;

        var visitor = new StepVisitor(moduleSchema, contract);
        script.Accept(visitor);
        if (visitor.Error is not null) return visitor.Error;
        if (!visitor.ReferencesParameter)
            return $"SQL must use the event parameter {contract.ParameterName}.";
        if (contract.Execution == ReadOnlyExecution && visitor.WritesData)
            return $"Event '{contract.Name}' is read-only; the step must not modify data.";
        return null;
    }

    private static void RejectUnknownProperties(JsonElement element, HashSet<string> allowed, string owner)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidOperationException($"{owner} has unknown property '{property.Name}'.");
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private sealed class StepVisitor(string moduleSchema, EventContract contract) : TSqlFragmentVisitor
    {
        private readonly Stack<HashSet<string>> guards = new();
        private HashSet<string> cteNames = new(StringComparer.OrdinalIgnoreCase);

        internal string? Error { get; private set; }
        internal bool ReferencesParameter { get; private set; }
        internal bool WritesData { get; private set; }

        private void Fail(TSqlFragment node, string message)
            => Error ??= $"{message} (line {node.StartLine}, column {node.StartColumn}).";

        public override void ExplicitVisit(TSqlScript node)
        {
            var collector = new CteCollector();
            node.Accept(collector);
            cteNames = collector.Names;
            base.ExplicitVisit(node);
        }

        // The IF guard is the idempotency contract: a module table is only touched inside the THEN
        // branch of IF OBJECT_ID(N'schema.table') IS NOT NULL, so an installation where the module
        // is absent or half-installed runs the step as a no-op instead of failing the platform
        // operation. The ELSE branch is not guarded by the predicate.
        public override void ExplicitVisit(IfStatement node)
        {
            node.Predicate?.Accept(this);
            var predicateGuards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectGuards(node.Predicate, predicateGuards);
            guards.Push(predicateGuards);
            node.ThenStatement?.Accept(this);
            guards.Pop();
            node.ElseStatement?.Accept(this);
        }

        public override void Visit(TSqlStatement node)
        {
            switch (node)
            {
                case BeginEndBlockStatement:
                case InsertStatement:
                case UpdateStatement:
                case DeleteStatement:
                case MergeStatement:
                case DeclareVariableStatement:
                case SetVariableStatement:
                    return;
                case SelectStatement select:
                    if (select.Into is not null) Fail(node, "SELECT INTO is not allowed");
                    return;
                case PredicateSetStatement set when set.Options == SetOptions.NoCount:
                    return;
                default:
                    Fail(node, $"Statement {node.GetType().Name} is not allowed in a runtime maintenance step; use IF, BEGIN/END, SELECT, INSERT, UPDATE, DELETE, MERGE, DECLARE, SET @variable or SET NOCOUNT");
                    return;
            }
        }

        public override void Visit(DataModificationSpecification node)
        {
            WritesData = true;
            if (node.OutputIntoClause is not null) Fail(node, "OUTPUT INTO is not allowed");
            if (node is UpdateSpecification { WhereClause: null } or DeleteSpecification { WhereClause: null })
                Fail(node, "UPDATE and DELETE must have a WHERE clause");

            if (node.Target is not NamedTableReference { SchemaObject.SchemaIdentifier: { } schema } target)
            {
                Fail(node, $"Write targets must be schema-qualified tables in the module schema '{moduleSchema}'; use the qualified table name, not an alias");
                return;
            }

            if (!schema.Value.Equals(moduleSchema, StringComparison.OrdinalIgnoreCase))
                Fail(node, $"Writes {schema.Value}.{target.SchemaObject.BaseIdentifier.Value}; runtime maintenance steps may only write the module schema '{moduleSchema}'");
        }

        public override void Visit(TableReference node)
        {
            if (node is not (NamedTableReference or JoinTableReference or QueryDerivedTable or JoinParenthesisTableReference or InlineDerivedTable))
                Fail(node, $"Table source {node.GetType().Name} is not allowed; use schema-qualified tables");
        }

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
            {
                Fail(node, "Cross-database and linked-server references are not allowed");
                return;
            }

            if (name.SchemaIdentifier is null)
            {
                if (!cteNames.Contains(name.BaseIdentifier.Value))
                    Fail(node, $"Table reference '{name.BaseIdentifier.Value}' must be schema-qualified");
                return;
            }

            var schema = name.SchemaIdentifier.Value;
            if (schema.Equals(moduleSchema, StringComparison.OrdinalIgnoreCase))
            {
                var qualified = $"{schema}.{name.BaseIdentifier.Value}";
                if (!guards.Any(set => set.Contains(qualified)))
                    Fail(node, $"{qualified} must be referenced inside IF OBJECT_ID(N'{qualified}', N'U') IS NOT NULL");
                return;
            }

            if (!ReadOnlySchemas.Contains(schema))
                Fail(node, $"References {schema}.{name.BaseIdentifier.Value}; steps may only reference the module schema '{moduleSchema}' and read omp or sys");
        }

        public override void Visit(ExecuteInsertSource node) => Fail(node, "INSERT ... EXEC is not allowed");

        public override void Visit(ExecutableEntity node) => Fail(node, "EXEC and dynamic SQL are not allowed; the platform binds the event parameter itself");

        public override void Visit(VariableReference node)
        {
            if (node.Name.Equals(contract.ParameterName, StringComparison.OrdinalIgnoreCase)) ReferencesParameter = true;
        }

        public override void Visit(DeclareVariableElement node)
        {
            if (node.VariableName.Value.Equals(contract.ParameterName, StringComparison.OrdinalIgnoreCase))
                Fail(node, $"{contract.ParameterName} is bound by the platform and must not be declared");
        }

        private static void CollectGuards(BooleanExpression? predicate, HashSet<string> into)
        {
            switch (predicate)
            {
                case BooleanParenthesisExpression parenthesis:
                    CollectGuards(parenthesis.Expression, into);
                    break;
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    CollectGuards(and.FirstExpression, into);
                    CollectGuards(and.SecondExpression, into);
                    break;
                case BooleanIsNullExpression { IsNot: true, Expression: FunctionCall function }
                    when function.CallTarget is null
                        && function.FunctionName.Value.Equals("OBJECT_ID", StringComparison.OrdinalIgnoreCase)
                        && function.Parameters.Count is 1 or 2
                        && function.Parameters[0] is StringLiteral literal
                        && NormalizeTwoPartName(literal.Value) is { } qualified:
                    into.Add(qualified);
                    break;
            }
        }

        private static string? NormalizeTwoPartName(string value)
        {
            var parts = value.Split('.');
            if (parts.Length != 2) return null;
            var schema = parts[0].Trim().Trim('[', ']', '"');
            var table = parts[1].Trim().Trim('[', ']', '"');
            return schema.Length == 0 || table.Length == 0 ? null : $"{schema}.{table}";
        }
    }

    private sealed class CteCollector : TSqlFragmentVisitor
    {
        internal HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public override void Visit(CommonTableExpression node) => Names.Add(node.ExpressionName.Value);
    }
}
