using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace OpenModulePlatform.Web.Shared.Analyzers.Tests
{
    public class OmpWebDefaultsAnalyzerTests
    {
        private const string FakeWebSharedSource = @"
using Microsoft.AspNetCore.Builder;

namespace Microsoft.AspNetCore.Builder
{
    public class WebApplicationBuilder { }
    public class WebApplication { }
}

namespace OpenModulePlatform.Web.Shared.Extensions
{
    public static class OmpWebHostingExtensions
    {
        public static WebApplicationBuilder AddOmpWebDefaults<TAppResource>(this WebApplicationBuilder builder, string optionsSectionName = null)
            where TAppResource : class
            => builder;

        public static WebApplication UseOmpWebDefaults(this WebApplication app, string optionsSectionName = null, bool mapRazorPages = true)
            => app;
    }
}
";

        private const string FakeWebSharedNonWebSource = @"
namespace OpenModulePlatform.Web.Shared
{
    public class WebSharedMarker { }
}
";

        private static CSharpAnalyzerTest<OmpWebDefaultsAnalyzer, DefaultVerifier> CreateTest(
            string source,
            bool isWebHost = true,
            bool isAuth = false,
            bool includeWebShared = true)
        {
            var test = new CSharpAnalyzerTest<OmpWebDefaultsAnalyzer, DefaultVerifier>
            {
                TestCode = source,
                ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
                TestState =
                {
                    OutputKind = OutputKind.ConsoleApplication,
                },
            };

            if (includeWebShared)
            {
                var webSharedProject = new ProjectState(
                    "OpenModulePlatform.Web.Shared",
                    LanguageNames.CSharp,
                    "/0/Test",
                    "cs")
                {
                    Sources = { isWebHost ? FakeWebSharedSource : FakeWebSharedNonWebSource },
                };

                test.TestState.AdditionalProjects.Add("OpenModulePlatform.Web.Shared", webSharedProject);
                test.TestState.AdditionalProjectReferences.Add("OpenModulePlatform.Web.Shared");
            }

            if (isAuth)
            {
                test.SolutionTransforms.Add((solution, projectId) =>
                    solution.WithProjectAssemblyName(projectId, "OpenModulePlatform.Auth"));
            }

            return test;
        }

        [Fact]
        public async Task WebHost_WithBothDefaults_NoDiagnostics()
        {
            const string source = @"
#nullable disable
using OpenModulePlatform.Web.Shared.Extensions;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        WebApplicationBuilder builder = null;
        WebApplication app = null;

        builder.AddOmpWebDefaults<object>(""Portal"");
        app.UseOmpWebDefaults(""Portal"", true);
    }
}
";

            var test = CreateTest(source);
            await test.RunAsync();
        }

        [Fact]
        public async Task NonWebProject_WithWebSharedReference_NoDiagnostics()
        {
            const string source = @"
using OpenModulePlatform.Web.Shared;

class Program
{
    static void Main() { }
}
";

            var test = CreateTest(source, isWebHost: false);
            await test.RunAsync();
        }

        [Fact]
        public async Task AuthProject_WithoutDefaults_NoDiagnostics()
        {
            const string source = @"
#nullable disable
using OpenModulePlatform.Web.Shared.Extensions;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        WebApplicationBuilder builder = null;
        WebApplication app = null;
    }
}
";

            var test = CreateTest(source, isAuth: true);
            await test.RunAsync();
        }

        [Fact]
        public async Task WebHost_MissingAddDefaults_ReportsOMPWEB001()
        {
            const string source = @"
#nullable disable
using OpenModulePlatform.Web.Shared.Extensions;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        WebApplicationBuilder builder = null;
        WebApplication app = null;

        app.UseOmpWebDefaults(""Portal"", true);
    }
}
";

            var expected = new DiagnosticResult("OMPWEB001", DiagnosticSeverity.Warning)
                .WithMessage("Web host 'TestProject' references OpenModulePlatform.Web.Shared but does not call AddOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.");

            var test = CreateTest(source);
            test.ExpectedDiagnostics.Add(expected);
            await test.RunAsync();
        }

        [Fact]
        public async Task WebHost_MissingUseDefaults_ReportsOMPWEB002()
        {
            const string source = @"
#nullable disable
using OpenModulePlatform.Web.Shared.Extensions;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        WebApplicationBuilder builder = null;
        WebApplication app = null;

        builder.AddOmpWebDefaults<object>(""Portal"");
    }
}
";

            var expected = new DiagnosticResult("OMPWEB002", DiagnosticSeverity.Warning)
                .WithMessage("Web host 'TestProject' references OpenModulePlatform.Web.Shared but does not call UseOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.");

            var test = CreateTest(source);
            test.ExpectedDiagnostics.Add(expected);
            await test.RunAsync();
        }

        [Fact]
        public async Task WebHost_MissingBothDefaults_ReportsOMPWEB001AndOMPWEB002()
        {
            const string source = @"
#nullable disable
using OpenModulePlatform.Web.Shared.Extensions;
using Microsoft.AspNetCore.Builder;

class Program
{
    static void Main()
    {
        WebApplicationBuilder builder = null;
        WebApplication app = null;
    }
}
";

            var expectedAdd = new DiagnosticResult("OMPWEB001", DiagnosticSeverity.Warning)
                .WithMessage("Web host 'TestProject' references OpenModulePlatform.Web.Shared but does not call AddOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.");

            var expectedUse = new DiagnosticResult("OMPWEB002", DiagnosticSeverity.Warning)
                .WithMessage("Web host 'TestProject' references OpenModulePlatform.Web.Shared but does not call UseOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.");

            var test = CreateTest(source);
            test.ExpectedDiagnostics.Add(expectedAdd);
            test.ExpectedDiagnostics.Add(expectedUse);
            await test.RunAsync();
        }
    }
}
