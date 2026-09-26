using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// The Portal as app-under-test: the shared process fixture pointed at this
/// repo's Portal project. Override OMP_UITESTS_DB to scan against another
/// database.
/// </summary>
public sealed class PortalAppFixture : WebAppProcessFixture
{
    protected override string SolutionFileName => "OpenModulePlatform.slnx";
    protected override string WebProjectName => "OpenModulePlatform.Portal";

    // The Portal binds its web-app options from the "Portal" section, not
    // "WebApp", so the shared WebApp__AllowAnonymous the base fixture sets
    // has no effect here.
    protected override IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string> { ["Portal__AllowAnonymous"] = "true" };
}

/// <summary>
/// The Auth app as app-under-test. Auth has no "/" route, so the login page
/// is the readiness probe.
/// </summary>
public sealed class AuthAppFixture : WebAppProcessFixture
{
    protected override string SolutionFileName => "OpenModulePlatform.slnx";
    protected override string WebProjectName => "OpenModulePlatform.Auth";
    protected override string ReadinessPath => "/login";
}

/// <summary>
/// The iFrame web app module as app-under-test (campaign csp-sista-undantagen):
/// its script-src dropped 'unsafe-inline', so Index and Standalone must render
/// with no executable inline scripts and no CSP violations. Same "Portal"
/// options section as the Portal, hence the same AllowAnonymous override.
/// </summary>
public sealed class IFrameAppFixture : WebAppProcessFixture
{
    protected override string SolutionFileName => "OpenModulePlatform.slnx";
    protected override string WebProjectName => "OpenModulePlatform.Web.iFrameWebAppModule";

    protected override IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string> { ["Portal__AllowAnonymous"] = "true" };
}

/// <summary>
/// The example web app module as app-under-test: its <c>/widgets/overview</c> page is the
/// <c>module-fragment</c> dashboard widget the Portal embeds, so the widget test boots the
/// module directly (anonymous, like the Portal and iFrame fixtures) and asserts the fragment
/// renders with only allowed elements and relative links.
/// </summary>
public sealed class ExampleWebAppModuleFixture : WebAppProcessFixture
{
    protected override string SolutionFileName => "OpenModulePlatform.slnx";
    protected override string WebProjectName => "OpenModulePlatform.Web.ExampleWebAppModule";
    protected override string WebProjectDirectory => "examples/WebAppModule/WebApp";

    protected override IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string> { ["Portal__AllowAnonymous"] = "true" };
}

/// <summary>
/// The example web app module with authentication required: boots without the anonymous
/// override so the fragment endpoint answers 401 for a cookie-less request. The readiness
/// probe is a static asset (served before Razor auth), because every Razor page of the
/// module requires a signed-in user.
/// </summary>
public sealed class ExampleWebAppModuleAuthRequiredFixture : WebAppProcessFixture
{
    protected override string SolutionFileName => "OpenModulePlatform.slnx";
    protected override string WebProjectName => "OpenModulePlatform.Web.ExampleWebAppModule";
    protected override string WebProjectDirectory => "examples/WebAppModule/WebApp";
    protected override string ReadinessPath => "/css/site.css";

    protected override IReadOnlyDictionary<string, string> ExtraEnvironment { get; } =
        new Dictionary<string, string> { ["Portal__AllowAnonymous"] = "false" };
}

[CollectionDefinition("ui")]
public sealed class UiCollection :
    ICollectionFixture<PlaywrightSessionFixture>,
    ICollectionFixture<PortalAppFixture>,
    ICollectionFixture<AuthAppFixture>,
    ICollectionFixture<IFrameAppFixture>,
    ICollectionFixture<ExampleWebAppModuleFixture>,
    ICollectionFixture<ExampleWebAppModuleAuthRequiredFixture>;
