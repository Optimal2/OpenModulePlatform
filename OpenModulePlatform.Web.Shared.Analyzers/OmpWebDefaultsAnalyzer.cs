using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace OpenModulePlatform.Web.Shared.Analyzers
{
    /// <summary>
    /// Reports when an ASP.NET Core web host references OpenModulePlatform.Web.Shared
    /// but does not call AddOmpWebDefaults (OMPWEB001) or UseOmpWebDefaults (OMPWEB002).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class OmpWebDefaultsAnalyzer : DiagnosticAnalyzer
    {
        public const string AddDefaultsDiagnosticId = "OMPWEB001";
        public const string UseDefaultsDiagnosticId = "OMPWEB002";

        private const string WebSharedAssemblyName = "OpenModulePlatform.Web.Shared";
        private const string AuthAssemblyName = "OpenModulePlatform.Auth";
        private const string WebApplicationBuilderTypeName = "Microsoft.AspNetCore.Builder.WebApplicationBuilder";
        private const string WebApplicationTypeName = "Microsoft.AspNetCore.Builder.WebApplication";
        private const string OmpWebHostingExtensionsFullName = "OpenModulePlatform.Web.Shared.Extensions.OmpWebHostingExtensions";
        private const string AddMethodName = "AddOmpWebDefaults";
        private const string UseMethodName = "UseOmpWebDefaults";
        private static readonly string[] CompilationEndTag = new[] { "CompilationEnd" };

        private static readonly DiagnosticDescriptor AddRule = new DiagnosticDescriptor(
            AddDefaultsDiagnosticId,
            "AddOmpWebDefaults missing",
            "Web host '{0}' references OpenModulePlatform.Web.Shared but does not call AddOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            "ASP.NET Core web hosts that reference OpenModulePlatform.Web.Shared should call AddOmpWebDefaults during service registration.",
            customTags: CompilationEndTag);

        private static readonly DiagnosticDescriptor UseRule = new DiagnosticDescriptor(
            UseDefaultsDiagnosticId,
            "UseOmpWebDefaults missing",
            "Web host '{0}' references OpenModulePlatform.Web.Shared but does not call UseOmpWebDefaults. This may result in missing topbar, auth, or shared web integration.",
            "Usage",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            "ASP.NET Core web hosts that reference OpenModulePlatform.Web.Shared should call UseOmpWebDefaults during pipeline configuration.",
            customTags: CompilationEndTag);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(AddRule, UseRule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(AnalyzeCompilationStart);
        }

        private static void AnalyzeCompilationStart(CompilationStartAnalysisContext startContext)
        {
            var compilation = startContext.Compilation;

            // OpenModulePlatform.Auth deliberately uses its own pipeline.
            if (string.Equals(compilation.AssemblyName, AuthAssemblyName, StringComparison.Ordinal))
            {
                return;
            }

            // Only analyze compilations that reference OpenModulePlatform.Web.Shared.
            if (!compilation.ReferencedAssemblyNames.Any(a =>
                string.Equals(a.Name, WebSharedAssemblyName, StringComparison.Ordinal)))
            {
                return;
            }

            // Only analyze ASP.NET Core web hosts.
            var webAppBuilderType = compilation.GetTypeByMetadataName(WebApplicationBuilderTypeName);
            var webAppType = compilation.GetTypeByMetadataName(WebApplicationTypeName);
            if (webAppBuilderType == null && webAppType == null)
            {
                return;
            }

            var extensionsType = compilation.GetTypeByMetadataName(OmpWebHostingExtensionsFullName);
            var addMethod = extensionsType?.GetMembers(AddMethodName).OfType<IMethodSymbol>().FirstOrDefault();
            var useMethod = extensionsType?.GetMembers(UseMethodName).OfType<IMethodSymbol>().FirstOrDefault();

            bool hasAdd = false;
            bool hasUse = false;
            var lockObject = new object();

            startContext.RegisterSyntaxNodeAction(nodeContext =>
            {
                var invocation = (InvocationExpressionSyntax)nodeContext.Node;
                var symbol = nodeContext.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol == null)
                {
                    return;
                }

                lock (lockObject)
                {
                    if (!hasAdd && IsSameMethod(symbol, addMethod))
                    {
                        hasAdd = true;
                    }

                    if (!hasUse && IsSameMethod(symbol, useMethod))
                    {
                        hasUse = true;
                    }
                }
            }, SyntaxKind.InvocationExpression);

            startContext.RegisterCompilationEndAction(endContext =>
            {
                var assemblyName = compilation.AssemblyName ?? "unknown";

                if (!hasAdd)
                {
                    endContext.ReportDiagnostic(Diagnostic.Create(AddRule, Location.None, assemblyName));
                }

                if (!hasUse)
                {
                    endContext.ReportDiagnostic(Diagnostic.Create(UseRule, Location.None, assemblyName));
                }
            });
        }

        private static bool IsSameMethod(IMethodSymbol invocationSymbol, IMethodSymbol? targetMethod)
        {
            if (targetMethod == null)
            {
                return false;
            }

            var originalInvocation = invocationSymbol.OriginalDefinition;
            var originalTarget = targetMethod.OriginalDefinition;

            return originalInvocation.Equals(originalTarget, SymbolEqualityComparer.Default)
                || (originalInvocation.ReducedFrom != null
                    && originalInvocation.ReducedFrom.Equals(originalTarget, SymbolEqualityComparer.Default));
        }
    }
}
