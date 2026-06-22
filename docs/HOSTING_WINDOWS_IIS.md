# Hosting OMP on Windows and IIS

## Baseline

The public OMP repository is now aligned on `.NET 10` for active development.

Use the latest supported `.NET 10 SDK` on development machines and the matching hosting components on Windows servers.

## Development machines

Install the current `.NET 10 SDK` before restoring, building, or publishing OMP projects.

Official download:
- https://dotnet.microsoft.com/en-us/download/dotnet/10.0

The repository also pins a .NET 10 SDK line in `global.json`.

## IIS-hosted OMP web applications

For IIS-hosted OMP web applications, install the current `.NET Hosting Bundle` on the target Windows server.

Why this matters:
- it installs the .NET runtime used by framework-dependent deployments
- it installs the ASP.NET Core Module used by IIS-hosted ASP.NET Core applications

Official guidance:
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/tutorials/publish-to-iis?view=aspnetcore-10.0

## web.config

OMP web projects use the `Microsoft.NET.Sdk.Web` SDK.

For IIS deployments, `web.config` must exist at the application content root. The ASP.NET Core Module reads it to configure the application under IIS.

Official guidance:
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/web-config?view=aspnetcore-10.0

## URL Rewrite

The IIS URL Rewrite module is **optional**.

It is useful when you need IIS-level rewrite or redirect rules, reverse-proxy style path handling, canonical URL rules, or other server-side URL transformations. It is not a universal prerequisite for every OMP deployment.

Official download:
- https://www.iis.net/downloads/microsoft/url-rewrite

## Load-balanced IIS deployments

When multiple IIS servers serve the same OMP DNS name, configure every Portal
and module web application with the same `OmpAuth:DataProtectionKeyPath`. Use a
shared folder that every relevant application pool identity can read and write.
Without shared Data Protection keys, an auth cookie created on one node cannot
be decrypted on another node, which commonly shows up as repeated redirects to
the login flow.

If TLS is terminated by a load balancer or reverse proxy, enable forwarded
headers and configure the trusted proxy IPs or networks. HostAgent-generated
web-app `appsettings.json` files inherit `WebAppUseForwardedHeaders` and the
matching `WebAppForwardedHeaders*` settings from the HostAgent configuration.

Load-balancer health checks should be scoped to the application route they are
protecting. Do not treat one unhealthy module web application as a reason to
drain the whole IIS node unless that is the intended operational policy for the
environment.

Portal exposes these anonymous health endpoints:

- `/health/live` returns HTTP 200 when the Portal process can answer requests.
- `/health/ready` returns HTTP 200 only when Portal can also reach the OMP
  database with a small SQL probe. It returns HTTP 503 when Portal cannot serve
  real traffic safely.

Use `/health/ready` for Portal-specific routing decisions. If a deployment
fronts several applications through the same DNS name, use equivalent
application-specific checks for those applications instead of a single
site-wide node check.

## Blazor Server behind IIS and load balancers

OMP Blazor Server modules use ASP.NET Core SignalR for interactive circuits.
Keep the default SignalR transport negotiation enabled unless a module has a
specific, tested reason to override it. The normal order is WebSockets first,
then fallback transports such as Server-Sent Events or long polling when the
browser, IIS, proxy, or load balancer cannot establish WebSockets.

Fallback transports are a resilience mechanism, not a replacement for correct
hosting configuration. Long polling in particular increases request volume and
still needs the same authenticated session, routing, forwarded-header, and
timeout behavior as WebSockets. If an environment disables or intermittently
breaks WebSockets, users may see Blazor reconnect UI and short interruptions
even when the application eventually recovers.

Use this checklist for IIS-hosted Blazor Server modules:

- Install the IIS WebSocket Protocol feature on every IIS node that serves
  Blazor Server modules.
- Enable WebSockets through any reverse proxy or load balancer in front of IIS.
- Keep ARR or load-balancer cookie persistence enabled for Blazor Server
  routes. A reconnecting circuit must be routed back to the server that owns
  that circuit unless the application has explicitly been designed and tested
  for another topology.
- Share ASP.NET Core Data Protection keys across all nodes that serve the same
  OMP Portal or module DNS name.
- When TLS terminates before Kestrel/IIS, enable `UseForwardedHeaders` and set
  the trusted proxy IPs or networks so generated HTTPS URLs, redirects, and
  cookie security decisions use the public request scheme and host.
- Keep OMP authentication and role cookies same-site for same-origin Portal and
  module hosting. If a deployment intentionally embeds an OMP module cross-site,
  review the affected cookies and browser policy separately; cross-site iframe
  scenarios normally require `SameSite=None`, `Secure`, and matching frame/CSP
  policy.
- Keep iframe use same-origin where possible. The shared OMP security headers
  emit `X-Frame-Options: SAMEORIGIN` by default.
- Set load-balancer idle timeouts above the module's SignalR keep-alive and
  client-timeout values. Do not use a timeout that silently closes idle
  WebSocket connections before the app can detect and reconnect.
- Scope health checks to a lightweight application route. Do not point load
  balancer health checks at Blazor circuit endpoints such as `_blazor`.

Blazor modules should provide a clear reconnect UI when interrupted. Only tune
SignalR keep-alive, client timeout, disconnected-circuit retention, or retry
intervals after validating the actual IIS/proxy/load-balancer timeout behavior.

## Recommended OMP deployment posture on Windows

For the current public repository, the recommended baseline is:

- develop on `.NET 10`
- publish web applications with the current Web SDK defaults
- install the current `.NET Hosting Bundle` on IIS servers
- use URL Rewrite only when the deployment topology requires rewrite rules
- keep `web.config` in the deployed application root

## Related repository files

- `global.json`
- `Directory.Packages.props`
- `OpenModulePlatform.Portal/Properties/PublishProfiles/`
- `examples/WebAppModule/Properties/PublishProfiles/`
- `examples/WebAppBlazorModule/Properties/PublishProfiles/`
- `examples/ServiceAppModule/WebApp/Properties/PublishProfiles/`
- `examples/WorkerAppModule/WebApp/Properties/PublishProfiles/`
