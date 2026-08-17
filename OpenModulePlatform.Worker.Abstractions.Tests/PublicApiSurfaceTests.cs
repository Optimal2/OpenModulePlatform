// File: OpenModulePlatform.Worker.Abstractions.Tests/PublicApiSurfaceTests.cs
using System.Reflection;
using System.Text;
using OpenModulePlatform.Worker.Abstractions.Contracts;

namespace OpenModulePlatform.Worker.Abstractions.Tests;

/// <summary>
/// Snapshots the public API surface of OpenModulePlatform.Worker.Abstractions and
/// fails when it changes.
///
/// Why this test exists: worker modules are compiled against one copy of this
/// assembly and loaded by a host that may carry another. Two builds whose public
/// surface differs have different type identity in practice, and the failure
/// shows up far from the cause -- a host reporting "Expected exactly one worker
/// module factory ... Available keys: ." with an empty list, because
/// IsAssignableFrom matched nothing. The same failure mode was observed twice in
/// one week (a plugin built against new abstractions loaded by an old host, and
/// IbsPackager's channel type loaded with a mismatched Abstractions dll), so it
/// is a proven failure mode, not a theoretical one.
///
/// The test does not prevent changing the surface; it forces the question "has
/// the worker host been deployed with the matching build?" at review time.
///
/// What the snapshot covers is what actually breaks binding: public type names
/// and kinds (interface vs class vs struct), class modifiers (sealed/abstract/
/// static), public constructors, methods, properties, fields and events with
/// their full signatures. What it deliberately excludes is what does not break
/// binding: method bodies, private/internal members, XML documentation, and
/// nullable reference annotations.
///
/// Approving a legitimate change: run the test, let it write
/// WorkerAbstractions.PublicApi.received.txt next to the test assembly, review
/// the diff, then copy it over WorkerAbstractions.PublicApi.snapshot.txt in this
/// project. The snapshot update is a normal file change, so it is plainly
/// visible in the pull request diff -- that visibility is the point.
/// </summary>
public sealed class PublicApiSurfaceTests
{
    private const string SnapshotFileName = "WorkerAbstractions.PublicApi.snapshot.txt";
    private const string ReceivedFileName = "WorkerAbstractions.PublicApi.received.txt";

    [Fact]
    public void PublicApiSurface_MatchesApprovedSnapshot()
    {
        var actual = PublicApiSnapshotBuilder.Build(typeof(IWorkerModuleFactory).Assembly);

        var snapshotPath = Path.Combine(AppContext.BaseDirectory, SnapshotFileName);
        Assert.True(
            File.Exists(snapshotPath),
            $"Approved snapshot not found at '{snapshotPath}'. The snapshot file must be copied to the test output directory.");

        var expected = File.ReadAllText(snapshotPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        var receivedPath = Path.Combine(AppContext.BaseDirectory, ReceivedFileName);
        File.WriteAllText(receivedPath, actual);

        Assert.Fail(
            "The public API surface of OpenModulePlatform.Worker.Abstractions has changed. " +
            "Worker modules are compiled against one copy of this assembly and loaded by a host that may carry another; " +
            "a surface change means every consumer must be rebuilt and the worker host redeployed together. " +
            $"If the change is intentional, review '{receivedPath}' against the approved snapshot and copy it over " +
            $"'OpenModulePlatform.Worker.Abstractions.Tests/{SnapshotFileName}' so the change is visible in the diff. " +
            "First differing line: " + FirstDifference(expected, actual));
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var count = Math.Max(expectedLines.Length, actualLines.Length);
        for (var i = 0; i < count; i++)
        {
            var expectedLine = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            var actualLine = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                return $"line {i + 1}: expected '{expectedLine}', actual '{actualLine}'.";
            }
        }

        return "<none>";
    }
}

