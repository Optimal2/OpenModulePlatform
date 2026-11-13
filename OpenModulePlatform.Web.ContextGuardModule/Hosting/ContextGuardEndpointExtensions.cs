using Microsoft.AspNetCore.Antiforgery;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Web.ContextGuardModule.Hosting;

public static class ContextGuardEndpointExtensions
{
    public static WebApplication MapContextGuardEndpoints(this WebApplication app)
    {
        // These two inbound integration endpoints deliberately sit outside OMP login and
        // antiforgery. They only stage/end by a one-time correlation; no context is readable.
        app.MapPost("/handoff/{profileKey}", async (
            HttpContext context,
            string profileKey,
            ContextGuardService service,
            ContextGuardFormReader reader,
            CancellationToken cancellationToken) =>
        {
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

            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location =
                $"{context.Request.PathBase}/show?k={Uri.EscapeDataString(result.Key)}";
            return Results.Empty;
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/handoff/{profileKey}/end", async (
            HttpContext context,
            string profileKey,
            ContextGuardService service,
            ContextGuardFormReader reader,
            CancellationToken cancellationToken) =>
        {
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

            var correlation = ContextGuardService.GetCorrelation(profile, form.Fields);
            if (string.IsNullOrWhiteSpace(correlation))
            {
                return Results.BadRequest();
            }

            service.End(profileKey, correlation);
            // Do not reveal whether a correlation value matched a live context.
            return Results.NoContent();
        }).AllowAnonymous().DisableAntiforgery();

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
