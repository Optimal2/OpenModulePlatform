// File: OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.EventPublisher.Sql;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.SignalR;
using OpenModulePlatform.Web.Shared.Telemetry;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using SystemNetIPNetwork = System.Net.IPNetwork;

namespace OpenModulePlatform.Web.Shared.Extensions;

/// <summary>
/// Registers the common hosting defaults used by the Portal and module web applications.
/// </summary>
/// <remarks>
/// <para>
/// The shared defaults deliberately stay small: Razor Pages, shared OMP cookie
/// authentication, optional forwarded-header support, and the shared services that
/// every OMP web application depends on.
/// </para>
/// <para>
/// Centralising these defaults reduces copy/paste between the Portal and individual
/// module UIs while still allowing each application to add its own services.
/// </para>
/// </remarks>
public static class OmpWebHostingExtensions
{
    private const int DefaultNotificationPageSize = 10;
    private const int MaxNotificationPageSize = 50;

    public static WebApplicationBuilder AddOmpWebDefaults<TAppResource>(
        this WebApplicationBuilder builder,
        string optionsSectionName = WebAppOptions.DefaultSectionName)
        where TAppResource : class
    {
        builder.AddOmpWebLogging();

        builder.Services.AddSingleton<IValidateOptions<WebAppOptions>, WebAppOptionsValidator>();

        builder.Services.AddOptions<WebAppOptions>()
            .Bind(builder.Configuration.GetSection(optionsSectionName))
            .ValidateOnStart();

        builder.Services.AddLocalization(options =>
        {
            options.ResourcesPath = "Resources";
        });

        builder.Services.AddRazorPages()
            .AddDataAnnotationsLocalization(options =>
            {
                // App resource first, SharedResource as fallback: the
                // validation message templates stamped by
                // OmpValidationMetadataProvider live in SharedResource so
                // every app renders localized defaults without duplicating
                // them, while an app can still override any key.
                options.DataAnnotationLocalizerProvider = static (_, factory) =>
                    new OmpCompositeStringLocalizer(
                        factory.Create(typeof(TAppResource)),
                        factory.Create(typeof(SharedResource)));
            });

        // Model binder conversion errors ("The value 'x' is not valid for ...") are
        // framework-internal English strings unless replaced. The accessors resolve
        // through the shared resource at binding time, so they follow the request
        // culture like every other localized string.
        builder.Services.AddOptions<Microsoft.AspNetCore.Mvc.MvcOptions>()
            .Configure<IStringLocalizerFactory>(static (options, localizerFactory) =>
            {
                // DataAnnotations attributes without an explicit ErrorMessage
                // (including the implicit Required for non-nullable reference
                // types) get a localizable template stamped on, so their
                // messages follow the request culture like everything else.
                options.ModelMetadataDetailsProviders.Add(new OmpValidationMetadataProvider());

                // A stale antiforgery token reloads the page instead of
                // surfacing the framework's empty 400 (which middleboxes can
                // re-frame into a lone "0" page).
                options.Filters.Add(new Mvc.AntiforgeryFailureRedirectFilter());

                var localizer = localizerFactory.Create(typeof(SharedResource));
                var messages = options.ModelBindingMessageProvider;
                messages.SetAttemptedValueIsInvalidAccessor((value, field) => localizer["The value '{0}' is not valid for {1}.", value, field]);
                messages.SetMissingBindRequiredValueAccessor(field => localizer["A value for the '{0}' field was not provided.", field]);
                messages.SetMissingKeyOrValueAccessor(() => localizer["A value is required."]);
                messages.SetMissingRequestBodyRequiredValueAccessor(() => localizer["A non-empty request body is required."]);
                messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => localizer["The value '{0}' is not valid.", value]);
                messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => localizer["The supplied value is invalid."]);
                messages.SetNonPropertyValueMustBeANumberAccessor(() => localizer["The field must be a number."]);
                messages.SetUnknownValueIsInvalidAccessor(field => localizer["The supplied value is invalid for {0}.", field]);
                messages.SetValueIsInvalidAccessor(value => localizer["The value '{0}' is invalid.", value]);
                messages.SetValueMustBeANumberAccessor(field => localizer["The field {0} must be a number.", field]);
                messages.SetValueMustNotBeNullAccessor(value => localizer["The value '{0}' is invalid.", value]);
            });

        builder.Services.AddAntiforgery();

        ConfigureOmpAuthentication(
            builder.Services,
            builder.Configuration,
            builder.Environment.ContentRootPath);

        var webAppOptions = builder.Configuration
            .GetSection(optionsSectionName)
            .Get<WebAppOptions>() ?? new WebAppOptions();
        var cultureSelectionService = new CultureSelectionService();

        builder.Services.AddAuthorization(options =>
        {
            if (!webAppOptions.AllowAnonymous)
            {
                options.FallbackPolicy = options.DefaultPolicy;
            }
        });

        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            var supportedCultureNames = webAppOptions.SupportedCultures;

            if (supportedCultureNames is null || supportedCultureNames.Length == 0)
            {
                supportedCultureNames = [webAppOptions.DefaultCulture];
            }

            var supportedCultures = supportedCultureNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(x => new CultureInfo(x))
                .ToArray();

            if (supportedCultures.Length == 0)
            {
                supportedCultures = [new CultureInfo("sv-SE")];
            }

            var defaultCultureName = string.IsNullOrWhiteSpace(webAppOptions.DefaultCulture)
                ? supportedCultures[0].Name
                : webAppOptions.DefaultCulture;

            options.DefaultRequestCulture = new RequestCulture(defaultCultureName);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;
            options.RequestCultureProviders =
            [
                new PreferredCultureRequestCultureProvider(webAppOptions, cultureSelectionService),
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider()
            ];
        });

        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<ILoggerFactory>((options, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(
                    "OpenModulePlatform.Web.Shared.ForwardedHeaders");

                ConfigureForwardedHeaders(options, webAppOptions, logger);
            });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddSignalR();
        builder.Services.AddTransient<IClaimsTransformation, ActiveRoleClaimsTransformation>();
        builder.Services.AddSingleton(cultureSelectionService);
        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.Configure<PushEventProducerOptions>(
            builder.Configuration.GetSection(PushEventProducerOptions.SectionName));
        builder.Services.AddSingleton<IPushEventPublisher>(sp =>
        {
            var db = sp.GetRequiredService<SqlConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<SqlPushEventPublisher>>();
            return new SqlPushEventPublisher(db.Create, logger);
        });
        builder.Services.AddSingleton<SignalRTopBarNotificationStatePublisher>();
        builder.Services.AddSingleton<OutboxTopBarNotificationStatePublisher>();
        builder.Services.AddSingleton<ITopBarNotificationStatePublisher, MigratingTopBarNotificationStatePublisher>();
        builder.Services.AddScoped<OmpConfigurationService>();
        builder.Services.AddScoped<OmpBrandingService>();
        builder.Services.AddScoped<RbacService>();
        builder.Services.AddScoped<NotificationService>();
        builder.Services.AddScoped<MessageService>();
        builder.Services.AddScoped<BannerService>();
        builder.Services.AddScoped<OpenModulePlatform.Web.Shared.Navigation.PortalTopBarService>();

        // Application performance telemetry. Registered for every OMP web app and enabled by
        // default: the questions it answers -- what is slow, and how did that change as the
        // installation was taken into use -- need data from the first day, not from the day
        // someone thinks to ask.
        var telemetryOptions = builder.Configuration
            .GetSection($"{optionsSectionName}:{OmpPerformanceTelemetryOptions.SectionName}")
            .Get<OmpPerformanceTelemetryOptions>() ?? new OmpPerformanceTelemetryOptions();
        telemetryOptions.Validate();
        builder.Services.AddSingleton(telemetryOptions);
        builder.Services.AddSingleton<OmpPerformanceTelemetry>();

        if (telemetryOptions.Enabled)
        {
            builder.Services.AddHostedService<OmpPerformanceTelemetryHostedService>();
        }

        return builder;
    }

    public static WebApplicationBuilder AddOmpPushEventDispatcher(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<PushEventDispatcherOptions>(
            builder.Configuration.GetSection(PushEventDispatcherOptions.SectionName));

        var dispatcherOptions = builder.Configuration
            .GetSection(PushEventDispatcherOptions.SectionName)
            .Get<PushEventDispatcherOptions>() ?? new PushEventDispatcherOptions();

        if (dispatcherOptions.Enabled)
        {
            builder.Services.AddSingleton<SqlPushEventOutboxStore>();
            builder.Services.AddHostedService<PushEventDispatcherHostedService>();
        }

        return builder;
    }

    public static IServiceCollection AddOmpCookieAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ConfigureOmpAuthentication(services, configuration);
        return services;
    }

    public static WebApplication UseOmpWebDefaults(
        this WebApplication app,
        string optionsSectionName = WebAppOptions.DefaultSectionName,
        bool mapRazorPages = true)
    {
        var options = app.Configuration
            .GetSection(optionsSectionName)
            .Get<WebAppOptions>() ?? new WebAppOptions();

        var authOptions = app.Configuration
            .GetSection(OmpAuthOptions.SectionName)
            .Get<OmpAuthOptions>() ?? new OmpAuthOptions();

        if (options.UseForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        app.UseOmpSecurityHeaders();

        app.UseOmpRedirectContentLength();

        // Resolve/emit the correlation id and open its logging scope as early as possible,
        // so every log line for the request (including error handling below) carries it.
        app.UseOmpRequestCorrelation();

        // Time the request from just inside the correlation scope, so the measurement
        // covers error handling too -- a page that fails slowly is the one worth knowing
        // about. The middleware is a no-op when telemetry is disabled.
        app.UseOmpPerformanceTelemetry(ResolveTelemetryAppKey(app));

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
        }

        var localizationOptions = app.Services
            .GetRequiredService<IOptions<RequestLocalizationOptions>>()
            .Value;

        app.UseRequestLocalization(localizationOptions);
        app.UseStaticFiles();
        app.MapStaticAssets().ShortCircuit();
        app.UseStatusCodePagesWithReExecute("/status/{0}");

        if (!mapRazorPages)
        {
            app.MapGet("/error", async (
                HttpContext context,
                IStringLocalizer<SharedResource> localizer,
                OmpBrandingService brandingService) =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;

                var branding = await brandingService.GetBrandingAsync(context.RequestAborted);
                var feature = context.Features.Get<IExceptionHandlerPathFeature>();
                var portalHref = OmpUrlPathHelper.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
                var appHomeHref = OmpUrlPathHelper.BuildAppHomeHref(context.Request.PathBase);
                var model = OmpErrorDisplayModelFactory.CreateForStatusCode(
                    StatusCodes.Status500InternalServerError,
                    feature?.Path,
                    portalHref,
                    appHomeHref,
                    localizer,
                    showBackButton: true);

                return Results.Content(
                    BuildFallbackStatusPageHtml(model, branding),
                    contentType: "text/html; charset=utf-8",
                    contentEncoding: System.Text.Encoding.UTF8,
                    statusCode: StatusCodes.Status500InternalServerError);
            }).AllowAnonymous();

            app.MapGet("/status/{statusCode:int}", async (
                HttpContext context,
                int statusCode,
                IStringLocalizer<SharedResource> localizer,
                OmpBrandingService brandingService) =>
            {
                // Clamp before handing the value to Kestrel: a direct request to
                // /status/99 or /status/1000 otherwise threw
                // ArgumentOutOfRangeException from the StatusCode setter and
                // produced a 500 through the exception handler (R4-E7).
                if (statusCode < 400 || statusCode > 599)
                {
                    statusCode = StatusCodes.Status500InternalServerError;
                }

                var branding = await brandingService.GetBrandingAsync(context.RequestAborted);
                var feature = context.Features.Get<IStatusCodeReExecuteFeature>();
                var requestedUrl = feature is null
                    ? context.Request.Path.ToString()
                    : string.Concat(feature.OriginalPathBase, feature.OriginalPath, feature.OriginalQueryString);
                var portalHref = OmpUrlPathHelper.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
                var appHomeHref = OmpUrlPathHelper.BuildAppHomeHref(context.Request.PathBase);
                var model = OmpErrorDisplayModelFactory.CreateForStatusCode(
                    statusCode,
                    requestedUrl,
                    portalHref,
                    appHomeHref,
                    localizer,
                    showBackButton: true);

                return Results.Content(
                    BuildFallbackStatusPageHtml(model, branding),
                    contentType: "text/html; charset=utf-8",
                    contentEncoding: System.Text.Encoding.UTF8,
                    statusCode: statusCode);
            }).AllowAnonymous();
        }

        app.MapGet("/localization/set-language", (
            HttpContext context,
            string culture,
            string? returnUrl,
            CultureSelectionService cultureSelectionService) =>
        {
            var preferredCulture = cultureSelectionService.NormalizePreferredCulture(culture, options);
            var effectiveCulture = cultureSelectionService.ResolveEffectiveCulture(preferredCulture, options);
            cultureSelectionService.ApplyCookies(context.Response, preferredCulture, effectiveCulture);

            if (IsSafeLocalReturnUrl(returnUrl))
            {
                return Results.LocalRedirect(returnUrl!);
            }

            return Results.LocalRedirect("/");
        }).AllowAnonymous();
        // Anonymous: the login page (itself anonymous) renders the shared topbar
        // with a language switcher; without this the fallback policy in apps with
        // AllowAnonymous=false redirected the language GET to the login page, so
        // the language never changed until after sign-in (R4-E6).

        // Both the OmpAuth and RBAC set-active-role paths run the same handler,
        // so register one delegate against both to keep them in lockstep.
        Func<HttpContext, IAntiforgery, RbacService, CancellationToken, Task<IResult>> setActiveRoleHandler =
            async (context, antiforgery, rbac, ct) =>
            {
                try
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    // A stale token (app restart with a new key ring, or a second
                    // tab re-authenticated as another account) used to throw out
                    // of this minimal endpoint — the MVC antiforgery redirect
                    // filter does not apply here — and surface as a generic 500.
                    // Degrade to 400 like the other antiforgery consumers (R4-E4).
                    return Results.BadRequest();
                }

                var selection = await ReadActiveRoleSelectionAsync(context, ct);
                if (!selection.IsValid)
                {
                    return Results.BadRequest();
                }

                return await HandleSetActiveRoleAsync(
                    context,
                    selection.RoleId,
                    selection.ReturnUrl,
                    rbac,
                    options,
                    ct);
            };

        app.MapPost(OmpAuthDefaults.SetActiveRolePath, setActiveRoleHandler).RequireAuthorization();
        app.MapPost(OmpAuthDefaults.RbacSetActiveRolePath, setActiveRoleHandler).RequireAuthorization();

        app.MapGet(PortalTopBarModel.DefaultSessionStatusPath, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            return Results.Json(new
            {
                authenticated = context.User.Identity?.IsAuthenticated == true
            });
        }).AllowAnonymous();

        app.MapPost(PortalTopBarService.ToggleFavoritePath, async (
            HttpContext context,
            PortalTopBarService portalTopBarService,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateTopbarAntiforgeryAsync(context, antiforgery, authOptions))
            {
                return Results.BadRequest();
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(ct);
            var entryKey = form["entryKey"].ToString();
            var appInstanceId = TryParseGuid(form["appInstanceId"].ToString());

            var result = await portalTopBarService.ToggleFavoriteAsync(
                options,
                context.Request,
                context.User,
                entryKey,
                appInstanceId,
                ct);

            if (!result.Success || result.Entry is null)
            {
                return Results.Forbid();
            }

            return Results.Json(new
            {
                isFavorite = result.IsFavorite,
                entryKey = result.Entry.EntryKey,
                appInstanceId = result.Entry.AppInstanceId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty,
                href = result.Entry.Href,
                groupTitle = result.Entry.GroupTitle,
                entryTitle = result.Entry.TextKey,
                label = result.Entry.FavoriteLabel
            });
        }).RequireAuthorization();

        app.MapPost(NotificationService.MarkReadPath, async (
            HttpContext context,
            NotificationService notificationService,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateTopbarAntiforgeryAsync(context, antiforgery, authOptions))
            {
                return Results.BadRequest();
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var userId = NotificationService.TryGetOmpUserId(context.User);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var form = await context.Request.ReadFormAsync(ct);
            if (!long.TryParse(form["notificationId"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var notificationId))
            {
                return Results.BadRequest();
            }

            var result = await notificationService.MarkAsReadAsync(userId.Value, notificationId, ct);
            if (!result.Success)
            {
                return Results.Forbid();
            }

            return Results.Json(new
            {
                success = true,
                notificationId,
                unreadCount = result.UnreadCount,
                destinationUrl = result.DestinationUrl ?? string.Empty
            });
        }).RequireAuthorization();

        app.MapPost(NotificationService.MarkAllReadPath, async (
            HttpContext context,
            NotificationService notificationService,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateTopbarAntiforgeryAsync(context, antiforgery, authOptions))
            {
                return Results.BadRequest();
            }

            var userId = NotificationService.TryGetOmpUserId(context.User);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var returnUrl = "/";
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                var candidateReturnUrl = form["returnUrl"].ToString();
                if (IsSafeLocalReturnUrl(candidateReturnUrl))
                {
                    returnUrl = candidateReturnUrl;
                }
            }

            var markedCount = await notificationService.MarkAllAsReadAsync(userId.Value, ct);
            if (!IsXmlHttpRequest(context.Request))
            {
                return Results.LocalRedirect(returnUrl);
            }

            return Results.Json(new
            {
                success = true,
                markedCount,
                unreadCount = 0
            });
        }).RequireAuthorization();

        app.MapPost(MessageService.MarkAllReadPath, async (
            HttpContext context,
            MessageService messageService,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateTopbarAntiforgeryAsync(context, antiforgery, authOptions))
            {
                return Results.BadRequest();
            }

            var userId = MessageService.TryGetOmpUserId(context.User);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var returnUrl = "/";
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                var candidateReturnUrl = form["returnUrl"].ToString();
                if (IsSafeLocalReturnUrl(candidateReturnUrl))
                {
                    returnUrl = candidateReturnUrl;
                }
            }

            var markedCount = await messageService.MarkAllConversationsReadAsync(userId.Value, ct);
            if (IsXmlHttpRequest(context.Request))
            {
                return Results.Json(new
                {
                    success = true,
                    markedCount,
                    unreadCount = 0
                });
            }

            return Results.LocalRedirect(returnUrl);
        }).RequireAuthorization();

        app.MapGet(PortalTopBarService.SummaryPath, async (
            HttpContext context,
            NotificationService notificationService,
            MessageService messageService,
            IOptions<WebAppOptions> webAppOptions,
            long? afterNotificationId,
            long? afterMessageId,
            CancellationToken ct) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return Results.Unauthorized();
            }

            var userId = NotificationService.TryGetOmpUserId(context.User);
            if (userId is null)
            {
                return Results.Json(new
                {
                    notifications = new { unreadCount = 0, items = Array.Empty<object>(), hasMore = false },
                    messages = new { unreadCount = 0, items = Array.Empty<object>() }
                });
            }

            // Compute the toast summaries first and reuse their unread counts for the
            // badges below: the summary already runs the same unread-count query, so a
            // separate GetUnreadCountAsync/GetUnreadMessageCountAsync per request just
            // opened another connection and counted the same rows a second time (R5-E1).
            var notificationSummary = await notificationService.GetToastSummaryForUserAsync(
                userId.Value,
                afterNotificationId,
                limit: 5,
                ct);
            // Fetch one extra row so hasMore is accurate: an exactly-full page with
            // nothing beyond it must not report hasMore. Only the page size is returned.
            var fetchedNotifications = await notificationService.GetRecentForUserAsync(userId.Value, DefaultNotificationPageSize + 1, ct);
            var notifications = fetchedNotifications.Take(DefaultNotificationPageSize).ToList();
            var portalBaseUrl = webAppOptions.Value.PortalTopBar?.PortalBaseUrl ?? "/";
            var messageSummary = await messageService.GetToastSummaryForUserAsync(
                userId.Value,
                afterMessageId,
                limit: 5,
                ct);
            var messageConversations = await messageService.GetConversationsForUserAsync(
                userId.Value,
                ct,
                limit: DefaultNotificationPageSize);

            var response = new Dictionary<string, object>
            {
                ["notifications"] = new
                {
                    unreadCount = notificationSummary.UnreadCount,
                    items = notifications.Select(row => new
                    {
                        notificationId = row.NotificationId,
                        title = row.Title,
                        content = row.Content,
                        level = row.Level,
                        destinationUrl = row.DestinationUrl ?? string.Empty,
                        callerKey = row.CallerKey ?? string.Empty,
                        callerDisplayName = row.CallerDisplayName ?? string.Empty,
                        callerIcon = SharedAssetUrl(context.Request, row.CallerIcon),
                        createdAt = row.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        isUnread = row.IsUnread
                    }),
                    hasMore = fetchedNotifications.Count > DefaultNotificationPageSize
                },
                ["messages"] = new
                {
                    unreadCount = messageSummary.UnreadCount,
                    items = messageConversations.Select(row => new
                    {
                        conversationId = row.ConversationId,
                        displayTitle = row.DisplayTitle,
                        lastMessagePreview = row.LastMessagePreview ?? string.Empty,
                        lastMessageAt = row.LastMessageAt.HasValue
                            ? row.LastMessageAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                            : string.Empty,
                        unreadCount = row.UnreadCount,
                        avatarUrl = BuildPortalAvatarUrl(portalBaseUrl, row.OtherUserId, row.OtherProfileImageStorageKey) ?? string.Empty,
                        href = BuildConversationHref(portalBaseUrl, row.ConversationId)
                    })
                }
            };

            // Always include the latest ids so first polls without an "after" baseline
            // can initialize their client-side baseline from real values instead of 0.
            response["latestNotificationId"] = notificationSummary.LatestNotificationId;
            response["newNotificationCount"] = notificationSummary.NewNotificationCount;
            response["newNotifications"] = notificationSummary.NewNotifications.Select(row => new
            {
                notificationId = row.NotificationId,
                title = row.Title,
                content = ToToastSnippet(row.Content),
                targetUrl = IsSafeLocalDestination(row.DestinationUrl) ? row.DestinationUrl : "/notifications"
            });

            response["latestMessageId"] = messageSummary.LatestMessageId;
            response["newMessageCount"] = messageSummary.NewMessageCount;
            response["newMessages"] = messageSummary.NewMessages.Select(row => new
            {
                messageId = row.MessageId,
                conversationId = row.ConversationId,
                title = row.Title,
                content = ToToastSnippet(row.Content),
                targetUrl = BuildConversationHref(portalBaseUrl, row.ConversationId)
            });

            return Results.Json(response);
        }).AllowAnonymous();

        app.MapGet(NotificationService.RecentPath, async (
            HttpContext context,
            NotificationService notificationService,
            int? limit,
            string? beforeCreatedAt,
            long? beforeNotificationId,
            CancellationToken ct) =>
        {
            var userId = NotificationService.TryGetOmpUserId(context.User);
            if (userId is null)
            {
                return Results.Forbid();
            }

            DateTime? before = null;
            if (!string.IsNullOrWhiteSpace(beforeCreatedAt)
                && DateTime.TryParse(
                    beforeCreatedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedBefore))
            {
                before = parsedBefore.ToUniversalTime();
            }

            var pageSize = Math.Clamp(
                limit.GetValueOrDefault(DefaultNotificationPageSize),
                1,
                MaxNotificationPageSize);
            // Fetch one extra row so hasMore is accurate for an exactly-full page.
            var fetched = await notificationService.GetRecentForUserAsync(
                userId.Value,
                pageSize + 1,
                before,
                beforeNotificationId,
                ct);
            var rows = fetched.Take(pageSize).ToList();

            return Results.Json(new
            {
                items = rows.Select(row => new
                {
                    notificationId = row.NotificationId,
                    title = row.Title,
                    content = row.Content,
                    level = row.Level,
                    destinationUrl = row.DestinationUrl ?? string.Empty,
                    callerKey = row.CallerKey ?? string.Empty,
                    callerDisplayName = row.CallerDisplayName ?? string.Empty,
                    callerIcon = SharedAssetUrl(context.Request, row.CallerIcon),
                    createdAt = row.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    isUnread = row.IsUnread
                }),
                hasMore = fetched.Count > pageSize
            });
        }).RequireAuthorization();

        // Two entry paths, one hub. ASP.NET Core registers the
        // HubLifetimeManager<TopBarNotificationHub> per hub type (singleton), so
        // both endpoints share the same connections, groups and IHubContext:
        // a server-side push reaches clients regardless of which path they
        // connected through. The topbar connects on Path; module pages using
        // omp-live-refresh connect on PushEventPath.
        app.MapHub<TopBarNotificationHub>(TopBarNotificationHub.Path)
            .RequireAuthorization();

        app.MapHub<TopBarNotificationHub>(TopBarNotificationHub.PushEventPath)
            .RequireAuthorization();

        // Anonymous apps still read the OMP cookie so shared UI can show the current user,
        // roles, favorites, and module navigation without requiring sign-in.
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        if (mapRazorPages)
        {
            app.MapRazorPages();
        }

        return app;
    }

    private static string SharedAssetUrl(HttpRequest request, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("/_content/OpenModulePlatform.Web.Shared/", StringComparison.OrdinalIgnoreCase)
            ? $"{request.PathBase}{trimmed}"
            : trimmed;
    }

    // Single source for the conversation URL so the conversations list and the
    // message toast targets stay in sync with the portal base path.
    private static string BuildConversationHref(string portalBaseUrl, long conversationId)
        => PortalTopBarModelFactory.CombinePortalHref(
            portalBaseUrl,
            $"/messages/{conversationId.ToString(CultureInfo.InvariantCulture)}");

    private static string? BuildPortalAvatarUrl(string portalBaseUrl, int? userId, string? storageKey)
    {
        var avatarPath = OmpAvatarHelper.BuildUserAvatarPath(userId, storageKey);
        return string.IsNullOrWhiteSpace(avatarPath)
            ? null
            : PortalTopBarModelFactory.CombinePortalHref(portalBaseUrl, avatarPath);
    }

    /// <summary>
    /// Redirect responses carry no body. Announce that with an explicit
    /// Content-Length: 0 instead of letting in-process IIS chunk-frame the
    /// empty body; a scanning middlebox that re-frames chunked responses can
    /// otherwise surface the terminating "0" chunk as the whole page, which
    /// users have hit intermittently after POST-redirect flows. Part of
    /// UseOmpWebDefaults; apps that wire their own pipeline call it directly.
    /// </summary>
    /// <summary>
    /// Registers the platform's forwarded-headers policy for an app that builds its own
    /// pipeline instead of going through <see cref="AddOmpWebDefaults{TAppResource}"/>.
    /// </summary>
    /// <remarks>
    /// R5-F6 put the policy inside AddOmpWebDefaults, which every app uses -- except the Auth
    /// app, which hand-rolls its pipeline. The Auth app is also the only consumer of
    /// LoginThrottleService, so the per-client-IP throttle the fix was written for never got
    /// a real client address: behind a reverse proxy the whole organization shared one lockout
    /// bucket and a single attacker could lock everyone out. The fix existed but could not
    /// reach the one component that needed it (R8-INV-8 / R5-F6).
    /// </remarks>
    public static IServiceCollection AddOmpForwardedHeaders(
        this IServiceCollection services,
        WebAppOptions webAppOptions)
    {
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<ILoggerFactory>((options, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(
                    "OpenModulePlatform.Web.Shared.ForwardedHeaders");

                ConfigureForwardedHeaders(options, webAppOptions, logger);
            });

        return services;
    }

    public static IApplicationBuilder UseOmpRedirectContentLength(this IApplicationBuilder app)
    {
        return app.Use(static async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var response = ((HttpContext)state).Response;
                if (response.StatusCode is 301 or 302 or 303 or 307 or 308
                    && response.ContentLength is null
                    && !response.HasStarted)
                {
                    response.ContentLength = 0;
                }

                return Task.CompletedTask;
            }, context);

            await next(context);
        });
    }

    public static IApplicationBuilder UseOmpSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var httpContext = (HttpContext)state;
                var headers = httpContext.Response.Headers;

                SetHeaderIfMissing(headers, "X-Content-Type-Options", "nosniff");
                SetHeaderIfMissing(headers, "Referrer-Policy", "same-origin");
                SetHeaderIfMissing(headers, "X-Frame-Options", "SAMEORIGIN");
                SetHeaderIfMissing(headers, "Permissions-Policy", "camera=(), microphone=(), geolocation=()");

                // HSTS only over HTTPS: a plain-HTTP internal deployment never
                // emits it (and browsers ignore it there anyway), so this is
                // safe without a config knob while adding downgrade protection
                // wherever TLS is used (R3-E10).
                if (httpContext.Request.IsHttps)
                {
                    SetHeaderIfMissing(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
                }

                // OMP still has trusted inline scripts and styles in legacy module pages.
                // Add CSP only after those pages have been migrated to nonce/hash based assets.
                return Task.CompletedTask;
            }, context);

            await next();
        });
    }

    private static void SetHeaderIfMissing(
        IHeaderDictionary headers,
        string headerName,
        string headerValue)
    {
        if (!headers.ContainsKey(headerName))
        {
            headers[headerName] = headerValue;
        }
    }

    private static async Task<IResult> HandleSetActiveRoleAsync(
        HttpContext context,
        int? roleId,
        string? returnUrl,
        RbacService rbac,
        WebAppOptions options,
        CancellationToken ct)
    {
        var roleContext = await rbac.GetUserRoleContextAsync(context.User, ct);
        var validRoleIds = roleContext.AvailableRoles.Select(x => x.RoleId).ToHashSet();

        if (roleId is int selectedRoleId && validRoleIds.Contains(selectedRoleId))
        {
            context.Response.Cookies.Append(
                ActiveRoleCookie.CookieName,
                selectedRoleId.ToString(CultureInfo.InvariantCulture),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    // Follow the connection instead of hardcoding Secure: on a
                    // plain-HTTP deployment (the sameAsRequest auth-cookie case)
                    // a Secure cookie is silently dropped, so role switching
                    // no-opped with no error (R4-E5).
                    Secure = context.Request.IsHttps,
                    Path = "/"
                });
        }
        else
        {
            ActiveRoleCookie.Clear(context.Response);
        }

        // Keep role switching predictable across Portal, Razor Pages modules, and Blazor modules.
        // The return URL is local-only; page authorization after the redirect decides whether the
        // newly selected role can remain on that page or should see the normal access-denied flow.
        if (IsSafeLocalReturnUrl(returnUrl))
        {
            return Results.LocalRedirect(returnUrl!);
        }

        var safePortalHref = OmpUrlPathHelper.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
        if (Uri.IsWellFormedUriString(safePortalHref, UriKind.Absolute))
        {
            return Results.Redirect(safePortalHref);
        }

        return Results.LocalRedirect(safePortalHref);
    }

    private static async Task<ActiveRoleSelection> ReadActiveRoleSelectionAsync(
        HttpContext context,
        CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
        {
            return ActiveRoleSelection.Invalid;
        }

        var form = await context.Request.ReadFormAsync(ct);
        var roleIdValue = form["roleId"].ToString();
        int? roleId = null;
        if (!string.IsNullOrWhiteSpace(roleIdValue))
        {
            if (!int.TryParse(roleIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRoleId))
            {
                return ActiveRoleSelection.Invalid;
            }

            roleId = parsedRoleId;
        }

        return new ActiveRoleSelection(true, roleId, form["returnUrl"].ToString());
    }

    private readonly record struct ActiveRoleSelection(bool IsValid, int? RoleId, string? ReturnUrl)
    {
        public static ActiveRoleSelection Invalid { get; } = new(false, null, null);
    }

    /// <summary>
    /// Applies at-rest encryption to the shared Data Protection key ring.
    /// Priority order (documented in docs/HOST_AGENT.md):
    /// 1. A set <c>OmpAuth:DpapiNgProtectionDescriptor</c> wins over everything:
    ///    the ring is protected with CNG DPAPI-NG, which is AD-backed and
    ///    decryptable on every domain-joined node by the descriptor principals
    ///    (the web-farm and multi-account answer when Active Directory exists).
    ///    An invalid descriptor throws at startup — never a silent fallback to
    ///    another scope. A descriptor that cannot take effect at all (no key
    ///    path resolved, or a non-Windows host) also throws at startup instead
    ///    of being silently ignored: see <see cref="ThrowIfDescriptorCannotTakeEffect"/>.
    /// 2. Otherwise a set <c>OmpAuth:DataProtectionCertificateThumbprint</c>
    ///    protects the ring with that X.509 certificate (ProtectKeysWithCertificate):
    ///    the web-farm answer WITHOUT Active Directory — the same certificate,
    ///    with its private key, is installed in LocalMachine\My on every node.
    ///    A missing certificate, a missing private key, or an expired/not-yet-valid
    ///    certificate throws at startup; retired certificates listed in
    ///    <c>OmpAuth:DataProtectionRetiredCertificateThumbprints</c> keep older
    ///    key files decryptable through UnprotectKeysWithAnyCertificate during a
    ///    certificate rotation.
    ///    Setting BOTH a descriptor and a certificate thumbprint is a startup
    ///    error (see <see cref="ThrowIfKeyProtectionModesConflict"/>): the
    ///    platform refuses to guess which at-rest mode the operator meant.
    /// 3. Otherwise <c>OmpAuth:ProtectKeysWithDpapi=false</c> disables all
    ///    at-rest encryption, as before R3-E8.
    /// 4. Otherwise legacy DPAPI in the configured scope: machine scope by
    ///    default, because OMP app pools may deliberately run as different
    ///    accounts (e.g. a printer-proxy identity) and a current-user-protected
    ///    key ring locks the shared cookie to the creating account so every
    ///    other pool loops on /auth/login.
    /// </summary>
    internal static void ApplyDataProtectionKeyProtection(
        IDataProtectionBuilder dataProtectionBuilder,
        OmpAuthOptions authOptions,
        Func<string, X509Certificate2?>? certificateResolver = null)
    {
        ThrowIfKeyProtectionModesConflict(authOptions);

        if (!string.IsNullOrWhiteSpace(authOptions.DpapiNgProtectionDescriptor))
        {
            var descriptor = authOptions.DpapiNgProtectionDescriptor.Trim();
            DpapiNgProtectionDescriptorValidator.ThrowIfInvalid(descriptor);
            dataProtectionBuilder.ProtectKeysWithDpapiNG(
                descriptor,
                flags: DpapiNGProtectionDescriptorFlags.None);
            return;
        }

        if (!string.IsNullOrWhiteSpace(authOptions.DataProtectionCertificateThumbprint))
        {
            var certificate = LoadKeyProtectionCertificate(
                authOptions.DataProtectionCertificateThumbprint,
                certificateResolver,
                isRetiredCertificate: false);
            dataProtectionBuilder.ProtectKeysWithCertificate(certificate);

            var retiredCertificates = new List<X509Certificate2>();
            foreach (var retiredThumbprint in authOptions.DataProtectionRetiredCertificateThumbprints)
            {
                if (string.IsNullOrWhiteSpace(retiredThumbprint))
                {
                    continue;
                }

                retiredCertificates.Add(LoadKeyProtectionCertificate(
                    retiredThumbprint,
                    certificateResolver,
                    isRetiredCertificate: true));
            }

            if (retiredCertificates.Count > 0)
            {
                dataProtectionBuilder.UnprotectKeysWithAnyCertificate(retiredCertificates.ToArray());
            }

            return;
        }

        if (!authOptions.ProtectKeysWithDpapi)
        {
            return;
        }

        dataProtectionBuilder.ProtectKeysWithDpapi(
            protectToLocalMachine: authOptions.DpapiProtectToLocalMachine);
    }

    /// <summary>
    /// Fails startup loudly when both supported encryption-at-rest modes are
    /// configured at once. DPAPI-NG and X.509 certificate protection solve the
    /// same problem in different trust models (AD-backed vs certificate-backed);
    /// applying whichever happens to come first would encrypt new keys to a
    /// mode the operator may not have intended, so the platform refuses to
    /// guess: the operator picks one.
    /// </summary>
    internal static void ThrowIfKeyProtectionModesConflict(OmpAuthOptions authOptions)
    {
        if (!string.IsNullOrWhiteSpace(authOptions.DpapiNgProtectionDescriptor)
            && !string.IsNullOrWhiteSpace(authOptions.DataProtectionCertificateThumbprint))
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DpapiNgProtectionDescriptor)} and " +
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)} are both set. " +
                "They are two different encryption-at-rest modes for the Data Protection key ring " +
                "(AD-backed DPAPI-NG vs an X.509 certificate), and the platform does not guess which " +
                "one was intended. Remove one of the settings and restart the application.");
        }

        if (string.IsNullOrWhiteSpace(authOptions.DataProtectionCertificateThumbprint)
            && authOptions.DataProtectionRetiredCertificateThumbprints.Any(t => !string.IsNullOrWhiteSpace(t)))
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionRetiredCertificateThumbprints)} is set " +
                $"but OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)} is empty. " +
                "Retired certificates only keep older key files readable while certificate " +
                "protection is active; without an active certificate thumbprint the list would be " +
                "silently ignored — and an explicitly set protection option is never silently " +
                "ignored. Set the active thumbprint, or remove the retired list, and restart the " +
                "application.");
        }
    }

    /// <summary>
    /// Fails startup loudly when <c>OmpAuth:DataProtectionCertificateThumbprint</c>
    /// is set but cannot take effect — same fail-loud philosophy as
    /// <see cref="ThrowIfDescriptorCannotTakeEffect"/>: an explicitly set
    /// protection option is never silently ignored.
    /// </summary>
    internal static void ThrowIfCertificateProtectionCannotTakeEffect(
        OmpAuthOptions authOptions,
        string? dataProtectionKeyPath,
        bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(authOptions.DataProtectionCertificateThumbprint))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(dataProtectionKeyPath))
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)} is set but " +
                "no data-protection key path could be resolved: OmpAuth:DataProtectionKeyPath is " +
                "empty and no content-root fallback applies. Certificate protection encrypts the " +
                "persisted key ring at rest, so without a key path the certificate cannot take " +
                "effect — and an explicitly set protection option is never silently ignored. Set " +
                "OmpAuth:DataProtectionKeyPath to the shared key directory, or remove the " +
                "certificate setting, and restart the application.");
        }

        if (!isWindows)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)} is set but " +
                "this platform only applies key-ring encryption at rest on Windows, so the " +
                "certificate cannot take effect on this host — and an explicitly set protection " +
                "option is never silently ignored. Run the application on Windows, or remove the " +
                "certificate setting, and restart the application.");
        }
    }

    /// <summary>
    /// Resolves a key-protection certificate by thumbprint from the
    /// LocalMachine\My store and validates that it can actually protect the key
    /// ring: it must exist, hold a private key, and (for the active
    /// certificate) be within its validity period. Every failure throws with a
    /// message naming the thumbprint — a silently skipped certificate would
    /// leave the ring unprotected, or old keys unreadable, while the operator
    /// believes certificate protection is active.
    /// </summary>
    /// <remarks>
    /// Retired certificates (decrypt-only during rotation) are exempt from the
    /// validity-period check: a rotation often happens BECAUSE the old
    /// certificate expired, and decrypting with its private key stays
    /// cryptographically valid after expiry.
    /// </remarks>
    internal static X509Certificate2 LoadKeyProtectionCertificate(
        string thumbprint,
        Func<string, X509Certificate2?>? certificateResolver,
        bool isRetiredCertificate)
    {
        var normalizedThumbprint = NormalizeCertificateThumbprint(thumbprint);
        var role = isRetiredCertificate ? "retired " : "";

        if (normalizedThumbprint.Length != 40 || !normalizedThumbprint.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: " +
                $"\"{thumbprint}\" is not a valid SHA-1 certificate thumbprint (40 hexadecimal " +
                $"characters after whitespace removal) for the {role}certificate lookup. Fix the " +
                "thumbprint and restart the application. The key ring does NOT silently fall back " +
                "to another protection scope.");
        }

        X509Certificate2? certificate;
        try
        {
            certificate = certificateResolver is not null
                ? certificateResolver(normalizedThumbprint)
                : FindInLocalMachineMyStore(normalizedThumbprint);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: the {role}" +
                $"certificate with thumbprint {normalizedThumbprint} could not be loaded from " +
                $"LocalMachine\\My: {ex.Message} The key ring does NOT silently fall back to " +
                "another protection scope: install the certificate (with its private key) or fix " +
                "the thumbprint, and restart the application.", ex);
        }

        if (certificate is null)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: no {role}" +
                $"certificate with thumbprint {normalizedThumbprint} exists in LocalMachine\\My. " +
                "Install the certificate (with its private key) into the local machine's Personal " +
                "store on every node, or fix the thumbprint, and restart the application. The key " +
                "ring does NOT silently fall back to another protection scope.");
        }

        ThrowIfCertificateCannotProtectKeys(certificate, normalizedThumbprint, isRetiredCertificate);
        return certificate;
    }

    /// <summary>
    /// Validates a resolved certificate for key-ring protection. Public keys
    /// alone cannot decrypt (and a private key is required for both encrypt and
    /// decrypt of the ring in practice), so a missing private key, or an
    /// active certificate outside its validity period, is a startup error.
    /// </summary>
    internal static void ThrowIfCertificateCannotProtectKeys(
        X509Certificate2 certificate,
        string normalizedThumbprint,
        bool isRetiredCertificate)
    {
        var role = isRetiredCertificate ? "retired " : "";

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: the {role}" +
                $"certificate with thumbprint {normalizedThumbprint} was found in " +
                "LocalMachine\\My but has NO private key accessible to this process. Grant the " +
                "app-pool identity read access to the certificate's private key (Manage private " +
                "keys in certlm.msc), or install the PFX with the private key, and restart the " +
                "application. The key ring does NOT silently fall back to another protection scope.");
        }

        if (certificate.GetRSAPrivateKey() is null)
        {
            // The framework's certificate encryptor uses RSA key transport
            // (rsa-1_5 in the persisted key XML), so an EC/DSA certificate
            // would not fail until the first key write. Fail at startup
            // instead, with the actual requirement named.
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: the {role}" +
                $"certificate with thumbprint {normalizedThumbprint} does not hold an RSA private " +
                "key. ASP.NET Core Data Protection encrypts the key ring with RSA key transport, " +
                "so the certificate must be RSA. Issue an RSA certificate, update the thumbprint, " +
                "and restart the application. The key ring does NOT silently fall back to another " +
                "protection scope.");
        }

        if (isRetiredCertificate)
        {
            // Expiry is accepted for retired certificates by design: see the
            // remarks on LoadKeyProtectionCertificate.
            return;
        }

        var now = DateTime.Now; // NotBefore/NotAfter are returned in local time.
        if (now < certificate.NotBefore)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: the " +
                $"certificate with thumbprint {normalizedThumbprint} is not valid yet " +
                $"(NotBefore {certificate.NotBefore:u}). Install the correct certificate or fix " +
                "the thumbprint, and restart the application. The key ring does NOT silently " +
                "fall back to another protection scope.");
        }

        if (now > certificate.NotAfter)
        {
            throw new InvalidOperationException(
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)}: the " +
                $"certificate with thumbprint {normalizedThumbprint} EXPIRED at " +
                $"{certificate.NotAfter:u}. A ring that silently kept encrypting to an expired " +
                "certificate would surface later as an audit/compliance finding, so startup fails " +
                "loudly instead. Issue and install the successor certificate, point " +
                $"OmpAuth:{nameof(OmpAuthOptions.DataProtectionCertificateThumbprint)} at it, and " +
                $"move this thumbprint to OmpAuth:{nameof(OmpAuthOptions.DataProtectionRetiredCertificateThumbprints)} " +
                "until no key file is still encrypted to it. Then restart the application.");
        }
    }

    private static string NormalizeCertificateThumbprint(string thumbprint)
    {
        // Thumbprints are often pasted from certmgr/certlm with spaces and in
        // mixed case; normalize before the store lookup.
        return string.Concat(thumbprint.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }

    private static X509Certificate2? FindInLocalMachineMyStore(string normalizedThumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        // validOnly: false — expiry is checked separately with a clearer message.
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            normalizedThumbprint,
            validOnly: false);

        return matches.Count == 0 ? null : matches[0];
    }

    /// <summary>
    /// Fails startup loudly when <c>OmpAuth:DpapiNgProtectionDescriptor</c> is
    /// set but cannot take effect. An explicitly set protection option that is
    /// silently ignored would leave the ring unprotected (or protected to a
    /// single node) while the operator believes DPAPI-NG is active — the same
    /// class of config error as an invalid descriptor, so it throws a clear
    /// <see cref="InvalidOperationException"/> instead of skipping.
    /// </summary>
    internal static void ThrowIfDescriptorCannotTakeEffect(
        OmpAuthOptions authOptions,
        string? dataProtectionKeyPath,
        bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(authOptions.DpapiNgProtectionDescriptor))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(dataProtectionKeyPath))
        {
            throw new InvalidOperationException(
                "OmpAuth:DpapiNgProtectionDescriptor is set but no data-protection key path " +
                "could be resolved: OmpAuth:DataProtectionKeyPath is empty and no " +
                "content-root fallback applies. DPAPI-NG protects the persisted key ring at " +
                "rest, so without a key path the descriptor cannot take effect — and an " +
                "explicitly set protection option is never silently ignored. Set " +
                "OmpAuth:DataProtectionKeyPath to the shared key directory, or remove the " +
                "descriptor setting, and restart the application.");
        }

        if (!isWindows)
        {
            throw new InvalidOperationException(
                "OmpAuth:DpapiNgProtectionDescriptor is set but CNG DPAPI-NG is only " +
                "available on Windows, so the descriptor cannot take effect on this host — " +
                "and an explicitly set protection option is never silently ignored. Run the " +
                "application on Windows, or remove the descriptor setting, and restart the " +
                "application.");
        }
    }

    private static void ConfigureOmpAuthentication(
        IServiceCollection services,
        IConfiguration configuration,
        string? contentRootPath = null)
    {
        var authOptions = configuration
            .GetSection(OmpAuthOptions.SectionName)
            .Get<OmpAuthOptions>() ?? new OmpAuthOptions();

        services.AddSingleton<IValidateOptions<OmpAuthOptions>, OmpAuthOptionsValidator>();

        services.AddOptions<OmpAuthOptions>()
            .Bind(configuration.GetSection(OmpAuthOptions.SectionName))
            .ValidateOnStart();

        // R7-F10: session revocation checkpoint. Every request carrying the
        // shared cookie re-validates the session against omp.users (account
        // still active, security stamp still current), so a disabled account or
        // a changed password ends the session instead of letting the cookie
        // live until it expires. Registered here so every application that
        // accepts the shared cookie gets the check, including the Auth app
        // which builds its pipeline by hand.
        services.AddMemoryCache();
        services.TryAddSingleton(configuration);
        services.TryAddSingleton<SqlConnectionFactory>();
        services.TryAddScoped<OmpConfigurationService>();
        services.TryAddScoped<IOmpSessionRevocationStore, OmpSqlSessionRevocationStore>();
        services.TryAddScoped<OmpSessionRevocationValidator>();

        var dataProtectionBuilder = services
            .AddDataProtection()
            .SetApplicationName(string.IsNullOrWhiteSpace(authOptions.ApplicationName)
                ? "OpenModulePlatform"
                : authOptions.ApplicationName);

        var dataProtectionKeyPath = ResolveDataProtectionKeyPath(
            authOptions.DataProtectionKeyPath,
            contentRootPath);

        // Explicitly set protection options that cannot take effect (no key
        // path resolved, a non-Windows host, or two conflicting modes) are
        // config errors: fail loudly instead of silently running an
        // unprotected or wrong-scope ring.
        ThrowIfKeyProtectionModesConflict(authOptions);
        ThrowIfDescriptorCannotTakeEffect(
            authOptions,
            dataProtectionKeyPath,
            OperatingSystem.IsWindows());
        ThrowIfCertificateProtectionCannotTakeEffect(
            authOptions,
            dataProtectionKeyPath,
            OperatingSystem.IsWindows());

        if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
        {
            dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

            // At-rest encryption of the key ring is OPT-IN since 2026-08-23: a reader
            // of the key directory can forge auth cookies for every OMP app (R3-E8),
            // so when nothing is configured the directory permissions are the whole
            // control. DPAPI and DPAPI-NG are Windows-only; elsewhere the ACL is the
            // protection either way.
            if (OperatingSystem.IsWindows())
            {
                ApplyDataProtectionKeyProtection(dataProtectionBuilder, authOptions);

                // Say it out loud at startup. A security trade that is invisible in
                // operation is one nobody remembers to close, and this one is meant to
                // end when the AD security group exists. Logged through the options
                // pipeline because no logger exists this early -- same shape as
                // AddOmpForwardedHeaders above.
                if (string.IsNullOrWhiteSpace(authOptions.DpapiNgProtectionDescriptor)
                    && string.IsNullOrWhiteSpace(authOptions.DataProtectionCertificateThumbprint)
                    && !authOptions.ProtectKeysWithDpapi)
                {
                    var keyPathForLog = dataProtectionKeyPath;
                    services.AddOptions<KeyManagementOptions>()
                        .Configure<ILoggerFactory>((_, loggerFactory) =>
                            loggerFactory
                                .CreateLogger("OpenModulePlatform.Web.Shared.DataProtection")
                                .LogWarning(
                                    "Data Protection key ring at {KeyPath} is NOT encrypted at rest. " +
                                    "Anyone who can read that directory can forge authentication cookies " +
                                    "for every OMP app sharing it, so its NTFS permissions are the only " +
                                    "control: grant read access to the app-pool identities only and keep " +
                                    "it off any file share. To encrypt again, set " +
                                    "OmpAuth:DpapiNgProtectionDescriptor to SID=<AD group SID> (AD-backed, " +
                                    "works across domain-joined nodes) or " +
                                    "OmpAuth:DataProtectionCertificateThumbprint to an X.509 certificate " +
                                    "installed in LocalMachine\\My on every node (no AD required) - not " +
                                    "OmpAuth:ProtectKeysWithDpapi, which ties the ring to this host.",
                                    keyPathForLog));
                }
            }
        }

        services.AddAuthentication(OmpAuthDefaults.AuthenticationScheme)
            .AddCookie(OmpAuthDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = string.IsNullOrWhiteSpace(authOptions.CookieName)
                    ? OmpAuthDefaults.CookieName
                    : authOptions.CookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Secure by default (R3-E5): SameAsRequest let a single HTTP
                // request issue the session cookie without the Secure flag.
                options.Cookie.SecurePolicy = string.Equals(authOptions.CookieSecurePolicy?.Trim(), "sameAsRequest", StringComparison.OrdinalIgnoreCase)
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.LoginPath = NormalizeLocalAuthPath(authOptions.LoginPath, OmpAuthDefaults.LoginPath);
                options.AccessDeniedPath = NormalizeLocalAuthPath(authOptions.AccessDeniedPath, OmpAuthDefaults.AccessDeniedPath);
                // R7-F10: the session lifetime is absolute. Sign-in stamps
                // ExpiresUtc from the configured per-provider lifetime and no
                // request may move it. With sliding renewal enabled, any
                // activity pushed that instant forward forever, so a disabled
                // employee's cookie lived as long as they kept clicking.
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(10);

                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        var loginPath = NormalizeLocalAuthPath(authOptions.LoginPath, OmpAuthDefaults.LoginPath);
                        var returnUrl = string.Concat(
                            context.Request.PathBase,
                            context.Request.Path,
                            context.Request.QueryString);
                        var safeReturnUrl = IsSafeLocalReturnUrl(returnUrl) ? returnUrl : "/";
                        context.Response.Redirect(BuildLoginRedirectUrl(loginPath, safeReturnUrl));
                        return Task.CompletedTask;
                    },
                    OnValidatePrincipal = context =>
                    {
                        var validator = context.HttpContext.RequestServices
                            .GetRequiredService<OmpSessionRevocationValidator>();
                        return validator.ValidateAsync(context);
                    }
                };
            });
    }

    private static string ResolveDataProtectionKeyPath(
        string? configuredPath,
        string? contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        if (string.IsNullOrWhiteSpace(contentRootPath))
        {
            return string.Empty;
        }

        try
        {
            var contentRoot = new DirectoryInfo(Path.GetFullPath(contentRootPath.Trim()));
            var webAppsRoot = contentRoot.Parent;
            var runtimeRoot = webAppsRoot?.Parent;
            if (webAppsRoot is not null
                && runtimeRoot is not null
                && string.Equals(webAppsRoot.Name, "WebApps", StringComparison.OrdinalIgnoreCase))
            {
                // Host Agent writes shared web-app keys beside the WebApps
                // root. This fallback keeps older module configs that lack
                // OmpAuth from silently using an app-local ephemeral key ring.
                return Path.Join(runtimeRoot.FullName, "DataProtectionKeys");
            }
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeLocalAuthPath(string? configuredPath, string fallbackPath)
    {
        var fallback = IsSafeLocalReturnUrl(fallbackPath)
            ? fallbackPath
            : "/";
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return fallback;
        }

        var candidate = configuredPath.Trim();
        return IsSafeLocalReturnUrl(candidate)
            && !candidate.Contains('?', StringComparison.Ordinal)
            && !candidate.Contains('#', StringComparison.Ordinal)
            ? candidate
            : fallback;
    }

    private static string BuildLoginRedirectUrl(string? configuredLoginPath, string returnUrl)
    {
        var loginPath = NormalizeLocalAuthPath(configuredLoginPath, OmpAuthDefaults.LoginPath);
        return QueryHelpers.AddQueryString(loginPath, "returnUrl", returnUrl);
    }

    private static bool IsSafeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var unescaped = Uri.UnescapeDataString(returnUrl);
            return !unescaped.StartsWith("//", StringComparison.Ordinal)
                && !unescaped.Contains('\\', StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsSafeLocalDestination(string? destinationUrl)
        => !string.IsNullOrWhiteSpace(destinationUrl)
           && destinationUrl.StartsWith("/", StringComparison.Ordinal)
           && !destinationUrl.StartsWith("//", StringComparison.Ordinal)
           && !destinationUrl.Contains('\\', StringComparison.Ordinal);

    private static string ToToastSnippet(string value)
    {
        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= 180
            ? normalized
            : string.Concat(normalized.AsSpan(0, 177), "...");
    }

    private static Guid? TryParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;

    /// <summary>
    /// Applies the forwarded-header trust model defined in configuration.
    /// </summary>
    /// <remarks>
    /// Trusting all proxies is convenient during development but unsafe for internet-facing
    /// deployments unless a trusted reverse proxy is guaranteed in front of the application.
    /// </remarks>
    private static void ConfigureForwardedHeaders(
        ForwardedHeadersOptions options,
        WebAppOptions webAppOptions,
        ILogger logger)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;

        // Honor X-Forwarded-For too, but ONLY when the operator has declared a
        // trusted proxy in front (known proxies/networks, or explicit trust-all).
        // Without X-Forwarded-For, every client behind a reverse proxy collapses to
        // the proxy's IP, so the per-client-IP login throttle (R4-F3) shares a
        // single bucket across the whole organization and one attacker locks
        // everyone out (R5-F6). The ForwardedHeaders middleware only applies XFF
        // when the immediate peer is a configured known proxy, so with no proxy
        // declared we keep XFF off and a direct client still cannot spoof its
        // address (preserving the R5S non-spoofable property).
        var trustsConfiguredProxy = webAppOptions.ForwardedHeadersTrustAllProxies
            || webAppOptions.ForwardedHeadersKnownProxies.Length > 0
            || webAppOptions.ForwardedHeadersKnownNetworks.Length > 0;
        if (trustsConfiguredProxy)
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedFor;
        }

        if (webAppOptions.ForwardedHeadersTrustAllProxies)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            logger.LogWarning(
                "Forwarded headers are configured to trust all proxies. " +
                "Only use this setting when a trusted reverse proxy is guaranteed.");

            return;
        }

        if (webAppOptions.ForwardedHeadersKnownProxies.Length > 0)
        {
            options.KnownProxies.Clear();

            foreach (var ipText in webAppOptions.ForwardedHeadersKnownProxies
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (IPAddress.TryParse(ipText.Trim(), out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
                else
                {
                    logger.LogWarning(
                        "Skipped invalid forwarded-header proxy IP '{ProxyIp}'.",
                        ipText);
                }
            }
        }

        if (webAppOptions.ForwardedHeadersKnownNetworks.Length > 0)
        {
            options.KnownIPNetworks.Clear();

            foreach (var cidr in webAppOptions.ForwardedHeadersKnownNetworks
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (TryParseCidrNetwork(cidr, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                }
                else
                {
                    logger.LogWarning(
                        "Skipped invalid forwarded-header network '{NetworkCidr}'.",
                        cidr);
                }
            }
        }
    }

    private static string BuildFallbackStatusPageHtml(OmpErrorDisplayModel model, OmpBranding branding)
    {
        var safeTitle = WebUtility.HtmlEncode(model.Title);
        var safeMessage = WebUtility.HtmlEncode(model.Message);
        var safeRequestedUrlLabel = WebUtility.HtmlEncode(model.RequestedUrlLabel ?? string.Empty);
        var safeRequestedUrl = WebUtility.HtmlEncode(model.RequestedUrl ?? string.Empty);
        var safePortalHref = WebUtility.HtmlEncode(model.PortalHref ?? "/");
        var safePortalText = WebUtility.HtmlEncode(model.PortalText ?? string.Empty);
        var safeAppHomeHref = WebUtility.HtmlEncode(model.AppHomeHref ?? string.Empty);
        var safeAppHomeText = WebUtility.HtmlEncode(model.AppHomeText ?? string.Empty);
        var safeBackText = WebUtility.HtmlEncode(model.BackText ?? string.Empty);
        var requestedUrlMarkup = string.IsNullOrWhiteSpace(model.RequestedUrl)
            ? string.Empty
            : $"<p class='omp-error-view__detail'><strong>{safeRequestedUrlLabel}:</strong> <code>{safeRequestedUrl}</code></p>";
        var backButtonMarkup = model.ShowBackButton && !string.IsNullOrWhiteSpace(model.BackText)
            ? $"<button type='button' class='omp-error-view__button omp-error-view__button--secondary' onclick='history.back()'>{safeBackText}</button>"
            : string.Empty;
        var appHomeMarkup = string.IsNullOrWhiteSpace(model.AppHomeHref) || string.IsNullOrWhiteSpace(model.AppHomeText)
            ? string.Empty
            : $"<a class='omp-error-view__button omp-error-view__button--secondary' href='{safeAppHomeHref}'>{safeAppHomeText}</a>";
        var portalMarkup = string.IsNullOrWhiteSpace(model.PortalHref) || string.IsNullOrWhiteSpace(model.PortalText)
            ? string.Empty
            : $"<a class='omp-error-view__button omp-error-view__button--primary' href='{safePortalHref}'>{safePortalText}</a>";
        var safeCulture = WebUtility.HtmlEncode(CultureInfo.CurrentUICulture.Name);
        var safePlatformName = WebUtility.HtmlEncode(branding.PlatformName);

        return $$"""
<!doctype html>
<html lang="{{safeCulture}}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{safeTitle}} - {{safePlatformName}}</title>
  <!--
    NOTE: These fallback styles intentionally mirror omp-error-view.css so this page remains fully styled
    even if static assets are unavailable during error handling. Keep this block and omp-error-view.css
    synchronized whenever styles are changed.
  -->
  <style>
    :root { color-scheme: light dark; }
    body { margin: 0; font-family: Arial, Helvetica, sans-serif; background: #f5f7fa; color: #16202a; }
    .omp-error-fallback { min-height: 100vh; display: grid; place-items: center; padding: 2rem; }
    .omp-error-view { width: min(46rem, 100%); margin: 0 auto; padding: 2rem; border: 1px solid #d7dde5; border-radius: 18px; background: #fff; box-shadow: 0 12px 36px rgba(15, 23, 42, 0.08); }
    .omp-error-view__code { display: inline-block; margin-bottom: 1rem; font-size: 0.875rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; color: #3559e0; }
    .omp-error-view__title { margin: 0 0 0.75rem; font-size: 1.9rem; line-height: 1.2; }
    .omp-error-view__message { margin: 0 0 1rem; line-height: 1.6; color: #223044; }
    .omp-error-view__detail { margin: 0; color: #4b5563; word-break: break-word; }
    .omp-error-view__detail code { display: inline-block; margin-top: 0.35rem; padding: 0.2rem 0.45rem; border-radius: 6px; background: #eef2ff; color: #1f2937; }
    .omp-error-view__actions { display: flex; flex-wrap: wrap; gap: 0.75rem; margin-top: 1.5rem; }
    .omp-error-view__button { display: inline-flex; align-items: center; justify-content: center; min-height: 2.75rem; padding: 0.75rem 1rem; border: 1px solid transparent; border-radius: 999px; font: inherit; font-weight: 600; text-decoration: none; cursor: pointer; }
    .omp-error-view__button--primary { background: #3559e0; border-color: #3559e0; color: #fff; }
    .omp-error-view__button--primary:hover, .omp-error-view__button--primary:focus, .omp-error-view__button--primary:focus-visible { background: #2948bc; border-color: #2948bc; color: #fff; text-decoration: none; }
    .omp-error-view__button--secondary { background: #fff; border-color: #c8d1dc; color: #223044; }
    .omp-error-view__button--secondary:hover, .omp-error-view__button--secondary:focus, .omp-error-view__button--secondary:focus-visible { background: #f5f7fa; }
  </style>
</head>
<body>
  <main class="omp-error-fallback">
    <section class="omp-error-view">
      <div class="omp-error-view__code">{{model.StatusCode}}</div>
      <h1 class="omp-error-view__title">{{safeTitle}}</h1>
      <p class="omp-error-view__message">{{safeMessage}}</p>
      {{requestedUrlMarkup}}
      <div class="omp-error-view__actions">
        {{backButtonMarkup}}
        {{appHomeMarkup}}
        {{portalMarkup}}
      </div>
    </section>
  </main>
</body>
</html>
""";
    }

    private static bool TryParseCidrNetwork(
        string? cidr,
        out SystemNetIPNetwork network)
    {
        network = default;

        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var parts = cidr.Trim().Split(
            '/',
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var prefix))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maxBits = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128;

        if (prefixLength < 0 || prefixLength > maxBits)
        {
            return false;
        }

        network = new SystemNetIPNetwork(prefix, prefixLength);
        return true;
    }

    // R3-E2: the topbar POSTs are CSRF-protected by SameSite=Lax on the auth
    // cookie, and the rendered and JS-built topbar forms always carry an
    // antiforgery token. Validating that token server-side is opt-in because
    // it rejects POSTs from pages rendered before the tokens existed.
    private static async Task<bool> ValidateTopbarAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        OmpAuthOptions authOptions)
    {
        if (!authOptions.ValidateTopbarAntiforgery)
        {
            return true;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static bool IsXmlHttpRequest(HttpRequest request)
        => string.Equals(
            request.Headers["X-Requested-With"].ToString(),
            "XMLHttpRequest",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name a web app is recorded under in the performance telemetry.
    /// </summary>
    /// <remarks>
    /// The assembly name rather than the configured title: it is stable across a rename in
    /// configuration, and a metric series whose identity changes when someone edits a
    /// display string is a series nobody can plot across a whole autumn.
    /// </remarks>
    private static string ResolveTelemetryAppKey(WebApplication app)
    {
        var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return string.IsNullOrWhiteSpace(assemblyName)
            ? app.Environment.ApplicationName
            : assemblyName;
    }
}
