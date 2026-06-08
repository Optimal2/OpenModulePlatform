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