/// <summary>
/// Renders the public surface of an assembly as a deterministic, line-oriented
/// text form. Everything emitted is part of the binding contract; everything
/// that is not (bodies, non-public members, attributes, nullability) is left out
/// so the snapshot does not churn on changes that cannot break a consumer.
/// </summary>
internal static class PublicApiSnapshotBuilder
{
    public static string Build(Assembly assembly)
    {
        var sb = new StringBuilder();
        foreach (var type in assembly.ExportedTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            AppendType(sb, type);
        }

        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendType(StringBuilder sb, Type type)
    {
        sb.Append("type ").Append(FormatTypeKind(type)).Append(' ').Append(type.FullName);

        var interfaces = type.GetInterfaces()
            .Select(FormatTypeName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (interfaces.Length > 0)
        {
            sb.Append(" : ").Append(string.Join(", ", interfaces));
        }

        sb.Append('\n');

        foreach (var line in EnumerateMembers(type).OrderBy(line => line, StringComparer.Ordinal))
        {
            sb.Append("    ").Append(line).Append('\n');
        }
    }

    private static IEnumerable<string> EnumerateMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var constructor in type.GetConstructors(flags))
        {
            yield return $"ctor({FormatParameters(constructor.GetParameters())})";
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName)
            {
                continue; // property/event accessors are rendered with their property/event
            }

            var modifiers = new StringBuilder();
            if (method.IsStatic)
            {
                modifiers.Append("static ");
            }
            if (method.IsAbstract)
            {
                modifiers.Append("abstract ");
            }
            else if (method.IsVirtual && !method.IsFinal)
            {
                modifiers.Append("virtual ");
            }

            var generic = method.IsGenericMethodDefinition
                ? "<" + string.Join(",", method.GetGenericArguments().Select(a => a.Name)) + ">"
                : string.Empty;

            yield return $"method {modifiers}{FormatTypeName(method.ReturnType)} {method.Name}{generic}({FormatParameters(method.GetParameters())})";
        }

        foreach (var property in type.GetProperties(flags))
        {
            var accessors = new StringBuilder();
            if (property.GetMethod is not null)
            {
                accessors.Append("get; ");
            }
            if (property.SetMethod is not null)
            {
                accessors.Append("set; ");
            }

            var staticPrefix = (property.GetMethod ?? property.SetMethod)?.IsStatic == true ? "static " : string.Empty;
            yield return $"property {staticPrefix}{FormatTypeName(property.PropertyType)} {property.Name} {{ {accessors}}}";
        }

        foreach (var field in type.GetFields(flags))
        {
            var modifiers = field.IsStatic ? "static " : string.Empty;
            if (field.IsInitOnly)
            {
                modifiers += "readonly ";
            }
            if (field.IsLiteral)
            {
                modifiers += "const ";
            }

            yield return $"field {modifiers}{FormatTypeName(field.FieldType)} {field.Name}";
        }

        foreach (var @event in type.GetEvents(flags))
        {
            yield return $"event {FormatTypeName(@event.EventHandlerType!)} {@event.Name}";
        }
    }

    private static string FormatTypeKind(Type type)
    {
        if (type.IsInterface)
        {
            return "interface";
        }
        if (type.IsEnum)
        {
            return "enum";
        }
        if (type.IsValueType)
        {
            return "struct";
        }

        var modifiers = new StringBuilder();
        if (type.IsAbstract && type.IsSealed)
        {
            modifiers.Append("static ");
        }
        else
        {
            if (type.IsSealed)
            {
                modifiers.Append("sealed ");
            }
            if (type.IsAbstract)
            {
                modifiers.Append("abstract ");
            }
        }

        return modifiers.Append("class").ToString();
    }

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(p => FormatTypeName(p.ParameterType) + " " + p.Name));

    private static string FormatTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return FormatTypeName(type.GetElementType()!) + "&";
        }
        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.IsGenericType)
        {
            var definitionName = type.GetGenericTypeDefinition().FullName!;
            var tick = definitionName.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                definitionName = definitionName[..tick];
            }

            return definitionName + "<" + string.Join(",", type.GetGenericArguments().Select(FormatTypeName)) + ">";
        }

        return type.FullName ?? type.Name;
    }
}
