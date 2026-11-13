using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using OpenModulePlatform.Web.ContextGuardModule.Pages;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.ContextGuardModule.Hosting;

public static class ContextGuardEndpointExtensions
{
    public static WebApplication MapContextGuardEndpoints(this WebApplication app)
    {
        app.UseRateLimiter();
        // These two inbound integration endpoints deliberately sit outside OMP login and
        // antiforgery. They only stage/end by a one-time correlation; no context is readable.
        app.MapPost("/handoff/{profileKey}", async (
            HttpContext context,
            string profileKey,
            ContextGuardService service,
            ContextGuardFormReader reader,
            CancellationToken cancellationToken) =>
        {
            DisableStatusPageReExecute(context);
            if (!service.TryGetProfile(profileKey, out var profile) || profile is null)
            {
                return Results.NotFound();
            }

            if (!ContextGuardFormReader.IsOriginAllowed(context.Request, profile))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var form = await reader.ReadAsync(
                context.Request,
                profile,
                service.OptionsSnapshot,
                cancellationToken);
            if (form.Status != FormReadStatus.Success)
            {
                return FormError(form.Status);
            }

            if (string.IsNullOrWhiteSpace(ContextGuardService.GetCorrelation(profile, form.AllowedFields)))
            {
                return Results.BadRequest();
            }

            var result = service.Receive(profileKey, form.AllowedFields, form.DroppedFieldCount);
            if (result.Status != Models.HandoffCreateStatus.Created || result.Key is null)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            ParkHandoffKey(context, service, result.Key);
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = $"{context.Request.PathBase}/show";
            return Results.Empty;
        }).AllowAnonymous().DisableAntiforgery()
          .RequireRateLimiting(ContextGuardServiceCollectionExtensions.ReceiveRateLimitPolicy);

        app.MapPost("/handoff/{profileKey}/end", async (
            HttpContext context,
            string profileKey,
            ContextGuardService service,
            ContextGuardFormReader reader,
            CancellationToken cancellationToken) =>
        {
            DisableStatusPageReExecute(context);
            if (!service.TryGetProfile(profileKey, out var profile) || profile is null || !profile.EndEnabled)
            {
                return Results.NotFound();
            }

            if (!ContextGuardFormReader.IsOriginAllowed(context.Request, profile))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var form = await reader.ReadAsync(
                context.Request,
                profile,
                service.OptionsSnapshot,
                cancellationToken);
            if (form.Status != FormReadStatus.Success)
            {
                return FormError(form.Status);
            }

            var correlation = ContextGuardService.GetCorrelation(profile, form.Fields);
            if (string.IsNullOrWhiteSpace(correlation))
            {
                return Results.BadRequest();
            }

            var presentedSecret = form.Fields
                .LastOrDefault(field => string.Equals(
                    field.Name,
                    ContextGuardService.EndSecretFieldName,
                    StringComparison.Ordinal))?.Value;
            switch (ContextGuardService.CheckEndAllowed(profile, correlation, presentedSecret))
            {
                case EndRequestStatus.EndDisabled:
                    return Results.NotFound();
                case EndRequestStatus.CorrelationTooShort:
                    return Results.BadRequest();
                case EndRequestStatus.InvalidSecret:
                    service.LogInvalidEndSecret(profileKey);
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            service.End(profileKey, correlation);
            // Do not reveal whether a correlation value matched a live context.
            return Results.NoContent();
        }).AllowAnonymous().DisableAntiforgery()
          .RequireRateLimiting(ContextGuardServiceCollectionExtensions.EndRateLimitPolicy);

        app.MapPost("/context/{contextId:guid}/heartbeat", async (
            HttpContext context,
            Guid contextId,
            ContextGuardService service,
            IAntiforgery antiforgery,
            CancellationToken cancellationToken) =>
        {
            if (OmpUserIdentity.TryGetOmpUserId(context.User) is not int userId)
            {
                return Results.Forbid();
            }

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var result = service.Heartbeat(contextId, userId);
            return Results.Json(new { state = StateName(result.State) });
        }).RequireAuthorization();

        app.MapGet("/context/{contextId:guid}/state", (
            HttpContext context,
            Guid contextId,
            ContextGuardService service) =>
        {
            if (OmpUserIdentity.TryGetOmpUserId(context.User) is not int userId)
            {
                return Results.Forbid();
            }

            var result = service.GetState(contextId, userId);
            return Results.Json(new { state = StateName(result.State) });
        }).RequireAuthorization();

        return app;
    }

    private static void ParkHandoffKey(HttpContext context, ContextGuardService service, string key)
    {
        // The one-time key travels in a short-lived, HttpOnly, path-scoped cookie
        // instead of the login returnUrl, so it never lands in address bars,
        // server logs or browser history. The guard page consumes and deletes it
        // on first use and still accepts the query form when no cookie is present.
        var cookiePath = context.Request.PathBase.HasValue
            ? context.Request.PathBase.Value + "/show"
            : "/show";
        context.Response.Cookies.Append(ShowModel.HandoffKeyCookieName, key, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = cookiePath,
            MaxAge = TimeSpan.FromSeconds(service.OptionsSnapshot.HandoffTtlSeconds + 300)
        });
    }

    private static void DisableStatusPageReExecute(HttpContext context)
    {
        // The host pipeline re-executes 4xx/5xx responses through an HTML status
        // page, which would mask the documented machine-readable statuses of these
        // inbound integration endpoints (403, 404, 413, 415, 429 become 400 on the
        // re-executed POST). These callers need the real status, so opt this request out.
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }
    }

    private static IResult FormError(FormReadStatus status) => status switch
    {
        FormReadStatus.UnsupportedMediaType => Results.StatusCode(StatusCodes.Status415UnsupportedMediaType),
        FormReadStatus.TooLarge => Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge),
        FormReadStatus.TooManyFields => Results.BadRequest(),
        FormReadStatus.FilesNotAllowed => Results.BadRequest(),
        _ => Results.BadRequest()
    };

    private static string StateName(Models.ContextLifecycle state) => state switch
    {
        Models.ContextLifecycle.Current => "current",
        Models.ContextLifecycle.Superseded => "superseded",
        _ => "ended"
    };
}
