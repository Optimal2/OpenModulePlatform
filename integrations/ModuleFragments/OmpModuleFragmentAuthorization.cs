using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.ModuleFragments;

/// <summary>
/// Protects an HTML fragment before its handler runs. Permissions within one attribute
/// are alternatives; multiple attributes must all pass. Never combine with AllowAnonymous:
/// <see cref="OmpModuleFragmentStartupFilter"/> fails host startup for such endpoints.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class OmpModuleFragmentAttribute : AuthorizeAttribute
{
    internal const string PolicyName = "OMP.ModuleFragment";

    public OmpModuleFragmentAttribute(params string[] permissions) : base(PolicyName)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty fragment permission is required.", nameof(permissions));
        }

        Permissions = Array.AsReadOnly((string[])permissions.Clone());
    }

    public IReadOnlyList<string> Permissions { get; }
}

/// <summary>Resolves permissions for the authenticated OMP session and active role.</summary>
public interface IOmpModuleFragmentPermissions
{
    Task<HashSet<string>> GetAsync(HttpContext context);
}

public static class OmpModuleFragmentExtensions
{
    /// <summary>Call after AddOmpWebDefaults and before building the application.</summary>
    public static IServiceCollection AddOmpModuleFragments(this IServiceCollection services)
    {
        services.AddAuthorization(options => options.AddPolicy(OmpModuleFragmentAttribute.PolicyName,
            policy => policy.AddAuthenticationSchemes(OmpAuthDefaults.AuthenticationScheme)
                .AddRequirements(new FragmentRequirement())));
        services.AddScoped<IOmpModuleFragmentPermissions, RbacFragmentPermissions>();
        services.AddScoped<IAuthorizationHandler, FragmentAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, FragmentAuthorizationResultHandler>();
        services.AddSingleton<IStartupFilter, OmpModuleFragmentStartupFilter>();
        return services;
    }
}

/// <summary>
/// Fails host startup when an endpoint combines the fragment policy with AllowAnonymous
/// (attribute or endpoint convention). AllowAnonymous short-circuits authorization
/// middleware, so such an endpoint would silently serve the fragment to anyone.
/// </summary>
internal sealed class OmpModuleFragmentStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            next(app);
            // The composite EndpointDataSource is wired from the application's route
            // builder while the request pipeline is composed, so the endpoints are
            // complete here but the server has not started accepting requests yet.
            ThrowOnConflictingEndpoints(app.ApplicationServices.GetRequiredService<EndpointDataSource>());
        };

    internal static void ThrowOnConflictingEndpoints(EndpointDataSource endpoints)
    {
        var conflicts = endpoints.Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<OmpModuleFragmentAttribute>() is not null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.DisplayName)
            .ToArray();

        if (conflicts.Length > 0)
        {
            throw new InvalidOperationException(
                "Endpoint(s) combine [OmpModuleFragment] with AllowAnonymous, which bypasses the "
                + $"module-fragment policy and would serve the fragment to anyone: {string.Join(", ", conflicts)}. "
                + "Remove AllowAnonymous from the fragment endpoint.");
        }
    }
}

internal sealed class FragmentRequirement : IAuthorizationRequirement;

internal sealed class RbacFragmentPermissions(RbacService rbac) : IOmpModuleFragmentPermissions
{
    public Task<HashSet<string>> GetAsync(HttpContext context)
        => rbac.GetUserPermissionsAsync(context.User, context.RequestAborted);
}

internal sealed class FragmentAuthorizationHandler(
    IOptions<WebAppOptions> options,
    IOmpModuleFragmentPermissions permissions) : AuthorizationHandler<FragmentRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, FragmentRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var fragments = http.GetEndpoint()?.Metadata.GetOrderedMetadata<OmpModuleFragmentAttribute>();
        if (fragments is null || fragments.Count == 0)
        {
            return;
        }

        // Match the existing explicit development bypass. This is NOT an enforced mode.
        if (options.Value.AllowAnonymous)
        {
            context.Succeed(requirement);
            return;
        }

        // The policy explicitly authenticates OmpAuth, so an unrelated identity or a
        // caller-supplied dashboard header cannot substitute for a validated OMP cookie.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var current = await permissions.GetAsync(http);
        if (fragments.All(fragment => fragment.Permissions.Any(current.Contains)))
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class FragmentAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Succeeded && context.GetEndpoint()?.Metadata.GetMetadata<OmpModuleFragmentAttribute>() is not null)
        {
            // Do not challenge/forbid the cookie handler: either can redirect. Also stop
            // status-page re-execution from turning the empty denial into HTML.
            var statusPages = context.Features.Get<IStatusCodePagesFeature>();
            if (statusPages is not null)
            {
                statusPages.Enabled = false;
            }

            context.Response.StatusCode = result.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            context.Response.ContentLength = 0;
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }

        return _default.HandleAsync(next, context, policy, result);
    }
}
